namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.Schema;

/// <summary>DBMS の差を吸収した値の種類。値の変換はこの種類の組み合わせで決まる。</summary>
public enum ValueKind
{
    Boolean,
    Integer,
    Decimal,
    Float,
    String,
    Binary,
    DateTime,
    Guid,

    /// <summary>このツールが扱わない型（日付のみ・時刻のみ・時間帯付き・XML・空間型など）。</summary>
    Unsupported,
}

/// <summary>文字列の長さの数え方。DBMS によって異なる。</summary>
public enum LengthUnit
{
    /// <summary>上限なし。</summary>
    None,

    /// <summary>UTF-16 のコード単位（SQL Server の nvarchar(n)）。</summary>
    Utf16Units,

    /// <summary>コードポイント（PostgreSQL の varchar(n)、MySQL の varchar(n)）。</summary>
    CodePoints,

    /// <summary>UTF-8 のバイト数（MySQL の text 系。utf8mb4 を前提とする）。</summary>
    Utf8Bytes,

    /// <summary>バイト数（バイナリ）。</summary>
    Bytes,
}

/// <summary>列の型。DBMS 固有の型名を、値の変換に必要な性質へ正規化したもの。</summary>
/// <param name="Kind">値の種類。</param>
/// <param name="NativeName">DBMS の型名（情報スキーマの値。小文字）。</param>
/// <param name="MaxLength">文字列・バイナリの上限。<see langword="null"/> は上限なし。</param>
/// <param name="Unit"><paramref name="MaxLength"/> の数え方。</param>
/// <param name="IsFixedLength">固定長（char / nchar / binary）か。</param>
/// <param name="IsUnicode">Unicode の文字列型か（SQL Server の varchar / char / text は false）。</param>
/// <param name="IntegerMin">整数の最小値。</param>
/// <param name="IntegerMax">整数の最大値。</param>
/// <param name="Precision">decimal の全体の桁数。0 は指定なし。</param>
/// <param name="Scale">decimal の小数部の桁数。</param>
/// <param name="IsSingle">単精度の浮動小数点か（SQL Server の real、PostgreSQL の real、MySQL の float）。</param>
/// <param name="FractionDigits">日時の小数秒の桁数（0〜7）。</param>
/// <param name="IsLegacyDateTime">SQL Server の datetime（1/300 秒刻み、1753-01-01 以降）か。</param>
public sealed record ColumnType(
    ValueKind Kind,
    string NativeName,
    long? MaxLength = null,
    LengthUnit Unit = LengthUnit.None,
    bool IsFixedLength = false,
    bool IsUnicode = true,
    long IntegerMin = long.MinValue,
    long IntegerMax = long.MaxValue,
    int Precision = 0,
    int Scale = 0,
    bool IsSingle = false,
    int FractionDigits = 0,
    bool IsLegacyDateTime = false)
{
    /// <summary>ログに出す短い表記。</summary>
    public string Describe()
    {
        return Kind switch
        {
            ValueKind.String or ValueKind.Binary when MaxLength is not null => $"{NativeName}({MaxLength})",
            ValueKind.Decimal when Precision > 0 => $"{NativeName}({Precision},{Scale})",
            ValueKind.DateTime when !IsLegacyDateTime => $"{NativeName}({FractionDigits})",
            _ => NativeName,
        };
    }
}

/// <summary>列。</summary>
/// <param name="Name">列名。</param>
/// <param name="Ordinal">表の中の位置（1 始まり）。</param>
/// <param name="Type">型。</param>
/// <param name="IsNullable">NULL を許すか。</param>
/// <param name="HasDefault">既定値があるか。</param>
/// <param name="IsAutoIncrement">自動採番（IDENTITY / sequence / AUTO_INCREMENT）か。</param>
/// <param name="IdentityAlways">PostgreSQL の <c>GENERATED ALWAYS AS IDENTITY</c> か（明示値の挿入に <c>OVERRIDING SYSTEM VALUE</c> が要る）。</param>
/// <param name="IsReadOnly">値を入れられない列（行バージョン、計算列、生成列）か。</param>
public sealed record ColumnInfo(
    string Name,
    int Ordinal,
    ColumnType Type,
    bool IsNullable,
    bool HasDefault,
    bool IsAutoIncrement,
    bool IdentityAlways,
    bool IsReadOnly);

/// <summary>表。</summary>
public sealed class TableInfo
{
    public TableInfo(
        string name,
        IReadOnlyList<ColumnInfo> columns,
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<string> referencedTables)
    {
        Name = name;
        Columns = columns;
        PrimaryKey = primaryKey;
        ReferencedTables = referencedTables;
    }

    /// <summary>表名。</summary>
    public string Name { get; }

    /// <summary>列（表の中の順）。</summary>
    public IReadOnlyList<ColumnInfo> Columns { get; }

    /// <summary>主キーの列名（キーの中の順）。無ければ空。</summary>
    public IReadOnlyList<string> PrimaryKey { get; }

    /// <summary>この表の外部キーが参照する表（親）。自己参照を含みうる。</summary>
    public IReadOnlyList<string> ReferencedTables { get; }

    /// <summary>列名（完全一致 → 大文字小文字を無視、の順）で列を探す。曖昧なら <see langword="null"/>。</summary>
    public ColumnInfo? FindColumn(string name) => NameMatcher.Find(Columns, name, c => c.Name);
}

/// <summary>DB の表と列の一覧（現在のスキーマ）。</summary>
public sealed class SchemaInfo
{
    public SchemaInfo(Dbms dbms, IReadOnlyList<TableInfo> tables)
    {
        Dbms = dbms;
        Tables = tables;
    }

    public Dbms Dbms { get; }

    public IReadOnlyList<TableInfo> Tables { get; }

    /// <summary>表名（完全一致 → 大文字小文字を無視、の順）で表を探す。曖昧なら <see langword="null"/>。</summary>
    public TableInfo? FindTable(string name) => NameMatcher.Find(Tables, name, t => t.Name);
}

/// <summary>名前の突き合わせ。完全一致を優先し、無ければ大文字小文字を無視して一意なものを採る。</summary>
internal static class NameMatcher
{
    public static T? Find<T>(IReadOnlyList<T> items, string name, Func<T, string> selector)
        where T : class
    {
        foreach (var item in items)
        {
            if (string.Equals(selector(item), name, StringComparison.Ordinal))
            {
                return item;
            }
        }

        T? found = null;
        foreach (var item in items)
        {
            if (!string.Equals(selector(item), name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found is not null)
            {
                return null;
            }

            found = item;
        }

        return found;
    }
}
