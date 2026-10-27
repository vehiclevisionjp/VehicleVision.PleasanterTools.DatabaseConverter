using System.Data.Common;
using System.Globalization;
using MySqlConnector;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Planning;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Schema;

namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.Databases;

/// <summary>MySQL の方言（MySqlConnector）。</summary>
public sealed class MySqlDialect : IDatabaseDialect
{
    public Dbms Dbms => Dbms.MySql;

    public int MaxParametersPerStatement => 30000;

    public string TableExistsSql =>
        "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @name";

    // 生成列は EXTRA の 'VIRTUAL GENERATED' / 'STORED GENERATED'（'DEFAULT_GENERATED' は式の既定値で、生成列ではない）
    private const string ColumnsSql = """
        SELECT c.TABLE_NAME, c.COLUMN_NAME, c.ORDINAL_POSITION,
               CASE WHEN c.IS_NULLABLE = 'YES' THEN 1 ELSE 0 END,
               LOWER(c.DATA_TYPE), LOWER(c.COLUMN_TYPE),
               c.CHARACTER_MAXIMUM_LENGTH, c.NUMERIC_PRECISION,
               c.NUMERIC_SCALE, c.DATETIME_PRECISION,
               CASE WHEN c.COLUMN_DEFAULT IS NULL THEN 0 ELSE 1 END,
               CASE WHEN c.EXTRA LIKE '%auto_increment%' THEN 1 ELSE 0 END,
               0,
               CASE WHEN c.EXTRA LIKE '%VIRTUAL GENERATED%' OR c.EXTRA LIKE '%STORED GENERATED%' THEN 1 ELSE 0 END
        FROM information_schema.COLUMNS c
        INNER JOIN information_schema.TABLES t
                ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
        WHERE t.TABLE_TYPE = 'BASE TABLE' AND c.TABLE_SCHEMA = DATABASE()
        ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION
        """;

    private const string PrimaryKeysSql = """
        SELECT k.TABLE_NAME, k.COLUMN_NAME
        FROM information_schema.TABLE_CONSTRAINTS tc
        INNER JOIN information_schema.KEY_COLUMN_USAGE k
                ON k.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA AND k.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
               AND k.TABLE_SCHEMA = tc.TABLE_SCHEMA AND k.TABLE_NAME = tc.TABLE_NAME
        WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY' AND tc.TABLE_SCHEMA = DATABASE()
        ORDER BY k.TABLE_NAME, k.ORDINAL_POSITION
        """;

    private const string ForeignKeysSql = """
        SELECT rc.TABLE_NAME, rc.REFERENCED_TABLE_NAME
        FROM information_schema.REFERENTIAL_CONSTRAINTS rc
        WHERE rc.CONSTRAINT_SCHEMA = DATABASE()
        """;

    public string QuoteIdentifier(string identifier) => "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";

    public DbConnection CreateConnection(string connectionString, int commandTimeoutSeconds)
    {
        try
        {
            var builder = new MySqlConnectionStringBuilder(connectionString)
            {
                // CHAR(36) を Guid ではなく文字列として扱う（値の型を、列の型どおりにそろえる）
                GuidFormat = MySqlGuidFormat.None,
                ConvertZeroDateTime = false,
                AllowUserVariables = false,
                TreatTinyAsBoolean = true,
            };
            if (string.IsNullOrEmpty(builder.CharacterSet))
            {
                builder.CharacterSet = "utf8mb4";
            }

            return new MySqlConnection(builder.ConnectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new ConnectionStringException("the MySQL connection string is invalid");
        }
    }

    // 変換先: 範囲外や長すぎる値を黙って丸めず、0 を AUTO_INCREMENT の自動採番と解釈しない。
    // NO_BACKSLASH_ESCAPES は入れない（ドライバの文字列エスケープの前提と食い違うため）
    public IReadOnlyList<string> SessionStatements(bool readOnlyRole)
        => readOnlyRole
            ? ["SET SESSION TRANSACTION READ ONLY"]
            : ["SET SESSION sql_mode = 'STRICT_ALL_TABLES,NO_AUTO_VALUE_ON_ZERO,NO_ZERO_DATE,NO_ZERO_IN_DATE'"];

    // MySQL 8 は表の統計（AUTO_INCREMENT）を既定で 24 時間キャッシュする。0 にして最新を読む（5.7 には無い変数）
    public IReadOnlyList<string> OptionalSessionStatements() => ["SET SESSION information_schema_stats_expiry = 0"];

    public Task<SchemaInfo> ReadSchemaAsync(DbConnection connection, int commandTimeoutSeconds, CancellationToken cancellationToken)
        => DbHelper.ReadSchemaAsync(this, connection, commandTimeoutSeconds, ColumnsSql, PrimaryKeysSql, ForeignKeysSql, cancellationToken);

    public string BuildCountSql(string table) => $"SELECT COUNT(*) FROM {QuoteIdentifier(table)}";

    public string BuildSelectSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> orderBy)
        => DbHelper.BuildSelect(this, table, columns, orderBy);

    public string BuildInsertSql(string table, IReadOnlyList<ColumnInfo> columns, int rowCount, bool overridingSystemValue)
        => DbHelper.BuildInsert(this, table, columns, rowCount, null);

    public DbParameter CreateParameter(string name, ColumnType targetType, object? value)
    {
        var parameter = new MySqlParameter { ParameterName = name };
        switch (targetType.Kind)
        {
            case ValueKind.Boolean:
                parameter.MySqlDbType = MySqlDbType.Bool;
                parameter.Value = value ?? DBNull.Value;
                break;
            case ValueKind.Integer:
                parameter.MySqlDbType = MySqlDbType.Int64;
                parameter.Value = value ?? DBNull.Value;
                break;
            case ValueKind.Decimal:
                parameter.MySqlDbType = MySqlDbType.NewDecimal;
                parameter.Value = value ?? DBNull.Value;
                break;
            case ValueKind.Float:
                if (targetType.IsSingle)
                {
                    parameter.MySqlDbType = MySqlDbType.Float;
                    parameter.Value = value is null ? DBNull.Value : (float)(double)value;
                }
                else
                {
                    parameter.MySqlDbType = MySqlDbType.Double;
                    parameter.Value = value ?? DBNull.Value;
                }

                break;
            case ValueKind.String:
                parameter.MySqlDbType = MySqlDbType.VarChar;
                parameter.Value = value ?? DBNull.Value;
                break;
            case ValueKind.Binary:
                parameter.MySqlDbType = MySqlDbType.LongBlob;
                parameter.Value = value ?? DBNull.Value;
                break;
            case ValueKind.DateTime:
                parameter.MySqlDbType = MySqlDbType.DateTime;
                parameter.Value = value ?? DBNull.Value;
                break;
            case ValueKind.Guid:
                parameter.MySqlDbType = MySqlDbType.VarChar;
                parameter.Value = value is Guid g ? g.ToString("D", CultureInfo.InvariantCulture) : DBNull.Value;
                break;
            default:
                throw new NotSupportedException($"type '{targetType.NativeName}' is not supported");
        }

        return parameter;
    }

    public string BuildCreateProgressTableSql(string table)
        => $"CREATE TABLE IF NOT EXISTS {QuoteIdentifier(table)} (`TableName` varchar(256) NOT NULL, `CopiedRows` bigint NOT NULL, " +
           "`CompletedAt` datetime(3) NOT NULL, `ToolVersion` varchar(64) NOT NULL, PRIMARY KEY (`TableName`)) DEFAULT CHARACTER SET utf8mb4";

    public async Task<long?> ReadNextAutoIncrementAsync(
        DbConnection connection, int timeout, string table, string column, CancellationToken cancellationToken)
    {
        var value = await DbHelper.ScalarAsync(
            connection,
            "SELECT AUTO_INCREMENT FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table",
            timeout,
            cancellationToken,
            ("@table", table)).ConfigureAwait(false);
        return DbHelper.ToNullableLong(value);
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
        await DbHelper.ExecuteAsync(
            connection,
            $"ALTER TABLE {QuoteIdentifier(table)} AUTO_INCREMENT = {next.ToString(CultureInfo.InvariantCulture)}",
            timeout,
            cancellationToken).ConfigureAwait(false);
        return next;
    }

    public Task BeginTableAsync(DbConnection connection, int timeout, TablePlan table, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task EndTableAsync(DbConnection connection, int timeout, TablePlan table, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public async Task<IConstraintScope> DisableConstraintsAsync(
        DbConnection connection, int timeout, IReadOnlyList<string> tables, CancellationToken cancellationToken)
    {
        await DbHelper.ExecuteAsync(connection, "SET FOREIGN_KEY_CHECKS = 0", timeout, cancellationToken).ConfigureAwait(false);
        return new StatementConstraintScope(
            connection,
            timeout,
            ["SET FOREIGN_KEY_CHECKS = 1"],
            "foreign keys are not checked on the target while copying (FOREIGN_KEY_CHECKS = 0); referential integrity is not re-checked afterwards");
    }
}
