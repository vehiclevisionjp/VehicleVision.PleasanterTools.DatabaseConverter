namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.Schema;

/// <summary>
/// 情報スキーマの型情報を <see cref="ColumnType"/> へ正規化する。
/// 型の対応表（_documents/アーキテクチャ方針.md「型の変換表」）のうち、「DBMS の型 → 値の種類」の側。
/// </summary>
public static class TypeCatalog
{
    /// <summary>これ以上の長さは「上限なし」とみなす（SQL Server の image / text の 2GB、MySQL の longtext の 4GB など）。</summary>
    private const long UnlimitedThreshold = 1_073_741_823;

    /// <summary>型を正規化する。</summary>
    /// <param name="dbms">この型を持つ DBMS。</param>
    /// <param name="dataType">情報スキーマの DATA_TYPE（大文字小文字は問わない）。</param>
    /// <param name="columnType">MySQL の COLUMN_TYPE（tinyint(1)、int unsigned など）。他の DBMS は DATA_TYPE と同じでよい。</param>
    /// <param name="characterMaximumLength">CHARACTER_MAXIMUM_LENGTH。</param>
    /// <param name="numericPrecision">NUMERIC_PRECISION。</param>
    /// <param name="numericScale">NUMERIC_SCALE。</param>
    /// <param name="datetimePrecision">DATETIME_PRECISION。</param>
    public static ColumnType Describe(
        Dbms dbms,
        string dataType,
        string? columnType,
        long? characterMaximumLength,
        int? numericPrecision,
        int? numericScale,
        int? datetimePrecision)
    {
        var name = dataType.Trim().ToLowerInvariant();
        var full = (columnType ?? name).Trim().ToLowerInvariant();
        return dbms switch
        {
            Dbms.SqlServer => DescribeSqlServer(name, characterMaximumLength, numericPrecision, numericScale, datetimePrecision),
            Dbms.PostgreSql => DescribePostgreSql(name, characterMaximumLength, numericPrecision, numericScale, datetimePrecision),
            Dbms.MySql => DescribeMySql(name, full, characterMaximumLength, numericPrecision, numericScale, datetimePrecision),
            _ => throw new ArgumentOutOfRangeException(nameof(dbms)),
        };
    }

    private static long? Limit(long? length)
        => length is null || length <= 0 || length >= UnlimitedThreshold ? null : length;

    private static ColumnType Unsupported(string name) => new(ValueKind.Unsupported, name);

    private static ColumnType Integer(string name, long min, long max)
        => new(ValueKind.Integer, name, IntegerMin: min, IntegerMax: max);

    private static ColumnType Decimal(string name, int? precision, int? scale)
        => new(ValueKind.Decimal, name, Precision: precision ?? 0, Scale: scale ?? 0);

    private static ColumnType VarString(string name, long? length, LengthUnit unit, bool unicode)
    {
        var limit = Limit(length);
        return new ColumnType(ValueKind.String, name, limit, limit is null ? LengthUnit.None : unit, IsUnicode: unicode);
    }

    private static ColumnType VarBinary(string name, long? length)
    {
        var limit = Limit(length);
        return new ColumnType(ValueKind.Binary, name, limit, limit is null ? LengthUnit.None : LengthUnit.Bytes);
    }

    private static ColumnType DescribeSqlServer(
        string name, long? length, int? precision, int? scale, int? dtPrecision)
    {
        switch (name)
        {
            case "bit":
                return new ColumnType(ValueKind.Boolean, name);
            case "tinyint":
                return Integer(name, 0, 255);
            case "smallint":
                return Integer(name, short.MinValue, short.MaxValue);
            case "int":
                return Integer(name, int.MinValue, int.MaxValue);
            case "bigint":
                return Integer(name, long.MinValue, long.MaxValue);
            case "decimal":
            case "numeric":
                return Decimal(name, precision, scale);
            case "money":
                return Decimal(name, 19, 4);
            case "smallmoney":
                return Decimal(name, 10, 4);
            case "float":
                return new ColumnType(ValueKind.Float, name);
            case "real":
                return new ColumnType(ValueKind.Float, name, IsSingle: true);
            case "char":
            case "nchar":
                return new ColumnType(ValueKind.String, name, Limit(length), LengthUnit.Utf16Units, IsFixedLength: true, IsUnicode: name == "nchar");
            case "varchar":
            case "nvarchar":
                return VarString(name, length, LengthUnit.Utf16Units, name == "nvarchar");
            case "text":
            case "ntext":
                return new ColumnType(ValueKind.String, name, IsUnicode: name == "ntext");
            case "binary":
                return new ColumnType(ValueKind.Binary, name, Limit(length), LengthUnit.Bytes, IsFixedLength: true);
            case "varbinary":
                return VarBinary(name, length);
            case "image":
                return new ColumnType(ValueKind.Binary, name);
            case "datetime":
                return new ColumnType(ValueKind.DateTime, name, FractionDigits: 3, IsLegacyDateTime: true);
            case "datetime2":
                return new ColumnType(ValueKind.DateTime, name, FractionDigits: dtPrecision ?? 7);
            case "uniqueidentifier":
                return new ColumnType(ValueKind.Guid, name);
            case "timestamp":
            case "rowversion":
                // 行バージョン。値は入れられないので、列を読む側で IsReadOnly にする
                return new ColumnType(ValueKind.Binary, "timestamp", 8, LengthUnit.Bytes, IsFixedLength: true);
            default:
                return Unsupported(name);
        }
    }

    private static ColumnType DescribePostgreSql(
        string name, long? length, int? precision, int? scale, int? dtPrecision)
    {
        switch (name)
        {
            case "boolean":
                return new ColumnType(ValueKind.Boolean, name);
            case "smallint":
                return Integer(name, short.MinValue, short.MaxValue);
            case "integer":
                return Integer(name, int.MinValue, int.MaxValue);
            case "bigint":
                return Integer(name, long.MinValue, long.MaxValue);
            case "numeric":
                return Decimal(name, precision, scale);
            case "real":
                return new ColumnType(ValueKind.Float, name, IsSingle: true);
            case "double precision":
                return new ColumnType(ValueKind.Float, name);
            case "character":
                return new ColumnType(ValueKind.String, name, Limit(length), LengthUnit.CodePoints, IsFixedLength: true);
            case "character varying":
                return VarString(name, length, LengthUnit.CodePoints, true);
            case "text":
                return new ColumnType(ValueKind.String, name);
            case "bytea":
                return new ColumnType(ValueKind.Binary, name);
            case "timestamp without time zone":
                return new ColumnType(ValueKind.DateTime, name, FractionDigits: dtPrecision ?? 6);
            case "uuid":
                return new ColumnType(ValueKind.Guid, name);
            default:
                return Unsupported(name);
        }
    }

    private static ColumnType DescribeMySql(
        string name, string full, long? length, int? precision, int? scale, int? dtPrecision)
    {
        var unsigned = full.Contains("unsigned", StringComparison.Ordinal);
        switch (name)
        {
            case "tinyint":
                if (full.StartsWith("tinyint(1)", StringComparison.Ordinal))
                {
                    return new ColumnType(ValueKind.Boolean, "tinyint(1)");
                }

                return unsigned ? Integer(name, 0, byte.MaxValue) : Integer(name, sbyte.MinValue, sbyte.MaxValue);
            case "smallint":
                return unsigned ? Integer(name, 0, ushort.MaxValue) : Integer(name, short.MinValue, short.MaxValue);
            case "mediumint":
                return unsigned ? Integer(name, 0, 16_777_215) : Integer(name, -8_388_608, 8_388_607);
            case "int":
                return unsigned ? Integer(name, 0, uint.MaxValue) : Integer(name, int.MinValue, int.MaxValue);
            case "bigint":
                // bigint unsigned は long に収まらないので扱わない
                return unsigned ? Unsupported("bigint unsigned") : Integer(name, long.MinValue, long.MaxValue);
            case "decimal":
                return Decimal(name, precision, scale);
            case "float":
                return new ColumnType(ValueKind.Float, name, IsSingle: true);
            case "double":
                return new ColumnType(ValueKind.Float, name);
            case "char":
                return new ColumnType(ValueKind.String, name, Limit(length), LengthUnit.CodePoints, IsFixedLength: true);
            case "varchar":
                return VarString(name, length, LengthUnit.CodePoints, true);
            case "tinytext":
            case "text":
            case "mediumtext":
            case "longtext":
                return VarString(name, length, LengthUnit.Utf8Bytes, true);
            case "binary":
                return new ColumnType(ValueKind.Binary, name, Limit(length), LengthUnit.Bytes, IsFixedLength: true);
            case "varbinary":
            case "tinyblob":
            case "blob":
            case "mediumblob":
            case "longblob":
                return VarBinary(name, length);
            case "datetime":
                return new ColumnType(ValueKind.DateTime, name, FractionDigits: dtPrecision ?? 0);
            default:
                return Unsupported(name);
        }
    }
}
