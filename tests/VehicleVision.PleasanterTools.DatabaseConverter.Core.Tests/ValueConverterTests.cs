using VehicleVision.PleasanterTools.DatabaseConverter.Core.Schema;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Values;

namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.Tests;

public sealed class ValueConverterTests
{
    private static object Convert(Dbms target, object value, ColumnType source, ColumnType targetType)
        => new ValueConverter(target).Convert(value, source, targetType, out _);

    private static string Fails(Dbms target, object value, ColumnType source, ColumnType targetType)
        => Assert.Throws<ValueConversionException>(() => Convert(target, value, source, targetType)).Message;

    // 真偽 -------------------------------------------------------------

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void 真偽は真偽と0か1の整数から変換できる(object input, bool expected)
        => Assert.Equal(expected, Convert(Dbms.PostgreSql, input, T.Bit, T.Bit));

    [Fact]
    public void 真偽へ0と1以外の整数は変換できない()
        => Assert.Contains("0 and 1", Fails(Dbms.PostgreSql, 2, T.Int, T.Bit));

    // 整数 -------------------------------------------------------------

    [Fact]
    public void 整数の範囲の境界()
    {
        Assert.Equal((long)int.MaxValue, Convert(Dbms.PostgreSql, (long)int.MaxValue, T.BigInt, T.Int));
        Assert.Equal((long)int.MinValue, Convert(Dbms.PostgreSql, (long)int.MinValue, T.BigInt, T.Int));
        Assert.Contains("out of range", Fails(Dbms.PostgreSql, (long)int.MaxValue + 1, T.BigInt, T.Int));
        Assert.Contains("out of range", Fails(Dbms.PostgreSql, (long)int.MinValue - 1, T.BigInt, T.Int));
    }

    [Fact]
    public void 符号なしの整数を符号付きへ入れると範囲外になる()
    {
        var tinyint = new ColumnType(ValueKind.Integer, "tinyint", IntegerMin: sbyte.MinValue, IntegerMax: sbyte.MaxValue);
        var unsigned = new ColumnType(ValueKind.Integer, "tinyint", IntegerMin: 0, IntegerMax: 255);
        Assert.Equal(127L, Convert(Dbms.MySql, (byte)127, unsigned, tinyint));
        Assert.Contains("out of range", Fails(Dbms.MySql, (byte)200, unsigned, tinyint));
    }

    [Fact]
    public void 整数へ小数部のある値は入れない()
        => Assert.Contains("fractional", Fails(Dbms.PostgreSql, 1.5m, T.Decimal(5, 2), T.BigInt));

    // decimal ----------------------------------------------------------

    [Fact]
    public void decimalの桁数に収まる値は末尾の0を落として渡す()
        => Assert.Equal(123.45m, Convert(Dbms.SqlServer, 123.4500m, T.Decimal(19, 4), T.Decimal(19, 4)));

    [Fact]
    public void decimalの小数部が多いと失敗する()
    {
        var message = Fails(Dbms.SqlServer, 1.23456m, T.Decimal(38, 10), T.Decimal(19, 4));
        Assert.Contains("fractional digits", message);
    }

    [Fact]
    public void decimalの整数部があふれると失敗する()
    {
        // (19,4) の整数部は 15 桁
        Assert.Equal(999999999999999.9999m, Convert(Dbms.SqlServer, 999999999999999.9999m, T.Decimal(38, 10), T.Decimal(19, 4)));
        Assert.Contains("integer digits", Fails(Dbms.SqlServer, 1000000000000000m, T.Decimal(38, 10), T.Decimal(19, 4)));
    }

    [Fact]
    public void decimalは小数部がゼロなら桁数の指定より長い表記でも入る()
        => Assert.Equal(1m, Convert(Dbms.SqlServer, 1.0000000000m, T.Decimal(38, 10), T.Decimal(19, 4)));

    [Fact]
    public void 負のdecimalも検査する()
        => Assert.Contains("integer digits", Fails(Dbms.MySql, -1000000000000000m, T.Decimal(38, 10), T.Decimal(19, 4)));

    // 浮動小数点 ---------------------------------------------------------

    [Fact]
    public void 倍精度から単精度へ丸めたら_rounded_を立てる()
    {
        var single = new ColumnType(ValueKind.Float, "float", IsSingle: true);
        var dbl = new ColumnType(ValueKind.Float, "float");
        var result = new ValueConverter(Dbms.MySql).Convert(0.1d, dbl, single, out var rounded);
        Assert.True(rounded);
        Assert.Equal((double)0.1f, result);
        new ValueConverter(Dbms.MySql).Convert(0.5d, dbl, single, out rounded);
        Assert.False(rounded);
    }

    [Fact]
    public void NaNはPostgreSQLだけが受け取る()
    {
        var dbl = new ColumnType(ValueKind.Float, "float");
        Assert.True(double.IsNaN((double)Convert(Dbms.PostgreSql, double.NaN, dbl, dbl)));
        Assert.Contains("NaN", Fails(Dbms.SqlServer, double.NaN, dbl, dbl));
        Assert.Contains("infinity", Fails(Dbms.MySql, double.PositiveInfinity, dbl, dbl));
    }

    // 文字列 -----------------------------------------------------------

    [Fact]
    public void NUL文字はPostgreSQLへ入れられない()
    {
        Assert.Contains("NUL", Fails(Dbms.PostgreSql, "a\0b", T.Text, T.Text));
        Assert.Equal("a\0b", Convert(Dbms.SqlServer, "a\0b", T.Text, T.Text));
        Assert.Equal("a\0b", Convert(Dbms.MySql, "a\0b", T.Text, T.Text));
    }

    [Fact]
    public void サロゲートペアは通り_対にならないサロゲートはSQLServer以外で失敗する()
    {
        const string emoji = "😀";
        Assert.Equal(emoji, Convert(Dbms.MySql, emoji, T.Text, T.Text));
        const string lone = "a\ud83dz";
        Assert.Contains("unpaired surrogate", Fails(Dbms.PostgreSql, lone, T.Text, T.Text));
        Assert.Contains("unpaired surrogate", Fails(Dbms.MySql, lone, T.Text, T.Text));
        Assert.Equal(lone, Convert(Dbms.SqlServer, lone, T.Text, T.Text));
    }

    [Fact]
    public void 文字列の長さは変換先の数え方で検査する()
    {
        // 😀 は UTF-16 では 2 単位、コードポイントでは 1、UTF-8 では 4 バイト
        const string emoji = "😀";
        var utf16 = new ColumnType(ValueKind.String, "nvarchar", 1, LengthUnit.Utf16Units);
        var codePoints = new ColumnType(ValueKind.String, "varchar", 1, LengthUnit.CodePoints);
        var bytes = new ColumnType(ValueKind.String, "text", 3, LengthUnit.Utf8Bytes);

        Assert.Equal(emoji, Convert(Dbms.PostgreSql, emoji, T.Text, codePoints));
        Assert.Contains("UTF-16 code units", Fails(Dbms.SqlServer, emoji, T.Text, utf16));
        Assert.Contains("UTF-8 bytes", Fails(Dbms.MySql, emoji, T.Text, bytes));
    }

    [Fact]
    public void 日本語の長さの境界()
    {
        var limit = new ColumnType(ValueKind.String, "varchar", 3, LengthUnit.CodePoints);
        Assert.Equal("日本語", Convert(Dbms.MySql, "日本語", T.Text, limit));
        Assert.Contains("exceeds", Fails(Dbms.MySql, "日本語です", T.Text, limit));
    }

    [Fact]
    public void GUIDは文字列になる()
    {
        var guid = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var uuid = new ColumnType(ValueKind.Guid, "uuid");
        var char36 = new ColumnType(ValueKind.String, "char", 36, LengthUnit.CodePoints, IsFixedLength: true);
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", Convert(Dbms.MySql, guid, uuid, char36));
        Assert.Equal(guid, Convert(Dbms.PostgreSql, "0F8FAD5B-D9CB-469F-A165-70867728950E", char36, uuid));
        Assert.Contains("GUID", Fails(Dbms.PostgreSql, "not-a-guid", char36, uuid));
    }

    // バイナリ ---------------------------------------------------------

    [Fact]
    public void バイナリの長さを検査する()
    {
        var blob = new ColumnType(ValueKind.Binary, "blob", 65535, LengthUnit.Bytes);
        Assert.NotNull(Convert(Dbms.MySql, new byte[65535], T.Bytes, blob));
        Assert.Contains("exceeds", Fails(Dbms.MySql, new byte[65536], T.Bytes, blob));
    }

    // 日時 -------------------------------------------------------------

    [Fact]
    public void SQLServerのdatetimeの範囲の境界()
    {
        var legacy = T.LegacyDateTime;
        var pg = T.DateTime(6);
        Assert.Equal(new DateTime(1753, 1, 1), Convert(Dbms.SqlServer, new DateTime(1753, 1, 1), pg, legacy));
        Assert.Contains("out of range", Fails(Dbms.SqlServer, new DateTime(1752, 12, 31, 23, 59, 59), pg, legacy));
        Assert.Contains("out of range", Fails(Dbms.SqlServer, DateTime.MinValue, pg, legacy));
        Assert.Equal(new DateTime(9999, 12, 31, 23, 59, 59, 997), Convert(Dbms.SqlServer, new DateTime(9999, 12, 31, 23, 59, 59, 997), pg, legacy));
        Assert.Contains("out of range", Fails(Dbms.SqlServer, new DateTime(9999, 12, 31, 23, 59, 59, 999), pg, legacy));
    }

    [Fact]
    public void SQLServerのdatetimeは3_33ミリ秒刻みへ丸める()
    {
        // .124 は 1/300 秒の刻み（.123 / .127 ...）へ丸まる
        var result = new ValueConverter(Dbms.SqlServer).Convert(new DateTime(2024, 2, 29, 12, 34, 56, 124), T.DateTime(3), T.LegacyDateTime, out var rounded);
        Assert.True(rounded);
        Assert.Equal(new DateTime(2024, 2, 29, 12, 34, 56, 123), result);
        // .003 は 1/300 秒の刻みで表せるので、丸めない
        var exact = new ValueConverter(Dbms.SqlServer).Convert(new DateTime(2024, 1, 1, 0, 0, 0, 3), T.DateTime(3), T.LegacyDateTime, out var exactRounded);
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, 3), exact);
        Assert.False(exactRounded);
    }

    [Fact]
    public void MySQLのdatetimeは1000年より前を受け取らない()
    {
        var mysql = T.DateTime(3);
        Assert.Equal(new DateTime(1000, 1, 1), Convert(Dbms.MySql, new DateTime(1000, 1, 1), T.DateTime(6), mysql));
        Assert.Contains("1000-01-01", Fails(Dbms.MySql, new DateTime(999, 12, 31), T.DateTime(6), mysql));
        Assert.Contains("1000-01-01", Fails(Dbms.MySql, DateTime.MinValue, T.DateTime(6), mysql));
    }

    [Fact]
    public void 西暦1年はPostgreSQLとdatetime2では保てる()
    {
        Assert.Equal(DateTime.MinValue, Convert(Dbms.PostgreSql, DateTime.MinValue, T.DateTime(6), T.DateTime(3)));
        var datetime2 = new ColumnType(ValueKind.DateTime, "datetime2", FractionDigits: 7);
        Assert.Equal(DateTime.MinValue, Convert(Dbms.SqlServer, DateTime.MinValue, T.DateTime(6), datetime2));
    }

    [Fact]
    public void 日時の小数秒は変換先の桁へ丸めて_rounded_を立てる()
    {
        var value = new DateTime(2024, 1, 2, 3, 4, 5).AddTicks(1_234_567);
        var result = new ValueConverter(Dbms.PostgreSql).Convert(value, T.DateTime(7), T.DateTime(3), out var rounded);
        Assert.True(rounded);
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5).AddTicks(1_230_000), result);

        var half = new DateTime(2024, 1, 2, 3, 4, 5).AddTicks(5_000);
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5).AddTicks(10_000), new ValueConverter(Dbms.PostgreSql).Convert(half, T.DateTime(7), T.DateTime(3), out _));
    }

    [Fact]
    public void 日時の丸めで9999年を超えたら失敗する()
    {
        var value = new DateTime(9999, 12, 31, 23, 59, 59).AddTicks(9_999_999);
        Assert.Contains("after rounding", Fails(Dbms.PostgreSql, value, T.DateTime(7), T.DateTime(3)));
    }

    [Fact]
    public void 日時のKindはUnspecifiedにそろえる()
    {
        var result = (DateTime)Convert(Dbms.PostgreSql, DateTime.SpecifyKind(new DateTime(2024, 1, 1), DateTimeKind.Utc), T.DateTime(6), T.DateTime(6));
        Assert.Equal(DateTimeKind.Unspecified, result.Kind);
    }

    // 変換可能の判定 -----------------------------------------------------

    [Theory]
    [InlineData(ValueKind.Binary, ValueKind.DateTime)]
    [InlineData(ValueKind.DateTime, ValueKind.String)]
    [InlineData(ValueKind.String, ValueKind.Integer)]
    [InlineData(ValueKind.Boolean, ValueKind.String)]
    public void 対応しない型の組み合わせは変換できない(ValueKind from, ValueKind to)
    {
        var message = ValueConverter.CheckSupported(new ColumnType(from, "a"), new ColumnType(to, "b"));
        Assert.NotNull(message);
    }

    [Fact]
    public void 扱わない型は変換できない()
    {
        Assert.Contains("not supported", ValueConverter.CheckSupported(new ColumnType(ValueKind.Unsupported, "xml"), T.Text));
        Assert.Contains("not supported", ValueConverter.CheckSupported(T.Text, new ColumnType(ValueKind.Unsupported, "json")));
    }

    [Fact]
    public void GUIDから短い文字列の列へは変換できない()
        => Assert.NotNull(ValueConverter.CheckSupported(new ColumnType(ValueKind.Guid, "uuid"), T.VarChar(35)));

    [Fact]
    public void 狭くなる変換は警告する()
    {
        Assert.NotNull(ValueConverter.DescribeRisk(T.Text, T.VarChar(100)));
        Assert.NotNull(ValueConverter.DescribeRisk(T.DateTime(6), T.LegacyDateTime));
        Assert.NotNull(ValueConverter.DescribeRisk(T.BigInt, T.Int));
        Assert.Null(ValueConverter.DescribeRisk(T.VarChar(10), T.VarChar(100)));
        Assert.NotNull(ValueConverter.DescribeRisk(T.Text, new ColumnType(ValueKind.String, "varchar", 10, LengthUnit.Utf16Units, IsUnicode: false)));
    }
}
