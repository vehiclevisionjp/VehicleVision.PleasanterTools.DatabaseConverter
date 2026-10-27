using System.Data;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using NpgsqlTypes;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Databases;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Schema;

namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.Tests;

/// <summary>SQL の組み立て（DB なし）。識別子のエスケープ、挿入文、パラメータの型。</summary>
public sealed class DialectTests
{
    [Theory]
    [InlineData(Dbms.SqlServer, "Items", "[Items]")]
    [InlineData(Dbms.SqlServer, "a]b", "[a]]b]")]
    [InlineData(Dbms.PostgreSql, "Items", "\"Items\"")]
    [InlineData(Dbms.PostgreSql, "a\"b", "\"a\"\"b\"")]
    [InlineData(Dbms.MySql, "Items", "`Items`")]
    [InlineData(Dbms.MySql, "a`b", "`a``b`")]
    public void 識別子は引用符で囲み_引用符自身をエスケープする(Dbms dbms, string identifier, string expected)
        => Assert.Equal(expected, AdoDatabaseOpener.CreateDialect(dbms).QuoteIdentifier(identifier));

    [Theory]
    [InlineData(Dbms.SqlServer)]
    [InlineData(Dbms.PostgreSql)]
    [InlineData(Dbms.MySql)]
    public void 識別子に危険な文字があっても_エスケープ済みの引用符つき識別子としてだけ現れる(Dbms dbms)
    {
        var dialect = AdoDatabaseOpener.CreateDialect(dbms);
        var evil = "x\"]`; DROP TABLE Items; --";
        var select = dialect.BuildSelectSql(evil, [evil], [evil]);
        var quoted = dialect.QuoteIdentifier(evil);
        Assert.Equal($"SELECT {quoted} FROM {quoted} ORDER BY {quoted}", select);
    }

    [Fact]
    public void SQLServerの挿入文は複数行でパラメータを順に振る()
    {
        var dialect = new SqlServerDialect();
        var columns = new[] { T.Col("Id", T.BigInt), T.Col("Title", T.Text) };
        Assert.Equal(
            "INSERT INTO [Items] ([Id], [Title]) VALUES (@p0, @p1), (@p2, @p3)",
            dialect.BuildInsertSql("Items", columns, 2, overridingSystemValue: true));
    }

    [Fact]
    public void PostgreSQLのALWAYS_IDENTITYにはOVERRIDINGを付ける()
    {
        var dialect = new PostgreSqlDialect();
        var columns = new[] { T.Col("Id", T.BigInt) };
        Assert.Equal("INSERT INTO \"Items\" (\"Id\") OVERRIDING SYSTEM VALUE VALUES (@p0)", dialect.BuildInsertSql("Items", columns, 1, true));
        Assert.Equal("INSERT INTO \"Items\" (\"Id\") VALUES (@p0)", dialect.BuildInsertSql("Items", columns, 1, false));
    }

    [Fact]
    public void MySQLの挿入文はバッククォートで囲む()
        => Assert.Equal(
            "INSERT INTO `Items` (`Id`) VALUES (@p0), (@p1), (@p2)",
            new MySqlDialect().BuildInsertSql("Items", [T.Col("Id", T.BigInt)], 3, false));

    [Fact]
    public void 読み取りの文は主キーで並べる()
    {
        Assert.Equal("SELECT [A], [B] FROM [T] ORDER BY [A]", new SqlServerDialect().BuildSelectSql("T", ["A", "B"], ["A"]));
        Assert.Equal("SELECT \"A\" FROM \"T\"", new PostgreSqlDialect().BuildSelectSql("T", ["A"], []));
        Assert.Equal("SELECT COUNT_BIG(*) FROM [T]", new SqlServerDialect().BuildCountSql("T"));
    }

    [Fact]
    public void SQLServerのパラメータは変換先の型に合わせる()
    {
        var d = new SqlServerDialect();
        var datetime = (SqlParameter)d.CreateParameter("@p", T.LegacyDateTime, new DateTime(2024, 1, 1));
        Assert.Equal(SqlDbType.DateTime, datetime.SqlDbType);
        var datetime2 = (SqlParameter)d.CreateParameter("@p", new ColumnType(ValueKind.DateTime, "datetime2", FractionDigits: 7), new DateTime(2024, 1, 1));
        Assert.Equal(SqlDbType.DateTime2, datetime2.SqlDbType);
        var dec = (SqlParameter)d.CreateParameter("@p", T.Decimal(19, 4), 1.5m);
        Assert.Equal((byte)19, dec.Precision);
        Assert.Equal((byte)4, dec.Scale);
        var big = (SqlParameter)d.CreateParameter("@p", T.Text, new string('a', 5000));
        Assert.Equal(-1, big.Size);
        Assert.Equal(SqlDbType.NVarChar, big.SqlDbType);
        var bit = (SqlParameter)d.CreateParameter("@p", T.Bit, true);
        Assert.Equal(SqlDbType.Bit, bit.SqlDbType);
        var bin = (SqlParameter)d.CreateParameter("@p", T.Bytes, new byte[20000]);
        Assert.Equal(-1, bin.Size);
        var nullParameter = d.CreateParameter("@p", T.Text, null);
        Assert.Equal(DBNull.Value, nullParameter.Value);
        var tiny = (SqlParameter)d.CreateParameter("@p", new ColumnType(ValueKind.Integer, "tinyint", IntegerMin: 0, IntegerMax: 255), 200L);
        Assert.Equal(SqlDbType.TinyInt, tiny.SqlDbType);
        Assert.Equal((byte)200, tiny.Value);
        var varchar = (SqlParameter)d.CreateParameter("@p", new ColumnType(ValueKind.String, "varchar", 10, LengthUnit.Utf16Units, IsUnicode: false), "a");
        Assert.Equal(SqlDbType.VarChar, varchar.SqlDbType);
    }

    [Fact]
    public void PostgreSQLのパラメータは変換先の型に合わせる()
    {
        var d = new PostgreSqlDialect();
        Assert.Equal(NpgsqlDbType.Timestamp, ((NpgsqlParameter)d.CreateParameter("@p", T.DateTime(3), new DateTime(2024, 1, 1))).NpgsqlDbType);
        Assert.Equal(NpgsqlDbType.Bytea, ((NpgsqlParameter)d.CreateParameter("@p", T.Bytes, new byte[1])).NpgsqlDbType);
        Assert.Equal(NpgsqlDbType.Boolean, ((NpgsqlParameter)d.CreateParameter("@p", T.Bit, false)).NpgsqlDbType);
        var smallint = (NpgsqlParameter)d.CreateParameter("@p", new ColumnType(ValueKind.Integer, "smallint", IntegerMin: short.MinValue, IntegerMax: short.MaxValue), 5L);
        Assert.Equal(NpgsqlDbType.Smallint, smallint.NpgsqlDbType);
        Assert.Equal((short)5, smallint.Value);
        Assert.Equal(NpgsqlDbType.Uuid, ((NpgsqlParameter)d.CreateParameter("@p", new ColumnType(ValueKind.Guid, "uuid"), Guid.NewGuid())).NpgsqlDbType);
    }

    [Fact]
    public void MySQLのパラメータは変換先の型に合わせる()
    {
        var d = new MySqlDialect();
        Assert.Equal(MySqlDbType.DateTime, ((MySqlParameter)d.CreateParameter("@p", T.DateTime(3), new DateTime(2024, 1, 1))).MySqlDbType);
        Assert.Equal(MySqlDbType.Bool, ((MySqlParameter)d.CreateParameter("@p", T.Bit, true)).MySqlDbType);
        var single = (MySqlParameter)d.CreateParameter("@p", new ColumnType(ValueKind.Float, "float", IsSingle: true), 0.5d);
        Assert.IsType<float>(single.Value);
    }

    [Theory]
    [InlineData(Dbms.SqlServer, "Server=x;Password=secret;Bogus=1")]
    [InlineData(Dbms.PostgreSql, "Host=x;Password=secret;Bogus=1")]
    [InlineData(Dbms.MySql, "Server=x;Password=secret;Bogus=1")]
    public void 不正な接続文字列の例外に接続文字列の断片を含めない(Dbms dbms, string connectionString)
    {
        var ex = Assert.Throws<ConnectionStringException>(() => AdoDatabaseOpener.CreateDialect(dbms).CreateConnection(connectionString, 30));
        Assert.DoesNotContain("secret", ex.Message);
        Assert.DoesNotContain("Bogus", ex.Message);
    }

    [Fact]
    public void 変換元の接続は_PostgreSQLとMySQLで読み取り専用にする()
    {
        Assert.Contains("SET default_transaction_read_only = on", new PostgreSqlDialect().SessionStatements(true));
        Assert.Empty(new PostgreSqlDialect().SessionStatements(false));
        Assert.Contains("SET SESSION TRANSACTION READ ONLY", new MySqlDialect().SessionStatements(true));
        Assert.Contains(new MySqlDialect().SessionStatements(false), s => s.Contains("STRICT_ALL_TABLES") && s.Contains("NO_AUTO_VALUE_ON_ZERO"));
    }

    [Fact]
    public void MySQLの接続はCHAR36を文字列として読み_utf8mb4にする()
    {
        var connection = new MySqlDialect().CreateConnection("Server=x;User=u;Password=p", 30);
        var builder = new MySqlConnectionStringBuilder(connection.ConnectionString);
        Assert.Equal(MySqlGuidFormat.None, builder.GuidFormat);
        Assert.Equal("utf8mb4", builder.CharacterSet);
    }
}
