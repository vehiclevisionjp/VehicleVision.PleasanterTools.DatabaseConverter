using System.Data.Common;
using Npgsql;
using NpgsqlTypes;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Planning;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Schema;

namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.Databases;

/// <summary>PostgreSQL の方言（Npgsql）。</summary>
public sealed class PostgreSqlDialect : IDatabaseDialect
{
    public Dbms Dbms => Dbms.PostgreSql;

    // Npgsql のパラメータ上限は 65535。行の大きさでも区切るので控えめにする
    public int MaxParametersPerStatement => 30000;

    public string TableExistsSql =>
        "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @name";

    private const string ColumnsSql = """
        SELECT c.table_name, c.column_name, c.ordinal_position,
               CASE WHEN c.is_nullable = 'YES' THEN 1 ELSE 0 END,
               c.data_type, c.udt_name,
               c.character_maximum_length::bigint, c.numeric_precision::int,
               c.numeric_scale::int, c.datetime_precision::int,
               CASE WHEN c.column_default IS NULL THEN 0 ELSE 1 END,
               CASE WHEN c.is_identity = 'YES' OR c.column_default LIKE 'nextval(%' THEN 1 ELSE 0 END,
               CASE WHEN c.identity_generation = 'ALWAYS' THEN 1 ELSE 0 END,
               CASE WHEN c.is_generated = 'ALWAYS' THEN 1 ELSE 0 END
        FROM information_schema.columns c
        INNER JOIN information_schema.tables t
                ON t.table_schema = c.table_schema AND t.table_name = c.table_name
        WHERE t.table_type = 'BASE TABLE' AND c.table_schema = current_schema()
        ORDER BY c.table_name, c.ordinal_position
        """;

    private const string PrimaryKeysSql = """
        SELECT k.table_name, k.column_name
        FROM information_schema.table_constraints tc
        INNER JOIN information_schema.key_column_usage k
                ON k.constraint_schema = tc.constraint_schema AND k.constraint_name = tc.constraint_name
               AND k.table_schema = tc.table_schema AND k.table_name = tc.table_name
        WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema = current_schema()
        ORDER BY k.table_name, k.ordinal_position
        """;

    // 外部キーの親子（情報スキーマの同等のビューは、所有者以外には見えないことがあるのでカタログを読む）
    private const string ForeignKeysSql = """
        SELECT child.relname, parent.relname
        FROM pg_catalog.pg_constraint c
        INNER JOIN pg_catalog.pg_class child ON child.oid = c.conrelid
        INNER JOIN pg_catalog.pg_class parent ON parent.oid = c.confrelid
        INNER JOIN pg_catalog.pg_namespace n ON n.oid = child.relnamespace
        WHERE c.contype = 'f' AND n.nspname = current_schema()
        """;

    public string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public DbConnection CreateConnection(string connectionString, int commandTimeoutSeconds)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrEmpty(builder.ApplicationName))
            {
                builder.ApplicationName = "DatabaseConverter";
            }

            return new NpgsqlConnection(builder.ConnectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new ConnectionStringException("the PostgreSQL connection string is invalid");
        }
    }

    // 変換元は、セッションの既定を読み取り専用にして、書き込みを DB 側でも拒否させる
    public IReadOnlyList<string> SessionStatements(bool readOnlyRole)
        => readOnlyRole ? ["SET default_transaction_read_only = on"] : [];

    public IReadOnlyList<string> OptionalSessionStatements() => [];

    public Task<SchemaInfo> ReadSchemaAsync(DbConnection connection, int commandTimeoutSeconds, CancellationToken cancellationToken)
        => DbHelper.ReadSchemaAsync(this, connection, commandTimeoutSeconds, ColumnsSql, PrimaryKeysSql, ForeignKeysSql, cancellationToken);

    public string BuildCountSql(string table) => $"SELECT COUNT(*) FROM {QuoteIdentifier(table)}";

    public string BuildSelectSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> orderBy)
        => DbHelper.BuildSelect(this, table, columns, orderBy);

    public string BuildInsertSql(string table, IReadOnlyList<ColumnInfo> columns, int rowCount, bool overridingSystemValue)
        => DbHelper.BuildInsert(this, table, columns, rowCount, overridingSystemValue ? "OVERRIDING SYSTEM VALUE" : null);

    public DbParameter CreateParameter(string name, ColumnType targetType, object? value)
    {
        var parameter = new NpgsqlParameter { ParameterName = name };
        switch (targetType.Kind)
        {
            case ValueKind.Boolean:
                parameter.NpgsqlDbType = NpgsqlDbType.Boolean;
                parameter.Value = value ?? DBNull.Value;
                break;
            case ValueKind.Integer:
                if (targetType.IntegerMax <= short.MaxValue)
                {
                    parameter.NpgsqlDbType = NpgsqlDbType.Smallint;
                    parameter.Value = value is null ? DBNull.Value : (short)(long)value;
                }
                else if (targetType.IntegerMax <= int.MaxValue)
                {
                    parameter.NpgsqlDbType = NpgsqlDbType.Integer;
                    parameter.Value = value is null ? DBNull.Value : (int)(long)value;
                }
                else
                {
                    parameter.NpgsqlDbType = NpgsqlDbType.Bigint;
                    parameter.Value = value ?? DBNull.Value;
                }

                break;
            case ValueKind.Decimal:
                parameter.NpgsqlDbType = NpgsqlDbType.Numeric;
                parameter.Value = value ?? DBNull.Value;
                break;
            case ValueKind.Float:
                if (targetType.IsSingle)
                {
                    parameter.NpgsqlDbType = NpgsqlDbType.Real;
                    parameter.Value = value is null ? DBNull.Value : (float)(double)value;
                }
                else
                {
                    parameter.NpgsqlDbType = NpgsqlDbType.Double;
                    parameter.Value = value ?? DBNull.Value;
                }

                break;
            case ValueKind.String:
                parameter.NpgsqlDbType = NpgsqlDbType.Text;
                parameter.Value = value ?? DBNull.Value;
                break;
            case ValueKind.Binary:
                parameter.NpgsqlDbType = NpgsqlDbType.Bytea;
                parameter.Value = value ?? DBNull.Value;
                break;
            case ValueKind.DateTime:
                // timestamp（時間帯なし）。Kind が Unspecified の値だけを渡す
                parameter.NpgsqlDbType = NpgsqlDbType.Timestamp;
                parameter.Value = value ?? DBNull.Value;
                break;
            case ValueKind.Guid:
                parameter.NpgsqlDbType = NpgsqlDbType.Uuid;
                parameter.Value = value ?? DBNull.Value;
                break;
            default:
                throw new NotSupportedException($"type '{targetType.NativeName}' is not supported");
        }

        return parameter;
    }

    public string BuildCreateProgressTableSql(string table)
        => $"CREATE TABLE IF NOT EXISTS {QuoteIdentifier(table)} (\"TableName\" varchar(256) NOT NULL PRIMARY KEY, " +
           "\"CopiedRows\" bigint NOT NULL, \"CompletedAt\" timestamp(3) NOT NULL, \"ToolVersion\" varchar(64) NOT NULL)";

    private async Task<string?> FindSequenceAsync(DbConnection connection, int timeout, string table, string column, CancellationToken ct)
    {
        // 第 1 引数は引用符つきの表名として解釈される。第 2 引数は列名そのもの
        var result = await DbHelper.ScalarAsync(
            connection,
            "SELECT pg_get_serial_sequence(@table, @column)",
            timeout,
            ct,
            ("@table", QuoteIdentifier(table)),
            ("@column", column)).ConfigureAwait(false);
        return result as string;
    }

    public async Task<long?> ReadNextAutoIncrementAsync(
        DbConnection connection, int timeout, string table, string column, CancellationToken cancellationToken)
    {
        var sequence = await FindSequenceAsync(connection, timeout, table, column, cancellationToken).ConfigureAwait(false);
        if (sequence is null)
        {
            return null;
        }

        // sequence 名は pg_get_serial_sequence が引用符つきで返したもの（DB が作った識別子）
        await using var command = DbHelper.CreateCommand(connection, $"SELECT last_value, is_called FROM {sequence}", timeout);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var last = System.Convert.ToInt64(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
        var called = System.Convert.ToBoolean(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture);
        return called ? last + 1 : last;
    }

    public async Task<long?> SetNextAutoIncrementAsync(
        DbConnection connection, int timeout, string table, string column, long? minimumNext, CancellationToken cancellationToken)
    {
        var max = DbHelper.ToNullableLong(await DbHelper.ScalarAsync(
            connection,
            $"SELECT MAX({QuoteIdentifier(column)}) FROM {QuoteIdentifier(table)}",
            timeout,
            cancellationToken).ConfigureAwait(false));
        if (max is null)
        {
            return null;
        }

        var sequence = await FindSequenceAsync(connection, timeout, table, column, cancellationToken).ConfigureAwait(false);
        if (sequence is null)
        {
            return null;
        }

        var next = Math.Max(minimumNext ?? 0, max.Value + 1);

        // is_called = false: 次の nextval が、ちょうど next を返す
        await using var command = DbHelper.CreateCommand(connection, "SELECT setval(CAST(@sequence AS regclass), @next, false)", timeout);
        command.Parameters.Add(new NpgsqlParameter("@sequence", NpgsqlDbType.Text) { Value = sequence });
        command.Parameters.Add(new NpgsqlParameter("@next", NpgsqlDbType.Bigint) { Value = next });
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return next;
    }

    public Task BeginTableAsync(DbConnection connection, int timeout, TablePlan table, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task EndTableAsync(DbConnection connection, int timeout, TablePlan table, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public async Task<IConstraintScope> DisableConstraintsAsync(
        DbConnection connection, int timeout, IReadOnlyList<string> tables, CancellationToken cancellationToken)
    {
        // スーパーユーザー権限が要る。外部キーとトリガが働かなくなる（接続の間だけ）
        await DbHelper.ExecuteAsync(connection, "SET session_replication_role = 'replica'", timeout, cancellationToken).ConfigureAwait(false);
        return new StatementConstraintScope(
            connection,
            timeout,
            ["SET session_replication_role = DEFAULT"],
            "foreign keys and triggers are not enforced on the target while copying (session_replication_role = replica); referential integrity is not re-checked afterwards");
    }
}
