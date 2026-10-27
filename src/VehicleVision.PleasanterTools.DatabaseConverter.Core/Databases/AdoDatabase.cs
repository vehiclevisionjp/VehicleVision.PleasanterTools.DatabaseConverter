using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Engine;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Planning;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Schema;

namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.Databases;

/// <summary>
/// ADO.NET の接続と <see cref="IDatabaseDialect"/> を組み合わせた <see cref="ITargetDatabase"/>。
/// 変換元として使うとき（<c>readOnlyRole = true</c>）は、書き込み用のメソッドを呼ばない。
/// </summary>
public sealed class AdoDatabase : ITargetDatabase
{
    private static readonly ColumnType ProgressNameType = new(ValueKind.String, "nvarchar", 256, LengthUnit.Utf16Units);
    private static readonly ColumnType ProgressCountType = new(ValueKind.Integer, "bigint");
    private static readonly ColumnType ProgressTimeType = new(ValueKind.DateTime, "datetime2", FractionDigits: 3);

    private readonly IDatabaseDialect _dialect;
    private readonly DbConnection _connection;
    private readonly int _timeout;

    private AdoDatabase(IDatabaseDialect dialect, DbConnection connection, int timeout)
    {
        _dialect = dialect;
        _connection = connection;
        _timeout = timeout;
    }

    public Dbms Dbms => _dialect.Dbms;

    /// <summary>接続を開き、セッションを設定する。</summary>
    public static async Task<AdoDatabase> OpenAsync(
        IDatabaseDialect dialect,
        string connectionString,
        bool readOnlyRole,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var connection = dialect.CreateConnection(connectionString, commandTimeoutSeconds);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            foreach (var statement in dialect.SessionStatements(readOnlyRole))
            {
                await DbHelper.ExecuteAsync(connection, statement, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            }

            foreach (var statement in dialect.OptionalSessionStatements())
            {
                try
                {
                    await DbHelper.ExecuteAsync(connection, statement, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
                }
                catch (DbException)
                {
                    // サーバの版によって無い設定。無くても変換はできる
                }
            }

            return new AdoDatabase(dialect, connection, commandTimeoutSeconds);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<SchemaInfo> ReadSchemaAsync(CancellationToken cancellationToken)
        => _dialect.ReadSchemaAsync(_connection, _timeout, cancellationToken);

    public async Task<long> CountRowsAsync(string table, CancellationToken cancellationToken)
    {
        var value = await DbHelper.ScalarAsync(_connection, _dialect.BuildCountSql(table), _timeout, cancellationToken).ConfigureAwait(false);
        return DbHelper.ToNullableLong(value) ?? 0;
    }

    public async IAsyncEnumerable<object?[]> ReadRowsAsync(
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> orderBy,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var command = DbHelper.CreateCommand(_connection, _dialect.BuildSelectSql(table, columns, orderBy), _timeout);

        // SequentialAccess: Binaries.Bin のような大きな列を、行ごとに全部は先読みしない（列は先頭から順に 1 度ずつ読む）
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new object?[columns.Count];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.GetValue(i);
            }

            yield return row;
        }
    }

    public Task<long?> ReadNextAutoIncrementAsync(string table, string column, CancellationToken cancellationToken)
        => _dialect.ReadNextAutoIncrementAsync(_connection, _timeout, table, column, cancellationToken);

    public async Task EnsureProgressTableAsync(CancellationToken cancellationToken)
        => await DbHelper.ExecuteAsync(
            _connection,
            _dialect.BuildCreateProgressTableSql(DatabaseConverterConstants.ProgressTableName),
            _timeout,
            cancellationToken).ConfigureAwait(false);

    private async Task<bool> ProgressTableExistsAsync(CancellationToken cancellationToken)
    {
        var count = await DbHelper.ScalarAsync(
            _connection,
            _dialect.TableExistsSql,
            _timeout,
            cancellationToken,
            ("@name", DatabaseConverterConstants.ProgressTableName)).ConfigureAwait(false);
        return (DbHelper.ToNullableLong(count) ?? 0) > 0;
    }

    public async Task<IReadOnlySet<string>> ReadCompletedTablesAsync(CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (!await ProgressTableExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return names;
        }

        var sql = $"SELECT {_dialect.QuoteIdentifier("TableName")} FROM {_dialect.QuoteIdentifier(DatabaseConverterConstants.ProgressTableName)}";
        await using var command = DbHelper.CreateCommand(_connection, sql, _timeout);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(DbHelper.GetText(reader, 0));
        }

        return names;
    }

    public async Task MarkCompletedAsync(string table, long rows, CancellationToken cancellationToken)
    {
        var q = _dialect.QuoteIdentifier(DatabaseConverterConstants.ProgressTableName);
        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = DbHelper.CreateCommand(_connection, $"DELETE FROM {q} WHERE {_dialect.QuoteIdentifier("TableName")} = @name", _timeout, transaction))
        {
            delete.Parameters.Add(_dialect.CreateParameter("@name", ProgressNameType, table));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var columns = string.Join(", ", new[] { "TableName", "CopiedRows", "CompletedAt", "ToolVersion" }.Select(_dialect.QuoteIdentifier));
        await using (var insert = DbHelper.CreateCommand(_connection, $"INSERT INTO {q} ({columns}) VALUES (@name, @rows, @at, @version)", _timeout, transaction))
        {
            insert.Parameters.Add(_dialect.CreateParameter("@name", ProgressNameType, table));
            insert.Parameters.Add(_dialect.CreateParameter("@rows", ProgressCountType, rows));
            var now = DateTime.UtcNow;
            insert.Parameters.Add(_dialect.CreateParameter(
                "@at",
                ProgressTimeType,
                new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Unspecified)));
            insert.Parameters.Add(_dialect.CreateParameter("@version", ProgressNameType, ToolVersion));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ToolVersion =>
        typeof(AdoDatabase).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task DropProgressTableAsync(CancellationToken cancellationToken)
    {
        if (await ProgressTableExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            await DbHelper.ExecuteAsync(
                _connection,
                $"DROP TABLE {_dialect.QuoteIdentifier(DatabaseConverterConstants.ProgressTableName)}",
                _timeout,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ClearTableAsync(string table, CancellationToken cancellationToken)
        => await DbHelper.ExecuteAsync(_connection, $"DELETE FROM {_dialect.QuoteIdentifier(table)}", _timeout, cancellationToken).ConfigureAwait(false);

    public Task<IConstraintScope> DisableConstraintsAsync(IReadOnlyList<string> tables, CancellationToken cancellationToken)
        => _dialect.DisableConstraintsAsync(_connection, _timeout, tables, cancellationToken);

    public Task BeginTableAsync(TablePlan table, CancellationToken cancellationToken)
        => _dialect.BeginTableAsync(_connection, _timeout, table, cancellationToken);

    public Task EndTableAsync(TablePlan table, CancellationToken cancellationToken)
        => _dialect.EndTableAsync(_connection, _timeout, table, cancellationToken);

    public async Task<WriteBatchResult> WriteBatchAsync(TablePlan table, IReadOnlyList<object?[]> rows, CancellationToken cancellationToken)
    {
        var targetColumns = table.Mappings.Select(m => m.Target).ToList();
        var overriding = targetColumns.Any(c => c.IdentityAlways);
        var rowsPerStatement = Math.Max(1, Math.Min(1000, _dialect.MaxParametersPerStatement / Math.Max(1, targetColumns.Count)));
        var tableName = table.Target!.Name;

        // まずバッチ全体を 1 つのトランザクションで書く
        try
        {
            await using var transaction = await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            for (var offset = 0; offset < rows.Count; offset += rowsPerStatement)
            {
                var count = Math.Min(rowsPerStatement, rows.Count - offset);
                await InsertAsync(tableName, targetColumns, rows, offset, count, overriding, transaction, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new WriteBatchResult(rows.Count, []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_connection.State != ConnectionState.Open)
            {
                throw;
            }
        }

        // 失敗したら（トランザクションは取り消し済み）、1 行ずつ書き直して、入らない行を特定する
        var failures = new List<RowWriteFailure>();
        for (var i = 0; i < rows.Count; i++)
        {
            try
            {
                await InsertAsync(tableName, targetColumns, rows, i, 1, overriding, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_connection.State != ConnectionState.Open)
                {
                    throw;
                }

                failures.Add(new RowWriteFailure(i, ConversionEngine.Describe(ex)));
            }
        }

        return new WriteBatchResult(rows.Count - failures.Count, failures);
    }

    private async Task InsertAsync(
        string table,
        IReadOnlyList<ColumnInfo> columns,
        IReadOnlyList<object?[]> rows,
        int offset,
        int count,
        bool overriding,
        DbTransaction? transaction,
        CancellationToken ct)
    {
        await using var command = DbHelper.CreateCommand(
            _connection, _dialect.BuildInsertSql(table, columns, count, overriding), _timeout, transaction);
        var p = 0;
        for (var r = 0; r < count; r++)
        {
            var row = rows[offset + r];
            for (var c = 0; c < columns.Count; c++)
            {
                command.Parameters.Add(_dialect.CreateParameter("@p" + p++, columns[c].Type, row[c]));
            }
        }

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public Task<long?> AdjustAutoIncrementAsync(TablePlan table, long? sourceNext, CancellationToken cancellationToken)
        => _dialect.SetNextAutoIncrementAsync(
            _connection, _timeout, table.Target!.Name, table.AutoIncrement!.Name, sourceNext, cancellationToken);

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync().ConfigureAwait(false);
}

/// <summary>実際の DB へ接続する <see cref="IDatabaseOpener"/>。</summary>
public sealed class AdoDatabaseOpener : IDatabaseOpener
{
    /// <summary>DBMS に対応する方言。</summary>
    public static IDatabaseDialect CreateDialect(Dbms dbms) => dbms switch
    {
        Dbms.SqlServer => new SqlServerDialect(),
        Dbms.PostgreSql => new PostgreSqlDialect(),
        Dbms.MySql => new MySqlDialect(),
        _ => throw new ArgumentOutOfRangeException(nameof(dbms)),
    };

    public async Task<ISourceDatabase> OpenSourceAsync(Dbms dbms, string connectionString, int commandTimeoutSeconds, CancellationToken cancellationToken)
        => await AdoDatabase.OpenAsync(CreateDialect(dbms), connectionString, true, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);

    public async Task<ITargetDatabase> OpenTargetAsync(Dbms dbms, string connectionString, int commandTimeoutSeconds, CancellationToken cancellationToken)
        => await AdoDatabase.OpenAsync(CreateDialect(dbms), connectionString, false, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
}
