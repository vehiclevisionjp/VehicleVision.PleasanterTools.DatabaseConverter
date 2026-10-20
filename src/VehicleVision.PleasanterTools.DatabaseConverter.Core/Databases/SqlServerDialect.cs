using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Planning;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Schema;

namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.Databases;

/// <summary>SQL Server の方言（Microsoft.Data.SqlClient）。</summary>
public sealed class SqlServerDialect : IDatabaseDialect
{
    // 1 つの INSERT 文のパラメータは 2100 個まで。余裕を持たせる
    public int MaxParametersPerStatement => 2000;

    public Dbms Dbms => Dbms.SqlServer;

    public string TableExistsSql =>
        "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = SCHEMA_NAME() AND TABLE_NAME = @name";

    // 情報スキーマの共通形（14 列）。IDENTITY と計算列・行バージョンは COLUMNPROPERTY / 型名で判定する
    private const string ColumnsSql = """
        SELECT c.TABLE_NAME, c.COLUMN_NAME, c.ORDINAL_POSITION,
               CASE WHEN c.IS_NULLABLE = 'YES' THEN 1 ELSE 0 END,
               LOWER(c.DATA_TYPE), LOWER(c.DATA_TYPE),
               CAST(c.CHARACTER_MAXIMUM_LENGTH AS bigint), CAST(c.NUMERIC_PRECISION AS int),
               CAST(c.NUMERIC_SCALE AS int), CAST(c.DATETIME_PRECISION AS int),
               CASE WHEN c.COLUMN_DEFAULT IS NULL THEN 0 ELSE 1 END,
               ISNULL(CAST(COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + N'.' + QUOTENAME(c.TABLE_NAME)), c.COLUMN_NAME, 'IsIdentity') AS int), 0),
               0,
               CASE WHEN ISNULL(COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + N'.' + QUOTENAME(c.TABLE_NAME)), c.COLUMN_NAME, 'IsComputed'), 0) = 1
                         OR LOWER(c.DATA_TYPE) = 'timestamp' THEN 1 ELSE 0 END
        FROM INFORMATION_SCHEMA.COLUMNS c
        INNER JOIN INFORMATION_SCHEMA.TABLES t
                ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
        WHERE t.TABLE_TYPE = 'BASE TABLE' AND c.TABLE_SCHEMA = SCHEMA_NAME()
        ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION
        """;

    private const string PrimaryKeysSql = """
        SELECT k.TABLE_NAME, k.COLUMN_NAME
        FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
        INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
                ON k.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA AND k.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
               AND k.TABLE_SCHEMA = tc.TABLE_SCHEMA AND k.TABLE_NAME = tc.TABLE_NAME
        WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY' AND tc.TABLE_SCHEMA = SCHEMA_NAME()
        ORDER BY k.TABLE_NAME, k.ORDINAL_POSITION
        """;

    private const string ForeignKeysSql = """
        SELECT OBJECT_NAME(fk.parent_object_id), OBJECT_NAME(fk.referenced_object_id)
        FROM sys.foreign_keys fk
        WHERE SCHEMA_NAME(fk.schema_id) = SCHEMA_NAME()
        """;

    public string QuoteIdentifier(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public DbConnection CreateConnection(string connectionString, int commandTimeoutSeconds)
    {
        try
        {
            return new SqlConnection(new SqlConnectionStringBuilder(connectionString).ConnectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            // 例外のメッセージには接続文字列の断片が入りうるので使わない
            throw new ConnectionStringException("the SQL Server connection string is invalid");
        }
    }

    // SQL Server には読み取り専用のセッションが無い。変換元へは SELECT しか発行しない
    public IReadOnlyList<string> SessionStatements(bool readOnlyRole) => [];

    public IReadOnlyList<string> OptionalSessionStatements() => [];

    public Task<SchemaInfo> ReadSchemaAsync(DbConnection connection, int commandTimeoutSeconds, CancellationToken cancellationToken)
        => DbHelper.ReadSchemaAsync(this, connection, commandTimeoutSeconds, ColumnsSql, PrimaryKeysSql, ForeignKeysSql, cancellationToken);

    public string BuildCountSql(string table) => $"SELECT COUNT_BIG(*) FROM {QuoteIdentifier(table)}";

    public string BuildSelectSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> orderBy)
        => DbHelper.BuildSelect(this, table, columns, orderBy);

    public string BuildInsertSql(string table, IReadOnlyList<ColumnInfo> columns, int rowCount, bool overridingSystemValue)
        => DbHelper.BuildInsert(this, table, columns, rowCount, null);

    public DbParameter CreateParameter(string name, ColumnType targetType, object? value)
    {
        var parameter = new SqlParameter { ParameterName = name };
        switch (targetType.Kind)
        {
            case ValueKind.Boolean:
                parameter.SqlDbType = SqlDbType.Bit;
                parameter.Value = value ?? DBNull.Value;
                break;
            case ValueKind.Integer:
                if (targetType.IntegerMax <= byte.MaxValue)
                {
                    parameter.SqlDbType = SqlDbType.TinyInt;
                    parameter.Value = value is null ? DBNull.Value : (byte)(long)value;
                }
                else if (targetType.IntegerMax <= short.MaxValue)
                {
                    parameter.SqlDbType = SqlDbType.SmallInt;
                    parameter.Value = value is null ? DBNull.Value : (short)(long)value;
                }
                else if (targetType.IntegerMax <= int.MaxValue)
                {
                    parameter.SqlDbType = SqlDbType.Int;
                    parameter.Value = value is null ? DBNull.Value : (int)(long)value;
                }
                else
                {
                    parameter.SqlDbType = SqlDbType.BigInt;
                    parameter.Value = value ?? DBNull.Value;
                }

                break;
            case ValueKind.Decimal:
                if (targetType.NativeName is "money" or "smallmoney")
                {
                    parameter.SqlDbType = targetType.NativeName == "money" ? SqlDbType.Money : SqlDbType.SmallMoney;
                }
                else
                {
                    parameter.SqlDbType = SqlDbType.Decimal;
                    if (targetType.Precision > 0)
                    {
                        parameter.Precision = (byte)targetType.Precision;
                        parameter.Scale = (byte)targetType.Scale;
                    }
                    else
                    {
                        parameter.Precision = 38;
                        parameter.Scale = 10;
                    }
                }

                parameter.Value = value ?? DBNull.Value;
                break;
            case ValueKind.Float:
                if (targetType.IsSingle)
                {
                    parameter.SqlDbType = SqlDbType.Real;
                    parameter.Value = value is null ? DBNull.Value : (float)(double)value;
                }
                else
                {
                    parameter.SqlDbType = SqlDbType.Float;
                    parameter.Value = value ?? DBNull.Value;
                }

                break;
            case ValueKind.String:
            {
                var text = value as string;
                var unicode = targetType.IsUnicode;
                parameter.SqlDbType = unicode ? SqlDbType.NVarChar : SqlDbType.VarChar;
                var limit = unicode ? 4000 : 8000;
                parameter.Size = text is null ? 1 : (text.Length > limit ? -1 : Math.Max(1, text.Length));
                parameter.Value = (object?)text ?? DBNull.Value;
                break;
            }

            case ValueKind.Binary:
            {
                var bytes = value as byte[];
                parameter.SqlDbType = SqlDbType.VarBinary;
                parameter.Size = bytes is null ? 1 : (bytes.Length > 8000 ? -1 : Math.Max(1, bytes.Length));
                parameter.Value = (object?)bytes ?? DBNull.Value;
                break;
            }

            case ValueKind.DateTime:
                if (targetType.IsLegacyDateTime)
                {
                    parameter.SqlDbType = SqlDbType.DateTime;
                }
                else
                {
                    parameter.SqlDbType = SqlDbType.DateTime2;
                    parameter.Scale = (byte)Math.Clamp(targetType.FractionDigits, 0, 7);
                }

                parameter.Value = value ?? DBNull.Value;
                break;
            case ValueKind.Guid:
                parameter.SqlDbType = SqlDbType.UniqueIdentifier;
                parameter.Value = value ?? DBNull.Value;
                break;
            default:
                throw new NotSupportedException($"type '{targetType.NativeName}' is not supported");
        }

        return parameter;
    }

    public string BuildCreateProgressTableSql(string table)
    {
        var q = QuoteIdentifier(table);
        return $"IF OBJECT_ID(N'{q.Replace("'", "''", StringComparison.Ordinal)}', N'U') IS NULL " +
               $"CREATE TABLE {q} ([TableName] nvarchar(256) NOT NULL PRIMARY KEY, [CopiedRows] bigint NOT NULL, " +
               "[CompletedAt] datetime2(3) NOT NULL, [ToolVersion] nvarchar(64) NOT NULL)";
    }

    public async Task<long?> ReadNextAutoIncrementAsync(
        DbConnection connection, int timeout, string table, string column, CancellationToken cancellationToken)
    {
        await using var command = DbHelper.CreateCommand(
            connection,
            """
            SELECT CAST(ic.last_value AS bigint), CAST(ic.seed_value AS bigint), CAST(ic.increment_value AS bigint)
            FROM sys.identity_columns ic
            WHERE ic.object_id = OBJECT_ID(@table) AND ic.name = @column
            """,
            timeout);
        DbHelper.AddParameter(command, "@table", QuoteIdentifier(table));
        DbHelper.AddParameter(command, "@column", column);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var last = DbHelper.ToNullableLong(reader.GetValue(0));
        var seed = DbHelper.ToNullableLong(reader.GetValue(1)) ?? 1;
        var increment = DbHelper.ToNullableLong(reader.GetValue(2)) ?? 1;
        return last is null ? seed : last + increment;
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

        var next = Math.Max(minimumNext ?? 0, max.Value + 1);

        // RESEED は「現在値」を指定する。次に払い出されるのは現在値 + 増分（Pleasanter の IDENTITY は増分 1）
        var literal = QuoteIdentifier(table).Replace("'", "''", StringComparison.Ordinal);
        await DbHelper.ExecuteAsync(
            connection,
            $"DBCC CHECKIDENT (N'{literal}', RESEED, {(next - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}) WITH NO_INFOMSGS",
            timeout,
            cancellationToken).ConfigureAwait(false);
        return next;
    }

    public async Task BeginTableAsync(DbConnection connection, int timeout, TablePlan table, CancellationToken cancellationToken)
    {
        if (table.AutoIncrementIsMapped)
        {
            await DbHelper.ExecuteAsync(connection, $"SET IDENTITY_INSERT {QuoteIdentifier(table.Target!.Name)} ON", timeout, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task EndTableAsync(DbConnection connection, int timeout, TablePlan table, CancellationToken cancellationToken)
    {
        if (table.AutoIncrementIsMapped && connection.State == ConnectionState.Open)
        {
            await DbHelper.ExecuteAsync(connection, $"SET IDENTITY_INSERT {QuoteIdentifier(table.Target!.Name)} OFF", timeout, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<IConstraintScope> DisableConstraintsAsync(
        DbConnection connection, int timeout, IReadOnlyList<string> tables, CancellationToken cancellationToken)
    {
        var restore = new List<string>();
        foreach (var table in tables)
        {
            var q = QuoteIdentifier(table);
            await DbHelper.ExecuteAsync(connection, $"ALTER TABLE {q} NOCHECK CONSTRAINT ALL", timeout, cancellationToken).ConfigureAwait(false);
            // WITH CHECK で既存の行も検査し、制約を「信頼された」状態へ戻す
            restore.Add($"ALTER TABLE {q} WITH CHECK CHECK CONSTRAINT ALL");
        }

        return new StatementConstraintScope(connection, timeout, restore, null);
    }
}
