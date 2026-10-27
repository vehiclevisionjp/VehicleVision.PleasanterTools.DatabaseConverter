using VehicleVision.PleasanterTools.DatabaseConverter.Core;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.CommandLine;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Databases;

namespace VehicleVision.PleasanterTools.DatabaseConverter.Integration.Tests;

/// <summary>
/// 実機の SQL Server / PostgreSQL / MySQL に対する変換のテスト。
/// Pleasanter の本物の表は使えないので、Pleasanter の表を模した小さな表（<see cref="MimicSchema"/>）を各 DBMS に作り、
/// 6 通りの変換を実際に行って、件数・内容・採番の続きを確かめる。表は毎回落として作り直す。
/// </summary>
public sealed class ConversionIntegrationTests
{
    private sealed record RunResult(int ExitCode, string Output, string Error)
    {
        public string All => Output + Environment.NewLine + Error;
    }

    // 6 通りの向き -------------------------------------------------------

    [DbFact(Dbms.SqlServer, Dbms.PostgreSql)]
    public Task SQLServerからPostgreSQLへ() => ConvertAndVerifyAsync(Dbms.SqlServer, Dbms.PostgreSql);

    [DbFact(Dbms.SqlServer, Dbms.MySql)]
    public Task SQLServerからMySQLへ() => ConvertAndVerifyAsync(Dbms.SqlServer, Dbms.MySql);

    [DbFact(Dbms.PostgreSql, Dbms.SqlServer)]
    public Task PostgreSQLからSQLServerへ() => ConvertAndVerifyAsync(Dbms.PostgreSql, Dbms.SqlServer);

    [DbFact(Dbms.PostgreSql, Dbms.MySql)]
    public Task PostgreSQLからMySQLへ() => ConvertAndVerifyAsync(Dbms.PostgreSql, Dbms.MySql);

    [DbFact(Dbms.MySql, Dbms.SqlServer)]
    public Task MySQLからSQLServerへ() => ConvertAndVerifyAsync(Dbms.MySql, Dbms.SqlServer);

    [DbFact(Dbms.MySql, Dbms.PostgreSql)]
    public Task MySQLからPostgreSQLへ() => ConvertAndVerifyAsync(Dbms.MySql, Dbms.PostgreSql);

    private static async Task ConvertAndVerifyAsync(Dbms from, Dbms to)
    {
        await using var source = await Db.OpenAsync(from);
        await using var target = await Db.OpenAsync(to);
        await PrepareAsync(source, target);
        await SeedMainAsync(source);

        var before = await SnapshotAllAsync(source);
        Assert.Equal(24, before[MimicSchema.Items.Table].Count);

        // バッチの境界を何度もまたぐ小さなバッチで流す。バイト数の上限も小さくして、大きなバイナリの行を単独で書かせる
        var result = await RunAsync(from, to, "--batch-size", "5", "--max-batch-bytes", "1048576");
        Assert.True(result.ExitCode == 0, result.All);
        Assert.Contains("Result: OK", result.Output);

        // 変換元は変わらない（読み取りだけ）
        var after = await SnapshotAllAsync(source);
        foreach (var spec in MimicSchema.Main)
        {
            Assert.Equal(before[spec.Table], after[spec.Table]);
        }

        Assert.False(await source.TableExistsAsync("__DatabaseConverterProgress"), "the source must not get a progress table");

        // 件数と内容が一致する
        var copied = await SnapshotAllAsync(target);
        foreach (var spec in MimicSchema.Main)
        {
            AssertSameRows(spec.Table, before[spec.Table], copied[spec.Table]);
        }

        Assert.Equal(24, copied[MimicSchema.Items.Table].Count);
        Assert.Equal(20, copied[MimicSchema.Results.Table].Count);
        Assert.Equal(40, copied[MimicSchema.ResultsHistory.Table].Count);
        Assert.Equal(6, copied[MimicSchema.Binaries.Table].Count);

        // 完了の記録（管理表）。両方にある表（空の Edge / CollationTest を含む）すべてに付く
        Assert.True(await target.TableExistsAsync("__DatabaseConverterProgress"));
        Assert.Equal(MimicSchema.CreateStatements(to).Count, await target.CountAsync("__DatabaseConverterProgress"));

        // 自動採番の続き: 変換元の最後の行（25）を消してあるので、次は 26。Binaries は 7
        await target.InsertAsync(MimicSchema.Items, ItemRow());
        Assert.Equal(26L, Convert.ToInt64(await target.ScalarAsync($"SELECT MAX({target.Dialect.QuoteIdentifier("ReferenceId")}) FROM {target.Dialect.QuoteIdentifier("Items")}")));
        await target.InsertAsync(MimicSchema.Binaries, [1L, 26L, "Attachments", "next", null, null, null, new DateTime(2024, 5, 1)]);
        Assert.Equal(7L, Convert.ToInt64(await target.ScalarAsync($"SELECT MAX({target.Dialect.QuoteIdentifier("BinaryId")}) FROM {target.Dialect.QuoteIdentifier("Binaries")}")));
    }

    // ドライラン -------------------------------------------------------------

    [DbFact(Dbms.SqlServer, Dbms.PostgreSql)]
    public async Task ドライランは何も書かない()
    {
        await using var source = await Db.OpenAsync(Dbms.SqlServer);
        await using var target = await Db.OpenAsync(Dbms.PostgreSql);
        await PrepareAsync(source, target);
        await SeedMainAsync(source);

        var result = await RunAsync(Dbms.SqlServer, Dbms.PostgreSql, "--dry-run");

        Assert.True(result.ExitCode == 0, result.All);
        Assert.Contains("[dry-run] Items: would copy 24 row(s)", result.Output);
        foreach (var spec in MimicSchema.Main)
        {
            Assert.Equal(0, await target.CountAsync(spec.Table));
        }

        Assert.False(await target.TableExistsAsync("__DatabaseConverterProgress"), "a dry-run must not create the progress table");
    }

    // 再開 -------------------------------------------------------------------

    [DbFact(Dbms.PostgreSql, Dbms.SqlServer)]
    public async Task 再開は完了した表をとばし_途中の表を入れ直す()
    {
        await using var source = await Db.OpenAsync(Dbms.PostgreSql);
        await using var target = await Db.OpenAsync(Dbms.SqlServer);
        await PrepareAsync(source, target);
        await SeedMainAsync(source);
        var expected = await SnapshotAllAsync(source);

        Assert.Equal(0, (await RunAsync(Dbms.PostgreSql, Dbms.SqlServer)).ExitCode);

        // 2 回目は、変換先が空でないので何も書かずに失敗する
        var again = await RunAsync(Dbms.PostgreSql, Dbms.SqlServer);
        Assert.Equal(1, again.ExitCode);
        Assert.Contains("not empty", again.All);
        Assert.Contains("--resume", again.All);

        // --resume: 完了した表は全部とばす
        var skipped = await RunAsync(Dbms.PostgreSql, Dbms.SqlServer, "--resume");
        Assert.True(skipped.ExitCode == 0, skipped.All);
        Assert.Contains("Skipped", skipped.Output);

        // Results が途中で止まったことにする（行を一部消し、完了の記録も消す）
        await target.ExecuteAsync("DELETE FROM [Binaries]");
        await target.ExecuteAsync("DELETE FROM [Results_history]");
        await target.ExecuteAsync("DELETE FROM [Results] WHERE [ResultId] > 7");
        await target.ExecuteAsync("DELETE FROM [__DatabaseConverterProgress] WHERE [TableName] IN (N'Results', N'Binaries', N'Results_history')");

        var resumed = await RunAsync(Dbms.PostgreSql, Dbms.SqlServer, "--resume");
        Assert.True(resumed.ExitCode == 0, resumed.All);
        var copied = await SnapshotAllAsync(target);
        foreach (var spec in MimicSchema.Main)
        {
            AssertSameRows(spec.Table, expected[spec.Table], copied[spec.Table]);
        }
    }

    [DbFact(Dbms.SqlServer, Dbms.MySql)]
    public async Task 完了したら管理表を消すオプション()
    {
        await using var source = await Db.OpenAsync(Dbms.SqlServer);
        await using var target = await Db.OpenAsync(Dbms.MySql);
        await PrepareAsync(source, target);
        await SeedMainAsync(source);

        var result = await RunAsync(Dbms.SqlServer, Dbms.MySql, "--cleanup-progress", "--disable-constraints");

        Assert.True(result.ExitCode == 0, result.All);
        Assert.False(await target.TableExistsAsync("__DatabaseConverterProgress"));
        Assert.Equal(24, await target.CountAsync("Items"));
    }

    // 変換できない値 -----------------------------------------------------------

    [DbFact(Dbms.SqlServer, Dbms.PostgreSql)]
    public async Task NUL文字を含む行はPostgreSQLへ入れず_失敗として数える()
    {
        await using var source = await Db.OpenAsync(Dbms.SqlServer);
        await using var target = await Db.OpenAsync(Dbms.PostgreSql);
        await PrepareAsync(source, target);
        await source.InsertAsync(MimicSchema.Edge, [1L, "ok", new DateTime(2024, 1, 1)]);
        await source.InsertAsync(MimicSchema.Edge, [2L, "has\0nul", null]);
        await source.InsertAsync(MimicSchema.Edge, [3L, "ok3", null]);

        var result = await RunAsync(Dbms.SqlServer, Dbms.PostgreSql, "--tables", "Edge");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[Edge] row #2 PK[Id=2] column 'Note': string contains a NUL character", result.Error);
        Assert.Equal(2, await target.CountAsync("Edge"));
        Assert.Equal(0, await target.CountAsync("__DatabaseConverterProgress"));
    }

    [DbFact(Dbms.PostgreSql, Dbms.SqlServer)]
    public async Task SQLServerのdatetimeの範囲外の日時は_行と列を示して失敗にする()
    {
        await using var source = await Db.OpenAsync(Dbms.PostgreSql);
        await using var target = await Db.OpenAsync(Dbms.SqlServer);
        await PrepareAsync(source, target);
        await source.InsertAsync(MimicSchema.Edge, [1L, "ok", new DateTime(1753, 1, 1)]);
        await source.InsertAsync(MimicSchema.Edge, [2L, "too old", new DateTime(1, 1, 2)]);
        await source.InsertAsync(MimicSchema.Edge, [3L, "also too old", new DateTime(1752, 12, 31, 23, 59, 59)]);

        var result = await RunAsync(Dbms.PostgreSql, Dbms.SqlServer, "--tables", "Edge");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("PK[Id=2] column 'At'", result.Error);
        Assert.Contains("PK[Id=3] column 'At'", result.Error);
        Assert.Contains("out of range", result.Error);
        Assert.Equal(1, await target.CountAsync("Edge"));
    }

    // 照合順序 -----------------------------------------------------------------

    [DbFact(Dbms.PostgreSql, Dbms.SqlServer)]
    public async Task 大文字小文字の違いで一意制約が破れる行をSQLServerで特定する()
    {
        await using var source = await Db.OpenAsync(Dbms.PostgreSql);
        await using var target = await Db.OpenAsync(Dbms.SqlServer);
        await PrepareAsync(source, target);
        await SeedCollationAsync(source);

        var result = await RunAsync(Dbms.PostgreSql, Dbms.SqlServer, "--tables", "CollationTest");

        // SQL Server の既定は大文字小文字を区別しない（アクセントは区別する）: 'ABC' だけが衝突する
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("PK[Id=2]", result.Error);
        Assert.DoesNotContain("PK[Id=4]", result.Error);
        Assert.Equal(3, await target.CountAsync("CollationTest"));
    }

    [DbFact(Dbms.PostgreSql, Dbms.MySql)]
    public async Task 大文字小文字とアクセントの違いで一意制約が破れる行をMySQLで特定する()
    {
        await using var source = await Db.OpenAsync(Dbms.PostgreSql);
        await using var target = await Db.OpenAsync(Dbms.MySql);
        await PrepareAsync(source, target);
        await SeedCollationAsync(source);

        var result = await RunAsync(Dbms.PostgreSql, Dbms.MySql, "--tables", "CollationTest");

        // utf8mb4_general_ci は大文字小文字もアクセントも区別しない: 'ABC' と 'café' が衝突する
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("PK[Id=2]", result.Error);
        Assert.Contains("PK[Id=4]", result.Error);
        Assert.Equal(2, await target.CountAsync("CollationTest"));
    }

    // 準備と補助 -----------------------------------------------------------------

    private static async Task SeedCollationAsync(Db source)
    {
        await source.InsertAsync(MimicSchema.CollationTest, [1L, "abc"]);
        await source.InsertAsync(MimicSchema.CollationTest, [2L, "ABC"]);
        await source.InsertAsync(MimicSchema.CollationTest, [3L, "cafe"]);
        await source.InsertAsync(MimicSchema.CollationTest, [4L, "café"]);
    }

    private static async Task PrepareAsync(Db source, Db target)
    {
        foreach (var db in new[] { source, target })
        {
            foreach (var sql in MimicSchema.DropStatements(db.Dbms))
            {
                await db.ExecuteAsync(sql);
            }

            foreach (var sql in MimicSchema.CreateStatements(db.Dbms))
            {
                await db.ExecuteAsync(sql);
            }
        }
    }

    private static async Task SeedMainAsync(Db source)
    {
        foreach (var row in MimicSchema.ItemRows())
        {
            await source.InsertAsync(MimicSchema.Items, row);
        }

        // 末尾の行を消して、自動採番を「最大値 + 1」より進める（変換先の続き番号が 26 になることを後で確かめる）
        await source.ExecuteAsync($"DELETE FROM {source.Dialect.QuoteIdentifier("Items")} WHERE {source.Dialect.QuoteIdentifier("ReferenceId")} = 25");

        foreach (var row in MimicSchema.ResultRows())
        {
            await source.InsertAsync(MimicSchema.Results, row);
        }

        foreach (var row in MimicSchema.ResultHistoryRows())
        {
            await source.InsertAsync(MimicSchema.ResultsHistory, row);
        }

        foreach (var row in MimicSchema.BinaryRows())
        {
            await source.InsertAsync(MimicSchema.Binaries, row);
        }
    }

    private static object?[] ItemRow()
        => ["Results", 1L, "next", null, false, null, null, new DateTime(2024, 5, 1), null];

    private static async Task<Dictionary<string, List<string>>> SnapshotAllAsync(Db db)
    {
        var snapshots = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var spec in MimicSchema.Main)
        {
            snapshots[spec.Table] = await db.SnapshotAsync(spec);
        }

        return snapshots;
    }

    private static void AssertSameRows(string table, List<string> expected, List<string> actual)
    {
        Assert.True(expected.Count == actual.Count, $"{table}: row count differs (expected {expected.Count}, actual {actual.Count})");
        for (var i = 0; i < expected.Count; i++)
        {
            if (expected[i] != actual[i])
            {
                Assert.Fail($"{table}: row {i + 1} differs.{Environment.NewLine}expected: {Shorten(expected[i])}{Environment.NewLine}actual:   {Shorten(actual[i])}");
            }
        }
    }

    private static string Shorten(string text) => text.Length > 400 ? text[..400] + "..." : text;

    private static async Task<RunResult> RunAsync(Dbms from, Dbms to, params string[] extraArguments)
    {
        var environment = new Dictionary<string, string?>
        {
            ["DBCONV_SOURCE_CONNECTION"] = TestDatabases.Require(from),
            ["DBCONV_TARGET_CONNECTION"] = TestDatabases.Require(to),
        };
        var output = new StringWriter();
        var error = new StringWriter();
        var app = new ConverterApplication(
            new AdoDatabaseOpener(),
            TextReader.Null,
            output,
            error,
            name => environment.GetValueOrDefault(name),
            "integration-test");
        string[] arguments = ["convert", "--from", from.ToCliName(), "--to", to.ToCliName(), .. extraArguments];
        var code = await app.RunAsync(arguments, CancellationToken.None);
        return new RunResult(code, output.ToString(), error.ToString());
    }
}
