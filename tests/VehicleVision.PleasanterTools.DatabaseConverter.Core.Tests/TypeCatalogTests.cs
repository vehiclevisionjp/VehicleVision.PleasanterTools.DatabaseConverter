using VehicleVision.PleasanterTools.DatabaseConverter.Core.Schema;

namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.Tests;

public sealed class TypeCatalogTests
{
    [Theory]
    [InlineData(Dbms.SqlServer, "bit", "bit")]
    [InlineData(Dbms.PostgreSql, "boolean", "boolean")]
    [InlineData(Dbms.MySql, "tinyint", "tinyint(1)")]
    public void 真偽は3つのDBMSとも_Boolean_になる(Dbms dbms, string dataType, string columnType)
        => Assert.Equal(ValueKind.Boolean, TypeCatalog.Describe(dbms, dataType, columnType, null, null, null, null).Kind);

    [Fact]
    public void MySQLのtinyintは長さ1以外なら整数になる()
    {
        var type = TypeCatalog.Describe(Dbms.MySql, "tinyint", "tinyint", null, 3, 0, null);
        Assert.Equal(ValueKind.Integer, type.Kind);
        Assert.Equal(-128, type.IntegerMin);
        Assert.Equal(127, type.IntegerMax);
    }

    [Fact]
    public void MySQLのunsignedの範囲()
    {
        var type = TypeCatalog.Describe(Dbms.MySql, "int", "int unsigned", null, 10, 0, null);
        Assert.Equal(0, type.IntegerMin);
        Assert.Equal(uint.MaxValue, type.IntegerMax);
        Assert.Equal(ValueKind.Unsupported, TypeCatalog.Describe(Dbms.MySql, "bigint", "bigint unsigned", null, 20, 0, null).Kind);
    }

    [Fact]
    public void SQLServerのtinyintは0から255()
    {
        var type = TypeCatalog.Describe(Dbms.SqlServer, "tinyint", null, null, 3, 0, null);
        Assert.Equal(0, type.IntegerMin);
        Assert.Equal(255, type.IntegerMax);
    }

    [Theory]
    [InlineData(Dbms.SqlServer, "nvarchar", -1L)]
    [InlineData(Dbms.PostgreSql, "text", null)]
    [InlineData(Dbms.MySql, "longtext", 4294967295L)]
    public void 長い文字列は上限なしになる(Dbms dbms, string dataType, long? length)
    {
        var type = TypeCatalog.Describe(dbms, dataType, dataType, length, null, null, null);
        Assert.Equal(ValueKind.String, type.Kind);
        Assert.Null(type.MaxLength);
    }

    [Fact]
    public void 文字列の長さの数え方はDBMSごとに違う()
    {
        Assert.Equal(LengthUnit.Utf16Units, TypeCatalog.Describe(Dbms.SqlServer, "nvarchar", null, 100, null, null, null).Unit);
        Assert.Equal(LengthUnit.CodePoints, TypeCatalog.Describe(Dbms.PostgreSql, "character varying", null, 100, null, null, null).Unit);
        Assert.Equal(LengthUnit.CodePoints, TypeCatalog.Describe(Dbms.MySql, "varchar", "varchar(100)", 100, null, null, null).Unit);
        var text = TypeCatalog.Describe(Dbms.MySql, "text", "text", 65535, null, null, null);
        Assert.Equal(LengthUnit.Utf8Bytes, text.Unit);
        Assert.Equal(65535, text.MaxLength);
    }

    [Fact]
    public void SQLServerのvarcharはUnicodeではない()
    {
        Assert.False(TypeCatalog.Describe(Dbms.SqlServer, "varchar", null, 10, null, null, null).IsUnicode);
        Assert.True(TypeCatalog.Describe(Dbms.SqlServer, "nvarchar", null, 10, null, null, null).IsUnicode);
    }

    [Fact]
    public void 日時の型()
    {
        var legacy = TypeCatalog.Describe(Dbms.SqlServer, "datetime", null, null, null, null, 3);
        Assert.True(legacy.IsLegacyDateTime);
        var datetime2 = TypeCatalog.Describe(Dbms.SqlServer, "datetime2", null, null, null, null, 7);
        Assert.False(datetime2.IsLegacyDateTime);
        Assert.Equal(7, datetime2.FractionDigits);
        Assert.Equal(3, TypeCatalog.Describe(Dbms.PostgreSql, "timestamp without time zone", null, null, null, null, 3).FractionDigits);
        Assert.Equal(3, TypeCatalog.Describe(Dbms.MySql, "datetime", "datetime(3)", null, null, null, 3).FractionDigits);
    }

    [Theory]
    [InlineData(Dbms.PostgreSql, "timestamp with time zone")]
    [InlineData(Dbms.SqlServer, "datetimeoffset")]
    [InlineData(Dbms.SqlServer, "xml")]
    [InlineData(Dbms.MySql, "json")]
    [InlineData(Dbms.MySql, "timestamp")]
    public void 扱わない型はUnsupported(Dbms dbms, string dataType)
        => Assert.Equal(ValueKind.Unsupported, TypeCatalog.Describe(dbms, dataType, dataType, null, null, null, null).Kind);

    [Fact]
    public void SQLServerのimageは上限なしのバイナリ()
    {
        var type = TypeCatalog.Describe(Dbms.SqlServer, "image", null, 2147483647, null, null, null);
        Assert.Equal(ValueKind.Binary, type.Kind);
        Assert.Null(type.MaxLength);
    }

    [Fact]
    public void MySQLのblobは64KBの上限を持つ()
    {
        var blob = TypeCatalog.Describe(Dbms.MySql, "blob", "blob", 65535, null, null, null);
        Assert.Equal(65535, blob.MaxLength);
        Assert.Null(TypeCatalog.Describe(Dbms.MySql, "longblob", "longblob", 4294967295, null, null, null).MaxLength);
    }

    [Fact]
    public void 単精度の浮動小数点()
    {
        Assert.True(TypeCatalog.Describe(Dbms.MySql, "float", "float", null, 12, null, null).IsSingle);
        Assert.False(TypeCatalog.Describe(Dbms.MySql, "double", "double", null, 22, null, null).IsSingle);
        Assert.True(TypeCatalog.Describe(Dbms.SqlServer, "real", null, null, 24, null, null).IsSingle);
        Assert.False(TypeCatalog.Describe(Dbms.SqlServer, "float", null, null, 53, null, null).IsSingle);
        Assert.True(TypeCatalog.Describe(Dbms.PostgreSql, "real", null, null, 24, null, null).IsSingle);
    }

    [Fact]
    public void 行バージョンは8バイトの固定長バイナリ()
    {
        var type = TypeCatalog.Describe(Dbms.SqlServer, "timestamp", null, 8, null, null, null);
        Assert.Equal(ValueKind.Binary, type.Kind);
        Assert.Equal(8, type.MaxLength);
    }

    [Fact]
    public void decimalの桁数()
    {
        var type = TypeCatalog.Describe(Dbms.SqlServer, "decimal", null, null, 19, 4, null);
        Assert.Equal(19, type.Precision);
        Assert.Equal(4, type.Scale);
        Assert.Equal(0, TypeCatalog.Describe(Dbms.PostgreSql, "numeric", null, null, null, null, null).Precision);
    }
}
