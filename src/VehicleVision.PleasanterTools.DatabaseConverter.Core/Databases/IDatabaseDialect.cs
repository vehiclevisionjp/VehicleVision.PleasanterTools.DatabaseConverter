using System.Data;
using System.Data.Common;
using System.Globalization;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Planning;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Schema;

namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.Databases;

/// <summary>接続文字列が不正。接続文字列の中身を含めないメッセージ（英語）だけを持つ。</summary>
public sealed class ConnectionStringException : Exception
{
    public ConnectionStringException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// DBMS ごとの方言。識別子の引用符、スキーマ読み取りの SQL、行の読み取り、バッチ挿入、採番の調整、制約の一時無効化を持つ。
/// 変換の本体はこの抽象にだけ依存する（<see cref="AdoDatabase"/> が組み合わせる）。
/// SQL に値を埋め込まない（値はパラメータ）。識別子は情報スキーマから得たものだけを、引用符でエスケープして使う。
/// </summary>
public interface IDatabaseDialect
{
    Dbms Dbms { get; }

    /// <summary>識別子を引用符で囲む（引用符自身はエスケープする）。</summary>
    string QuoteIdentifier(string identifier);

    /// <summary>接続を作る（開かない）。接続文字列が不正なら <see cref="ConnectionStringException"/>。</summary>
    DbConnection CreateConnection(string connectionString, int commandTimeoutSeconds);

    /// <summary>接続を開いた直後に実行する文。<paramref name="readOnlyRole"/> が true なら、変換元（読み取り専用）の接続。</summary>
    IReadOnlyList<string> SessionStatements(bool readOnlyRole);

    /// <summary>接続を開いた直後に実行する文のうち、失敗しても続けてよいもの（サーバの版によって無い設定など）。</summary>
    IReadOnlyList<string> OptionalSessionStatements();

    /// <summary>1 つの INSERT 文に使えるパラメータ数の上限。</summary>
    int MaxParametersPerStatement { get; }

    /// <summary>現在のスキーマの表・列・主キー・外部キーを読む。</summary>
    Task<SchemaInfo> ReadSchemaAsync(DbConnection connection, int commandTimeoutSeconds, CancellationToken cancellationToken);

    /// <summary>表の行数を数える SQL。</summary>
    string BuildCountSql(string table);

    /// <summary>行を読む SQL（列は指定順）。</summary>
    string BuildSelectSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> orderBy);

    /// <summary>複数行 INSERT 文（値はパラメータ <c>@p0</c>, <c>@p1</c>, ...）。</summary>
    string BuildInsertSql(string table, IReadOnlyList<ColumnInfo> columns, int rowCount, bool overridingSystemValue);

    /// <summary>変換先の列の型に合わせたパラメータを作る。</summary>
    DbParameter CreateParameter(string name, ColumnType targetType, object? value);

    /// <summary>管理表を作る SQL（無ければ作る）。</summary>
    string BuildCreateProgressTableSql(string table);

    /// <summary>表の存在を調べる SQL（パラメータ <c>@name</c>、件数を返す）。</summary>
    string TableExistsSql { get; }

    /// <summary>自動採番列の「次に払い出される値」を読む。</summary>
    Task<long?> ReadNextAutoIncrementAsync(DbConnection connection, int timeout, string table, string column, CancellationToken cancellationToken);

    /// <summary>自動採番の続き番号を <c>max(minimumNext, 最大値 + 1)</c> へ合わせる。</summary>
    Task<long?> SetNextAutoIncrementAsync(DbConnection connection, int timeout, string table, string column, long? minimumNext, CancellationToken cancellationToken);

    /// <summary>表への書き込みの前処理（SQL Server の <c>SET IDENTITY_INSERT ON</c>）。</summary>
    Task BeginTableAsync(DbConnection connection, int timeout, TablePlan table, CancellationToken cancellationToken);

    /// <summary>表への書き込みの後処理（SQL Server の <c>SET IDENTITY_INSERT OFF</c>）。</summary>
    Task EndTableAsync(DbConnection connection, int timeout, TablePlan table, CancellationToken cancellationToken);

    /// <summary>外部キーなどを一時的に無効にする。</summary>
    Task<IConstraintScope> DisableConstraintsAsync(DbConnection connection, int timeout, IReadOnlyList<string> tables, CancellationToken cancellationToken);
}

/// <summary>ADO.NET の呼び出しの小道具。</summary>
internal static class DbHelper
{
    public static DbCommand CreateCommand(DbConnection connection, string sql, int timeout, DbTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeout;
        command.Transaction = transaction;
        return command;
    }

    public static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    public static async Task<int> ExecuteAsync(
        DbConnection connection, string sql, int timeout, CancellationToken ct, DbTransaction? transaction = null)
    {
        await using var command = CreateCommand(connection, sql, timeout, transaction);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task<object?> ScalarAsync(
        DbConnection connection, string sql, int timeout, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, sql, timeout);
        foreach (var (name, value) in parameters)
        {
            AddParameter(command, name, value);
        }

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is DBNull ? null : result;
    }

    /// <summary>文字列を読む。ドライバによっては、情報スキーマの文字列が byte[] で返ることがあるため、どちらでも読めるようにする。</summary>
    public static string GetText(DbDataReader reader, int ordinal)
        => reader.GetValue(ordinal) switch
        {
            string s => s,
            byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
            var other => System.Convert.ToString(other, CultureInfo.InvariantCulture) ?? string.Empty,
        };

    public static long? ToNullableLong(object? value)
        => value is null or DBNull ? null : System.Convert.ToInt64(value, CultureInfo.InvariantCulture);

    /// <summary>引用符つき識別子の列を「, 」でつなぐ。</summary>
    public static string JoinQuoted(IDatabaseDialect dialect, IEnumerable<string> names)
        => string.Join(", ", names.Select(dialect.QuoteIdentifier));

    /// <summary>複数行 INSERT 文を組み立てる（方言の共通部分）。</summary>
    public static string BuildInsert(
        IDatabaseDialect dialect, string table, IReadOnlyList<ColumnInfo> columns, int rowCount, string? overridingClause)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("INSERT INTO ").Append(dialect.QuoteIdentifier(table))
            .Append(" (").Append(JoinQuoted(dialect, columns.Select(c => c.Name))).Append(')');
        if (overridingClause is not null)
        {
            sb.Append(' ').Append(overridingClause);
        }

        sb.Append(" VALUES ");
        var p = 0;
        for (var r = 0; r < rowCount; r++)
        {
            if (r > 0)
            {
                sb.Append(", ");
            }

            sb.Append('(');
            for (var c = 0; c < columns.Count; c++)
            {
                if (c > 0)
                {
                    sb.Append(", ");
                }

                sb.Append("@p").Append(p++.ToString(CultureInfo.InvariantCulture));
            }

            sb.Append(')');
        }

        return sb.ToString();
    }

    public static string BuildSelect(
        IDatabaseDialect dialect, string table, IReadOnlyList<string> columns, IReadOnlyList<string> orderBy)
    {
        var sql = $"SELECT {JoinQuoted(dialect, columns)} FROM {dialect.QuoteIdentifier(table)}";
        return orderBy.Count == 0 ? sql : $"{sql} ORDER BY {JoinQuoted(dialect, orderBy)}";
    }

    /// <summary>情報スキーマの 14 列の共通形（表名・列名・位置・NULL 可・型名・完全な型・長さ・精度・位取り・日時の精度・既定値・自動採番・ALWAYS・読み取り専用）から、スキーマを読む。</summary>
    public static async Task<SchemaInfo> ReadSchemaAsync(
        IDatabaseDialect dialect,
        DbConnection connection,
        int timeout,
        string columnsSql,
        string primaryKeysSql,
        string foreignKeysSql,
        CancellationToken ct)
    {
        var tableOrder = new List<string>();
        var columns = new Dictionary<string, List<ColumnInfo>>(StringComparer.Ordinal);
        await using (var command = CreateCommand(connection, columnsSql, timeout))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var table = GetText(reader, 0);
                var type = TypeCatalog.Describe(
                    dialect.Dbms,
                    GetText(reader, 4),
                    GetText(reader, 5),
                    ToNullableLong(reader.GetValue(6)),
                    (int?)ToNullableLong(reader.GetValue(7)),
                    (int?)ToNullableLong(reader.GetValue(8)),
                    (int?)ToNullableLong(reader.GetValue(9)));
                var column = new ColumnInfo(
                    GetText(reader, 1),
                    (int)System.Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                    type,
                    IsNullable: System.Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture) != 0,
                    HasDefault: System.Convert.ToInt64(reader.GetValue(10), CultureInfo.InvariantCulture) != 0,
                    IsAutoIncrement: System.Convert.ToInt64(reader.GetValue(11), CultureInfo.InvariantCulture) != 0,
                    IdentityAlways: System.Convert.ToInt64(reader.GetValue(12), CultureInfo.InvariantCulture) != 0,
                    IsReadOnly: System.Convert.ToInt64(reader.GetValue(13), CultureInfo.InvariantCulture) != 0);
                if (!columns.TryGetValue(table, out var list))
                {
                    list = [];
                    columns[table] = list;
                    tableOrder.Add(table);
                }

                list.Add(column);
            }
        }

        var primaryKeys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using (var command = CreateCommand(connection, primaryKeysSql, timeout))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var table = GetText(reader, 0);
                if (!primaryKeys.TryGetValue(table, out var list))
                {
                    list = [];
                    primaryKeys[table] = list;
                }

                list.Add(GetText(reader, 1));
            }
        }

        var parents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using (var command = CreateCommand(connection, foreignKeysSql, timeout))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var child = GetText(reader, 0);
                if (!parents.TryGetValue(child, out var list))
                {
                    list = [];
                    parents[child] = list;
                }

                list.Add(GetText(reader, 1));
            }
        }

        var tables = tableOrder
            .Select(name => new TableInfo(
                name,
                columns[name],
                primaryKeys.TryGetValue(name, out var pk) ? pk : [],
                parents.TryGetValue(name, out var parent) ? parent.Distinct(StringComparer.Ordinal).ToList() : []))
            .ToList();
        return new SchemaInfo(dialect.Dbms, tables);
    }
}

/// <summary>制約を無効にしたときの後始末を、文の列として持つ実装。</summary>
internal sealed class StatementConstraintScope : IConstraintScope
{
    private readonly DbConnection _connection;
    private readonly int _timeout;
    private readonly IReadOnlyList<string> _restoreStatements;

    public StatementConstraintScope(DbConnection connection, int timeout, IReadOnlyList<string> restoreStatements, string? warning)
    {
        _connection = connection;
        _timeout = timeout;
        _restoreStatements = restoreStatements;
        Warning = warning;
    }

    public string? Warning { get; }

    public async Task<IReadOnlyList<string>> RestoreAsync(CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        foreach (var statement in _restoreStatements)
        {
            try
            {
                await DbHelper.ExecuteAsync(_connection, statement, _timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                problems.Add(Engine.ConversionEngine.Describe(ex));
            }
        }

        return problems;
    }
}
