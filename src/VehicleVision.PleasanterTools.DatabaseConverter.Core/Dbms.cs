namespace VehicleVision.PleasanterTools.DatabaseConverter.Core;

/// <summary>対応する DBMS。</summary>
public enum Dbms
{
    SqlServer,
    PostgreSql,
    MySql,
}

/// <summary>DBMS 名の解析と表示。</summary>
public static class DbmsNames
{
    /// <summary>コマンドラインの名前（<c>sqlserver</c> / <c>postgresql</c> / <c>mysql</c>）から解析する。大文字小文字は区別しない。</summary>
    public static bool TryParse(string? text, out Dbms dbms)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "sqlserver":
                dbms = Dbms.SqlServer;
                return true;
            case "postgresql":
            case "postgres":
                dbms = Dbms.PostgreSql;
                return true;
            case "mysql":
                dbms = Dbms.MySql;
                return true;
            default:
                dbms = default;
                return false;
        }
    }

    /// <summary>コマンドラインで使う名前。</summary>
    public static string ToCliName(this Dbms dbms) => dbms switch
    {
        Dbms.SqlServer => "sqlserver",
        Dbms.PostgreSql => "postgresql",
        Dbms.MySql => "mysql",
        _ => throw new ArgumentOutOfRangeException(nameof(dbms)),
    };

    /// <summary>コンソールへ出す名前（英語）。</summary>
    public static string ToDisplayName(this Dbms dbms) => dbms switch
    {
        Dbms.SqlServer => "SQL Server",
        Dbms.PostgreSql => "PostgreSQL",
        Dbms.MySql => "MySQL",
        _ => throw new ArgumentOutOfRangeException(nameof(dbms)),
    };
}
