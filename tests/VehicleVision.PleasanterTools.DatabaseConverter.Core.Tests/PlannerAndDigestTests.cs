using VehicleVision.PleasanterTools.DatabaseConverter.Core.Planning;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Schema;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Values;

namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.Tests;

public sealed class TableDigestTests
{
    private static TableDigest Digest(params object?[][] rows)
    {
        var digest = new TableDigest(["Id", "Name"], [T.BigInt, T.VarChar(50)], [0]);
        foreach (var row in rows)
        {
            digest.Add(row);
        }

        return digest;
    }

    [Fact]
    public void 行の順序が違っても同じになる()
        => Assert.Empty(Digest([1L, "a"], [2L, "b"]).Compare(Digest([2L, "b"], [1L, "a"])));

    [Fact]
    public void 件数の違いを見つける()
        => Assert.Contains(Digest([1L, "a"]).Compare(Digest([1L, "a"], [2L, "b"])), d => d.Contains("row count"));

    [Fact]
    public void 列の値の違いは列名つきで示す()
    {
        var differences = Digest([1L, "a"]).Compare(Digest([1L, "A"]));
        Assert.Contains(differences, d => d.Contains("'Name'"));
        Assert.DoesNotContain(differences, d => d.Contains("primary key"));
    }

    [Fact]
    public void 主キーの集合の違いを見つける()
        => Assert.Contains(Digest([1L, "a"]).Compare(Digest([2L, "a"])), d => d.Contains("primary key"));

    [Fact]
    public void 行をまたぐ値の入れ替えを見つける()
    {
        // 列ごとの合計は同じだが、行の組み合わせが違う
        var differences = Digest([1L, "a"], [2L, "b"]).Compare(Digest([1L, "b"], [2L, "a"]));
        Assert.NotEmpty(differences);
    }

    [Fact]
    public void NULLと空文字を区別する()
        => Assert.NotEmpty(Digest([1L, null]).Compare(Digest([1L, string.Empty])));

    [Fact]
    public void 固定長の文字列は末尾の空白を無視する()
    {
        var fixedType = new ColumnType(ValueKind.String, "char", 10, LengthUnit.CodePoints, IsFixedLength: true);
        Assert.Equal(TableDigest.Canonicalize("abc", fixedType), TableDigest.Canonicalize("abc       ", fixedType));
        Assert.NotEqual(TableDigest.Canonicalize("abc", T.VarChar(10)), TableDigest.Canonicalize("abc ", T.VarChar(10)));
    }

    [Fact]
    public void 数値と真偽と日時は型をまたいで同じ表記になる()
    {
        Assert.Equal("1", TableDigest.Canonicalize(true, T.Bit));
        Assert.Equal("1", TableDigest.Canonicalize((sbyte)1, T.Bit));
        Assert.Equal("12.5", TableDigest.Canonicalize(12.50m, T.Decimal(10, 4)));
        Assert.Equal(TableDigest.Canonicalize(5, T.BigInt), TableDigest.Canonicalize(5L, T.BigInt));
        Assert.Equal(TableDigest.Canonicalize(new DateTime(2024, 1, 1), T.DateTime(3)), TableDigest.Canonicalize(new DateTime(2024, 1, 1), T.LegacyDateTime));
    }

    [Fact]
    public void 単精度の列は単精度へ丸めてから比べる()
    {
        var single = new ColumnType(ValueKind.Float, "float", IsSingle: true);
        Assert.Equal(TableDigest.Canonicalize(0.1d, single), TableDigest.Canonicalize(0.1f, single));
    }

    [Fact]
    public void バイナリは中身で比べる()
    {
        var digest1 = new TableDigest(["B"], [T.Bytes], []);
        digest1.Add([new byte[] { 1, 2, 3 }]);
        var digest2 = new TableDigest(["B"], [T.Bytes], []);
        digest2.Add([new byte[] { 1, 2, 4 }]);
        Assert.NotEmpty(digest1.Compare(digest2));
    }
}

public sealed class ConversionPlannerTests
{
    private static ConversionPlan Plan(SchemaInfo source, SchemaInfo target, PlanOptions? options = null)
        => ConversionPlanner.Plan(source, target, options ?? new PlanOptions());

    private static TableInfo Items(Dbms dbms, params ColumnInfo[] extra)
        => T.Table(
            "Items",
            [T.Col("ReferenceId", T.BigInt, nullable: false, auto: true), T.Col("Title", T.VarChar(100)), .. extra],
            pk: ["ReferenceId"]);

    [Fact]
    public void 同名の列を突き合わせる()
    {
        var plan = Plan(
            T.Schema(Dbms.SqlServer, Items(Dbms.SqlServer)),
            T.Schema(Dbms.PostgreSql, Items(Dbms.PostgreSql)));

        var table = Assert.Single(plan.Tables);
        Assert.True(table.IsRunnable);
        Assert.Equal(["ReferenceId", "Title"], table.Mappings.Select(m => m.Target.Name));
        Assert.Equal("ReferenceId", table.AutoIncrement?.Name);
        Assert.True(table.AutoIncrementIsMapped);
        Assert.Equal([0], table.SourceKeyIndexes);
    }

    [Fact]
    public void 大文字小文字だけが違う表と列も突き合わせる()
    {
        var source = T.Schema(Dbms.SqlServer, T.Table("Items", [T.Col("Id", T.BigInt), T.Col("Title", T.Text)]));
        var target = T.Schema(Dbms.MySql, T.Table("items", [T.Col("id", T.BigInt), T.Col("title", T.Text)]));
        var table = Assert.Single(Plan(source, target).Tables);
        Assert.True(table.IsRunnable);
        Assert.Equal("items", table.Target!.Name);
    }

    [Fact]
    public void 変換先にだけある列は_NULLか既定値があれば警告_無ければエラー()
    {
        var source = T.Schema(Dbms.SqlServer, T.Table("T", [T.Col("A", T.Text)]));

        var okTarget = T.Schema(Dbms.PostgreSql, T.Table("T", [T.Col("A", T.Text), T.Col("B", T.Text, nullable: true), T.Col("C", T.Int, nullable: false, hasDefault: true)]));
        var ok = Assert.Single(Plan(source, okTarget).Tables);
        Assert.True(ok.IsRunnable);
        Assert.Equal(2, ok.Warnings.Count(w => w.Contains("does not exist in the source")));

        var ngTarget = T.Schema(Dbms.PostgreSql, T.Table("T", [T.Col("A", T.Text), T.Col("D", T.Int, nullable: false)]));
        var ng = Assert.Single(Plan(source, ngTarget).Tables);
        Assert.False(ng.IsRunnable);
        Assert.Contains(ng.Errors, e => e.Contains("'D'") && e.Contains("NOT NULL"));
    }

    [Fact]
    public void 変換元にだけある列は無視して警告する()
    {
        var source = T.Schema(Dbms.SqlServer, T.Table("T", [T.Col("A", T.Text), T.Col("Extra", T.Text)]));
        var target = T.Schema(Dbms.PostgreSql, T.Table("T", [T.Col("A", T.Text)]));
        var table = Assert.Single(Plan(source, target).Tables);
        Assert.True(table.IsRunnable);
        Assert.Single(table.Mappings);
        Assert.Contains(table.Warnings, w => w.Contains("'Extra'"));
    }

    [Fact]
    public void 行バージョンの列は入れない()
    {
        var rowVersion = new ColumnType(ValueKind.Binary, "timestamp", 8, LengthUnit.Bytes);
        var source = T.Schema(Dbms.SqlServer, T.Table("T", [T.Col("A", T.Text), T.Col("RowVer", rowVersion, readOnly: true)]));
        var target = T.Schema(Dbms.PostgreSql, T.Table("T", [T.Col("A", T.Text), T.Col("RowVer", T.Bytes)]));
        var table = Assert.Single(Plan(source, target).Tables);
        Assert.Single(table.Mappings);
        Assert.Contains(table.Warnings, w => w.Contains("RowVer") && w.Contains("row version"));
    }

    [Fact]
    public void 型が対応しない列は表をエラーにする()
    {
        var source = T.Schema(Dbms.SqlServer, T.Table("T", [T.Col("A", T.Bytes)]));
        var target = T.Schema(Dbms.PostgreSql, T.Table("T", [T.Col("A", T.DateTime(3))]));
        var table = Assert.Single(Plan(source, target).Tables);
        Assert.False(table.IsRunnable);
        Assert.Contains(table.Errors, e => e.Contains("'A'"));
    }

    [Fact]
    public void 共通の列が無ければエラー()
    {
        var source = T.Schema(Dbms.SqlServer, T.Table("T", [T.Col("A", T.Text)]));
        var target = T.Schema(Dbms.PostgreSql, T.Table("T", [T.Col("B", T.Text)]));
        Assert.Contains(Assert.Single(Plan(source, target).Tables).Errors, e => e.Contains("no columns in common"));
    }

    [Fact]
    public void 変換先に無い表は_SourceOnly_で_変換元に無い表は_TargetOnly()
    {
        var source = T.Schema(Dbms.SqlServer, Items(Dbms.SqlServer), T.Table("OnlySource", [T.Col("A", T.Text)]));
        var target = T.Schema(Dbms.PostgreSql, Items(Dbms.PostgreSql), T.Table("OnlyTarget", [T.Col("A", T.Text)]));
        var plan = Plan(source, target);
        Assert.Contains(plan.Tables, t => t.Name == "OnlySource" && t.IsSourceOnly);
        Assert.Equal(["OnlyTarget"], plan.TargetOnlyTables);
        Assert.Equal("OnlySource", plan.Tables[^1].Name);
    }

    [Fact]
    public void 親の表を先に置く()
    {
        var a = T.Table("A_child", [T.Col("Id", T.BigInt), T.Col("ParentId", T.BigInt)], parents: ["Z_parent"]);
        var z = T.Table("Z_parent", [T.Col("Id", T.BigInt)]);
        var plan = Plan(T.Schema(Dbms.SqlServer, a, z), T.Schema(Dbms.PostgreSql, a, z));
        Assert.Equal(["Z_parent", "A_child"], plan.Tables.Select(t => t.Name));
    }

    [Fact]
    public void 循環する外部キーは_制約を無効にしない限りエラー()
    {
        var a = T.Table("A", [T.Col("Id", T.BigInt)], parents: ["B"]);
        var b = T.Table("B", [T.Col("Id", T.BigInt)], parents: ["A"]);
        var self = T.Table("S", [T.Col("Id", T.BigInt)], parents: ["S"]);
        var source = T.Schema(Dbms.SqlServer, a, b, self);
        var target = T.Schema(Dbms.PostgreSql, a, b, self);

        var strict = Plan(source, target);
        Assert.All(strict.Tables, t => Assert.Contains(t.Errors, e => e.Contains("--disable-constraints")));

        var relaxed = Plan(source, target, new PlanOptions { DisableConstraints = true });
        Assert.All(relaxed.Tables, t => Assert.True(t.IsRunnable));
    }

    [Fact]
    public void Quartzの表は既定で除外し_名指しなら対象にする()
    {
        var quartz = T.Table("QRTZ_LOCKS", [T.Col("A", T.Text)]);
        var items = Items(Dbms.SqlServer);
        var source = T.Schema(Dbms.SqlServer, quartz, items);
        var target = T.Schema(Dbms.PostgreSql, quartz, Items(Dbms.PostgreSql));

        var byDefault = Plan(source, target);
        Assert.DoesNotContain(byDefault.Tables, t => t.Name == "QRTZ_LOCKS");
        Assert.Contains(byDefault.ExcludedTables, e => e.StartsWith("QRTZ_LOCKS", StringComparison.Ordinal));

        var named = Plan(source, target, new PlanOptions { Tables = ["qrtz_locks"] });
        Assert.Equal(["QRTZ_LOCKS"], named.Tables.Select(t => t.Name));
    }

    [Fact]
    public void tables_と_exclude_tables_で絞り込む()
    {
        var a = T.Table("A", [T.Col("Id", T.BigInt)]);
        var b = T.Table("B", [T.Col("Id", T.BigInt)]);
        var c = T.Table("C", [T.Col("Id", T.BigInt)]);
        var source = T.Schema(Dbms.SqlServer, a, b, c);
        var target = T.Schema(Dbms.PostgreSql, a, b, c);

        Assert.Equal(["A", "C"], Plan(source, target, new PlanOptions { Tables = ["a", "C"] }).Tables.Select(t => t.Name));
        Assert.Equal(["A", "C"], Plan(source, target, new PlanOptions { ExcludeTables = ["B"] }).Tables.Select(t => t.Name));
        Assert.Contains(Plan(source, target, new PlanOptions { Tables = ["Nope"] }).Errors, e => e.Contains("'Nope'"));
    }

    [Fact]
    public void 管理表は対象にしない()
    {
        var progress = T.Table(DatabaseConverterConstants.ProgressTableName, [T.Col("TableName", T.Text)]);
        var source = T.Schema(Dbms.SqlServer, Items(Dbms.SqlServer));
        var target = T.Schema(Dbms.PostgreSql, Items(Dbms.PostgreSql), progress);
        var plan = Plan(source, target);
        Assert.Empty(plan.TargetOnlyTables);
        Assert.Single(plan.Tables);
    }

    [Fact]
    public void 自動採番の列が変換元に無ければコピーしない()
    {
        var source = T.Schema(Dbms.SqlServer, T.Table("T", [T.Col("A", T.Text)]));
        var target = T.Schema(Dbms.PostgreSql, T.Table("T", [T.Col("Id", T.BigInt, nullable: false, auto: true), T.Col("A", T.Text)]));
        var table = Assert.Single(Plan(source, target).Tables);
        Assert.NotNull(table.AutoIncrement);
        Assert.False(table.AutoIncrementIsMapped);
    }
}
