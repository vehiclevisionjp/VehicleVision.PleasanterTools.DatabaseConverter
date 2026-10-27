using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using VehicleVision.PleasanterTools.DatabaseConverter.Core;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Databases;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Schema;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace VehicleVision.PleasanterTools.DatabaseConverter.Integration.Tests;

/// <summary>
/// 実機の DB の接続文字列。環境変数 <c>DBCONV_TEST_SQLSERVER</c> / <c>DBCONV_TEST_POSTGRESQL</c> / <c>DBCONV_TEST_MYSQL</c> から読む。
/// 設定が無い DBMS のテストはスキップする。ただし CI は <c>DBCONV_REQUIRE_ALL_RDBMS=1</c> を立て、
/// 未設定があればテストを落とす（何も試さずに緑になるのを防ぐため）。
/// </summary>
internal static class TestDatabases
{
    public const string RequireAllVariable = "DBCONV_REQUIRE_ALL_RDBMS";

    public static IReadOnlyList<Dbms> All { get; } = [Dbms.SqlServer, Dbms.PostgreSql, Dbms.MySql];

    public static string VariableName(Dbms dbms) => dbms switch
    {
        Dbms.SqlServer => "DBCONV_TEST_SQLSERVER",
        Dbms.PostgreSql => "DBCONV_TEST_POSTGRESQL",
        Dbms.MySql => "DBCONV_TEST_MYSQL",
        _ => throw new ArgumentOutOfRangeException(nameof(dbms)),
    };

    public static string? ConnectionString(Dbms dbms)
    {
        var value = Environment.GetEnvironmentVariable(VariableName(dbms));
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static bool RequireAll => Environment.GetEnvironmentVariable(RequireAllVariable) == "1";

    /// <summary>テストをスキップする理由。流すなら <see langword="null"/>（CI では常に流して、未設定ならテスト本体で落とす）。</summary>
    public static string? SkipReason(IEnumerable<Dbms> needed)
    {
        if (RequireAll)
        {
            return null;
        }

        var missing = needed.Where(d => ConnectionString(d) is null).Select(VariableName).ToList();
        return missing.Count == 0 ? null : $"{string.Join(", ", missing)} is not set (the real-database tests need it)";
    }

    /// <summary>接続文字列を返す。無ければ失敗（CI で設定漏れを見逃さない）。</summary>
    public static string Require(Dbms dbms)
    {
        var value = ConnectionString(dbms);
        Assert.True(value is not null, $"{VariableName(dbms)} is not set.");
        return value!;
    }
}

/// <summary>必要な DBMS の接続文字列が無ければスキップする <see cref="FactAttribute"/>。</summary>
public sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute(params Dbms[] needed)
    {
        Skip = TestDatabases.SkipReason(needed);
    }
}

public sealed class TestDatabasesTests
{
    [Fact]
    public void CIでは全RDBMSが設定されている()
    {
        if (!TestDatabases.RequireAll)
        {
            return;
        }

        foreach (var dbms in TestDatabases.All)
        {
            Assert.True(
                TestDatabases.ConnectionString(dbms) is not null,
                $"{TestDatabases.RequireAllVariable}=1 but {TestDatabases.VariableName(dbms)} is not set.");
        }
    }
}

/// <summary>テストから実機の DB を直接操作する接続。</summary>
internal sealed class Db : IAsyncDisposable
{
    private readonly DbConnection _connection;

    private Db(Dbms dbms, DbConnection connection)
    {
        Dbms = dbms;
        Dialect = AdoDatabaseOpener.CreateDialect(dbms);
        _connection = connection;
    }

    public Dbms Dbms { get; }

    public IDatabaseDialect Dialect { get; }

    public static async Task<Db> OpenAsync(Dbms dbms)
    {
        var dialect = AdoDatabaseOpener.CreateDialect(dbms);
        var connection = dialect.CreateConnection(TestDatabases.Require(dbms), 120);
        await connection.OpenAsync();
        return new Db(dbms, connection);
    }

    public async Task<int> ExecuteAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    public async Task<long> CountAsync(string table)
        => Convert.ToInt64(await ScalarAsync($"SELECT COUNT(*) FROM {Dialect.QuoteIdentifier(table)}"), CultureInfo.InvariantCulture);

    public async Task<bool> TableExistsAsync(string table)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = Dialect.TableExistsSql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) > 0;
    }

    public async Task InsertAsync(MimicSpec spec, object?[] values)
    {
        var columns = spec.Columns.Where(c => !c.Generated).ToList();
        Assert.Equal(columns.Count, values.Length);
        await using var command = _connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = $"INSERT INTO {Dialect.QuoteIdentifier(spec.Table)} " +
                              $"({string.Join(", ", columns.Select(c => Dialect.QuoteIdentifier(c.Name)))}) " +
                              $"VALUES ({string.Join(", ", columns.Select((_, i) => "@p" + i))})";
        for (var i = 0; i < columns.Count; i++)
        {
            command.Parameters.Add(Dialect.CreateParameter("@p" + i, columns[i].TypeFor(Dbms), values[i]));
        }

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>表の全行を、DBMS 間で比べられる文字列にして返す（主キー順）。</summary>
    public async Task<List<string>> SnapshotAsync(MimicSpec spec)
    {
        await using var command = _connection.CreateCommand();
        command.CommandTimeout = 300;
        command.CommandText = Dialect.BuildSelectSql(spec.Table, spec.Columns.Select(c => c.Name).ToList(), spec.OrderBy);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var parts = new string[spec.Columns.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = Canonical(reader.GetValue(i));
            }

            rows.Add(string.Join("|", parts));
        }

        return rows;
    }

    public static string Canonical(object value) => value switch
    {
        DBNull => "<NULL>",
        bool b => b ? "1" : "0",
        byte[] bytes => $"bin:{bytes.Length}:{Convert.ToHexString(SHA256.HashData(bytes))}",
        string s => "s:" + s,
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        decimal m => m.ToString("0.####", CultureInfo.InvariantCulture),
        double x => x.ToString("R", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("R", CultureInfo.InvariantCulture),
        sbyte or byte or short or int or long => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
