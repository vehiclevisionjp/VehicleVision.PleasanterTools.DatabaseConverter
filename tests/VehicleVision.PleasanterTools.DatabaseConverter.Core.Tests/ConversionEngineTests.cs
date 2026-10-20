using VehicleVision.PleasanterTools.DatabaseConverter.Core.Engine;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Planning;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Schema;

namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.Tests;

/// <summary>変換の本体を、メモリ上の偽の source / target で確かめる。</summary>
public sealed class ConversionEngineTests
{
    private static readonly TableInfo SourceItems = T.Table(
        "Items",
        [
            T.Col("ReferenceId", T.BigInt, nullable: false, auto: true),
            T.Col("Title", T.VarChar(20, LengthUnit.Utf16Units), nullable: false),
            T.Col("Body", T.Text),
            T.Col("Updated", T.DateTime(3)),
        ],
        pk: ["ReferenceId"]);

    private static readonly TableInfo TargetItems = T.Table(
        "Items",
        [
            T.Col("ReferenceId", T.BigInt, nullable: false, auto: true),
            T.Col("Title", T.VarChar(20), nullable: false),
            T.Col("Body", T.Text),
            T.Col("Updated", T.DateTime(3)),
        ],
        pk: ["ReferenceId"]);

    private static object?[][] ItemRows(int count)
        => Enumerable.Range(1, count)
            .Select(i => new object?[] { (long)i, $"title{i}", i % 2 == 0 ? null : $"本文{i}", new DateTime(2024, 1, 1).AddMinutes(i) })
            .ToArray();

    private static (InMemoryDatabase Source, InMemoryDatabase Target) Databases(int rows)
    {
        var source = new InMemoryDatabase(Dbms.SqlServer).Add(SourceItems, ItemRows(rows));
        var target = new InMemoryDatabase(Dbms.PostgreSql).Add(TargetItems);
        return (source, target);
    }

    private static async Task<(ConversionReport Report, ListLog Log)> RunAsync(
        InMemoryDatabase source, InMemoryDatabase target, RunOptions? options = null, PlanOptions? planOptions = null)
    {
        var log = new ListLog();
        var plan = ConversionPlanner.Plan(
            await source.ReadSchemaAsync(CancellationToken.None),
            await target.ReadSchemaAsync(CancellationToken.None),
            planOptions ?? new PlanOptions());
        var engine = new ConversionEngine(source, target, log);
        var report = await engine.RunAsync(plan, options ?? new RunOptions(), CancellationToken.None);
        return (report, log);
    }

    [Fact]
    public async Task 全行を変換先へコピーして_件数と内容を確かめる()
    {
        var (source, target) = Databases(25);
        var (report, _) = await RunAsync(source, target);

        var table = Assert.Single(report.Tables);
        Assert.Equal(TableStatus.Copied, table.Status);
        Assert.Equal(25, table.SourceRows);
        Assert.Equal(25, table.CopiedRows);
        Assert.True(table.Verified);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal(25, target.Rows("Items").Count);
        Assert.Contains("Items", target.Completed);
        Assert.Equal(0, source.WriteCalls);
    }

    [Fact]
    public async Task バッチは指定の行数で区切る()
    {
        var (source, target) = Databases(25);
        await RunAsync(source, target, new RunOptions { BatchSize = 10 });
        Assert.Equal([10, 10, 5], target.Batches.Select(b => b.Rows));
    }

    [Fact]
    public async Task バッチは概算のバイト数でも区切る()
    {
        var big = new string('あ', 5000); // 約 10KB
        var source = new InMemoryDatabase(Dbms.SqlServer).Add(
            T.Table("Big", [T.Col("Id", T.BigInt, nullable: false), T.Col("Body", T.Text)], pk: ["Id"]),
            Enumerable.Range(1, 9).Select(i => new object?[] { (long)i, big }).ToArray());
        var target = new InMemoryDatabase(Dbms.PostgreSql).Add(T.Table("Big", [T.Col("Id", T.BigInt, nullable: false), T.Col("Body", T.Text)], pk: ["Id"]));

        await RunAsync(source, target, new RunOptions { BatchSize = 1000, MaxBatchBytes = 25_000 });

        // 1 行 約 10KB なので 3 行（約 30KB）で区切られる
        Assert.Equal([3, 3, 3], target.Batches.Select(b => b.Rows));
    }

    [Fact]
    public async Task 自動採番は変換元の次の番号と最大値の大きい方へ合わせる()
    {
        var (source, target) = Databases(5);
        source.SourceNextAutoIncrement["Items"] = 100; // 末尾の行が消えて、番号が最大値より進んでいる
        var (report, _) = await RunAsync(source, target);
        Assert.Equal(100, target.AdjustedAutoIncrement["Items"]);
        Assert.Equal(0, report.ExitCode);

        var (source2, target2) = Databases(5);
        source2.SourceNextAutoIncrement["Items"] = 3; // 古い値でも最大値 + 1 を下回らない
        await RunAsync(source2, target2);
        Assert.Equal(6, target2.AdjustedAutoIncrement["Items"]);

        var (source3, target3) = Databases(5); // 次の番号を読めない
        var (report3, _) = await RunAsync(source3, target3);
        Assert.Equal(6, target3.AdjustedAutoIncrement["Items"]);
        Assert.Contains(report3.Tables[0].Warnings, w => w.Contains("unavailable"));
    }

    [Fact]
    public async Task ドライランは何も書かない()
    {
        var (source, target) = Databases(7);
        var (report, log) = await RunAsync(source, target, new RunOptions { DryRun = true });

        Assert.Equal(0, target.WriteCalls);
        Assert.Empty(target.Rows("Items"));
        Assert.False(target.ProgressTableExists);
        var table = Assert.Single(report.Tables);
        Assert.Equal(TableStatus.DryRun, table.Status);
        Assert.Equal(7, table.SourceRows);
        Assert.Contains("[dry-run] Items: would copy 7 row(s)", log.Text);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public async Task ドライランでも定義の不一致は失敗として数える()
    {
        var source = new InMemoryDatabase(Dbms.SqlServer).Add(T.Table("T", [T.Col("A", T.Text)]));
        var target = new InMemoryDatabase(Dbms.PostgreSql).Add(T.Table("T", [T.Col("A", T.Text), T.Col("B", T.Int, nullable: false)]));
        var (report, _) = await RunAsync(source, target, new RunOptions { DryRun = true });
        Assert.Equal(1, report.ExitCode);
        Assert.Equal(0, target.WriteCalls);
    }

    [Fact]
    public async Task 変換先が空でなければ_何も書かずに失敗する()
    {
        var (source, target) = Databases(3);
        target.Add(TargetItems, new object?[] { 1L, "old", null, null });

        var (report, _) = await RunAsync(source, target);

        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.Tables[0].Errors, e => e.Contains("not empty") && e.Contains("--resume"));
        Assert.Equal(0, target.WriteCalls);
        Assert.Single(target.Rows("Items"));
    }

    [Fact]
    public async Task 再開では_完了した表をとばし_途中の表を入れ直す()
    {
        var a = T.Table("A", [T.Col("Id", T.BigInt, nullable: false)], pk: ["Id"]);
        var b = T.Table("B", [T.Col("Id", T.BigInt, nullable: false)], pk: ["Id"]);
        var source = new InMemoryDatabase(Dbms.SqlServer)
            .Add(a, new object?[] { 1L }, new object?[] { 2L })
            .Add(b, new object?[] { 1L }, new object?[] { 2L }, new object?[] { 3L });
        var target = new InMemoryDatabase(Dbms.PostgreSql)
            .Add(a, new object?[] { 1L }, new object?[] { 2L }) // 完了している
            .Add(b, new object?[] { 1L }); // 途中で止まった
        target.Completed.Add("A");

        var (report, _) = await RunAsync(source, target, new RunOptions { Resume = true });

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(TableStatus.Skipped, report.Tables.Single(t => t.Name == "A").Status);
        Assert.Equal(TableStatus.Copied, report.Tables.Single(t => t.Name == "B").Status);
        Assert.Equal(["B"], target.Cleared);
        Assert.Equal(3, target.Rows("B").Count);
        Assert.Contains("B", target.Completed);
    }

    [Fact]
    public async Task 完了の記録があっても件数が違えば失敗する()
    {
        var a = T.Table("A", [T.Col("Id", T.BigInt, nullable: false)], pk: ["Id"]);
        var source = new InMemoryDatabase(Dbms.SqlServer).Add(a, new object?[] { 1L }, new object?[] { 2L });
        var target = new InMemoryDatabase(Dbms.PostgreSql).Add(a, new object?[] { 1L });
        target.Completed.Add("A");

        var (report, _) = await RunAsync(source, target, new RunOptions { Resume = true });

        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.Tables[0].Errors, e => e.Contains("row count differs"));
    }

    [Fact]
    public async Task 変換できない値は_表_行_列を示す失敗にして_その行だけをとばす()
    {
        var (source, target) = Databases(5);
        source.Add(SourceItems, ItemRows(5).Select(r => r).Append([6L, new string('x', 21), null, null]).ToArray());

        var (report, _) = await RunAsync(source, target);

        var table = report.Tables[0];
        Assert.Equal(TableStatus.Failed, table.Status);
        Assert.Equal(1, table.FailedRows);
        Assert.Equal(5, table.CopiedRows);
        var failure = Assert.Single(table.Failures);
        Assert.Equal("Title", failure.Column);
        Assert.Contains("PK[ReferenceId=6]", failure.Location);
        Assert.Contains("exceeds", failure.Message);
        Assert.Equal(1, report.ExitCode);
        Assert.DoesNotContain("Items", target.Completed); // 失敗した表は完了を記録しない
        Assert.Equal(5, target.Rows("Items").Count);
    }

    [Fact]
    public async Task NULLをNOT_NULLの列へは入れない()
    {
        var (source, target) = Databases(0);
        source.Add(SourceItems, new object?[] { 1L, null, null, null });
        var (report, _) = await RunAsync(source, target);
        Assert.Contains(report.Tables[0].Failures, f => f.Column == "Title" && f.Message.Contains("NOT NULL"));
    }

    [Fact]
    public async Task 変換先が拒否した行は_主キーを示して失敗にする()
    {
        var (source, target) = Databases(5);
        target.RejectRow = (_, row) => (long)row[0]! == 3 ? "duplicate key value violates unique constraint" : null;

        var (report, _) = await RunAsync(source, target);

        var table = report.Tables[0];
        Assert.Equal(1, table.FailedRows);
        Assert.Equal(4, table.CopiedRows);
        Assert.Contains("PK[ReferenceId=3]", Assert.Single(table.Failures).Location);
        Assert.Equal(1, report.ExitCode);
        Assert.Equal(4, target.Rows("Items").Count);
    }

    [Fact]
    public async Task 読み戻した内容が違えば失敗として数える()
    {
        var (source, target) = Databases(5);
        target.CorruptOnWrite = (_, row) =>
        {
            var copy = (object?[])row.Clone();
            if ((long)copy[0]! == 2)
            {
                copy[1] = "tampered";
            }

            return copy;
        };

        var (report, _) = await RunAsync(source, target);

        var table = report.Tables[0];
        Assert.Equal(TableStatus.Failed, table.Status);
        Assert.Contains(table.Errors, e => e.Contains("verification failed") && e.Contains("'Title'"));
        Assert.Equal(1, report.ExitCode);
        Assert.DoesNotContain("Items", target.Completed);
    }

    [Fact]
    public async Task 丸めた値の数を数える()
    {
        var source = new InMemoryDatabase(Dbms.PostgreSql).Add(
            T.Table("T", [T.Col("Id", T.BigInt, nullable: false), T.Col("At", T.DateTime(6))], pk: ["Id"]),
            new object?[] { 1L, new DateTime(2024, 1, 1, 0, 0, 0, 1) },
            new object?[] { 2L, new DateTime(2024, 1, 1, 0, 0, 0, 3) },
            new object?[] { 3L, new DateTime(2024, 1, 1).AddTicks(10) });
        var target = new InMemoryDatabase(Dbms.SqlServer).Add(
            T.Table("T", [T.Col("Id", T.BigInt, nullable: false), T.Col("At", T.LegacyDateTime)], pk: ["Id"]));

        var (report, _) = await RunAsync(source, target);

        Assert.Equal(2, report.Tables[0].RoundedValues);
        Assert.Equal(0, report.ExitCode);
        Assert.Contains(report.Tables[0].Warnings, w => w.Contains("rounded"));
    }

    [Fact]
    public async Task 変換先にだけ無い表に行があれば失敗_空なら警告だけ()
    {
        var onlySource = T.Table("OnlySource", [T.Col("Id", T.BigInt)]);
        var source = new InMemoryDatabase(Dbms.SqlServer).Add(onlySource, new object?[] { 1L });
        var target = new InMemoryDatabase(Dbms.PostgreSql);
        var (report, _) = await RunAsync(source, target);
        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.Tables[0].Errors, e => e.Contains("only in the source"));

        var emptySource = new InMemoryDatabase(Dbms.SqlServer).Add(onlySource);
        var (report2, _) = await RunAsync(emptySource, new InMemoryDatabase(Dbms.PostgreSql));
        Assert.Equal(0, report2.ExitCode);
        Assert.Equal(TableStatus.Skipped, report2.Tables[0].Status);
    }

    [Fact]
    public async Task 制約を無効にして_終わったら戻す()
    {
        var (source, target) = Databases(3);
        var (report, _) = await RunAsync(source, target, new RunOptions { DisableConstraints = true });
        Assert.True(target.ConstraintsDisabled);
        Assert.True(target.ConstraintsRestored);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public async Task 完了したら管理表を消すオプション()
    {
        var (source, target) = Databases(3);
        var (report, _) = await RunAsync(source, target, new RunOptions { CleanupProgress = true });
        Assert.Equal(0, report.ExitCode);
        Assert.False(target.ProgressTableExists);

        var (source2, target2) = Databases(3);
        var (report2, _) = await RunAsync(source2, target2);
        Assert.Equal(0, report2.ExitCode);
        Assert.True(target2.ProgressTableExists);
    }

    [Fact]
    public async Task 失敗があれば管理表を消さない()
    {
        var (source, target) = Databases(3);
        target.RejectRow = (_, row) => (long)row[0]! == 1 ? "boom" : null;
        var (report, _) = await RunAsync(source, target, new RunOptions { CleanupProgress = true });
        Assert.Equal(1, report.ExitCode);
        Assert.True(target.ProgressTableExists);
    }

    [Fact]
    public async Task 親の表を先にコピーする()
    {
        var parent = T.Table("Z_Parent", [T.Col("Id", T.BigInt, nullable: false)], pk: ["Id"]);
        var child = T.Table("A_Child", [T.Col("Id", T.BigInt, nullable: false), T.Col("ParentId", T.BigInt)], pk: ["Id"], parents: ["Z_Parent"]);
        var source = new InMemoryDatabase(Dbms.SqlServer)
            .Add(parent, new object?[] { 1L })
            .Add(child, new object?[] { 1L, 1L });
        var target = new InMemoryDatabase(Dbms.PostgreSql).Add(parent).Add(child);

        await RunAsync(source, target);

        Assert.Equal(["Z_Parent", "A_Child"], target.Batches.Select(b => b.Table));
    }

    [Fact]
    public async Task 中断したら表は完了を記録せず_終了コードは失敗()
    {
        var (source, target) = Databases(3);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var log = new ListLog();
        var plan = ConversionPlanner.Plan(await source.ReadSchemaAsync(default), await target.ReadSchemaAsync(default), new PlanOptions());

        var report = await new ConversionEngine(source, target, log).RunAsync(plan, new RunOptions(), cts.Token);

        Assert.True(report.Cancelled);
        Assert.Equal(1, report.ExitCode);
        Assert.Empty(target.Completed);
    }

    [Fact]
    public async Task 結果の一覧は表ごとの件数と失敗を英語で出す()
    {
        var (source, target) = Databases(5);
        target.RejectRow = (_, row) => (long)row[0]! == 2 ? "boom" : null;
        var (report, _) = await RunAsync(source, target);
        var log = new ListLog();
        report.WriteSummary(log);

        Assert.Contains("=== Summary ===", log.Text);
        Assert.Contains("Source rows", log.Text);
        Assert.Contains("PK[ReferenceId=2]", log.Text);
        Assert.Contains("Result: FAILED", log.Text);
        Assert.Contains("1 failure(s)", log.Text);
    }

    [Fact]
    public void 行の位置の表記()
    {
        Assert.Equal("row #7 (no primary key)", ConversionEngine.DescribeRow([], ["A"], new object?[] { 1 }, 7));
        Assert.Equal("row #3 PK[Id=5, Name='x']", ConversionEngine.DescribeRow([0, 1], ["Id", "Name"], new object?[] { 5L, "x" }, 3));
    }
}
