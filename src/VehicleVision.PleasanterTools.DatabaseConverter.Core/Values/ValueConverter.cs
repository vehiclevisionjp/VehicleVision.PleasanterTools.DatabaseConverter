using System.Data.SqlTypes;
using System.Globalization;
using System.Text;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Schema;

namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.Values;

/// <summary>値を変換できなかった。メッセージは英語（コンソールへ出る）。</summary>
public sealed class ValueConversionException : Exception
{
    public ValueConversionException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 値を、変換先の列の型へ変換する。型の変換表（_documents/アーキテクチャ方針.md）の実装。
/// 変換先の範囲・長さに収まらない値は例外にする（黙って丸めない）。
/// 変換先の精度へ丸めたもの（日時の小数秒、単精度への変換）は <c>rounded</c> で知らせる。
/// 出力する .NET の型は、種類ごとに固定: Boolean=bool / Integer=long / Decimal=decimal / Float=double /
/// String=string / Binary=byte[] / DateTime=DateTime（Kind は Unspecified）/ Guid=Guid。
/// </summary>
public sealed class ValueConverter
{
    /// <summary>MySQL の datetime が保証する下限。</summary>
    private static readonly DateTime MySqlMinDateTime = new(1000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    private readonly Dbms _target;

    public ValueConverter(Dbms target)
    {
        _target = target;
    }

    /// <summary>
    /// 変換元の型から変換先の型への変換が、そもそも可能かを調べる。
    /// </summary>
    /// <returns>可能なら <see langword="null"/>。不可なら理由（英語）。</returns>
    public static string? CheckSupported(ColumnType source, ColumnType target)
    {
        if (source.Kind == ValueKind.Unsupported)
        {
            return $"source type '{source.NativeName}' is not supported";
        }

        if (target.Kind == ValueKind.Unsupported)
        {
            return $"target type '{target.NativeName}' is not supported";
        }

        var ok = (source.Kind, target.Kind) switch
        {
            (ValueKind.Boolean, ValueKind.Boolean or ValueKind.Integer) => true,
            (ValueKind.Integer, ValueKind.Boolean or ValueKind.Integer or ValueKind.Decimal or ValueKind.Float) => true,
            (ValueKind.Decimal, ValueKind.Integer or ValueKind.Decimal or ValueKind.Float) => true,
            (ValueKind.Float, ValueKind.Integer or ValueKind.Decimal or ValueKind.Float) => true,
            (ValueKind.String, ValueKind.String or ValueKind.Guid) => true,
            (ValueKind.Guid, ValueKind.Guid or ValueKind.String) => true,
            (ValueKind.Binary, ValueKind.Binary) => true,
            (ValueKind.DateTime, ValueKind.DateTime) => true,
            _ => false,
        };
        if (!ok)
        {
            return $"conversion from '{source.Describe()}' to '{target.Describe()}' is not supported";
        }

        if (source.Kind == ValueKind.Guid && target.Kind == ValueKind.String
            && target.MaxLength is { } max && max < 36)
        {
            return $"a GUID needs 36 characters but the target column '{target.Describe()}' is shorter";
        }

        return null;
    }

    /// <summary>
    /// 変換で値が欠ける・丸まる可能性があれば、警告（英語）を返す。無ければ <see langword="null"/>。
    /// 実際に欠けるかは行ごとに調べ、収まらない値は失敗として数える。
    /// </summary>
    public static string? DescribeRisk(ColumnType source, ColumnType target)
    {
        if (target.Kind == ValueKind.String && !target.IsUnicode)
        {
            return $"target column '{target.Describe()}' is not a Unicode type; characters outside its code page may be replaced";
        }

        if (source.Kind == ValueKind.String && target.Kind == ValueKind.String
            && target.MaxLength is { } tMax && (source.MaxLength is not { } sMax || sMax > tMax))
        {
            return $"target '{target.Describe()}' is shorter than source '{source.Describe()}'; longer values will be reported as failures";
        }

        if (source.Kind == ValueKind.Binary && target.Kind == ValueKind.Binary
            && target.MaxLength is { } tbMax && (source.MaxLength is not { } sbMax || sbMax > tbMax))
        {
            return $"target '{target.Describe()}' is smaller than source '{source.Describe()}'; larger values will be reported as failures";
        }

        if (source.Kind == ValueKind.DateTime && target.Kind == ValueKind.DateTime
            && (target.IsLegacyDateTime || target.FractionDigits < source.FractionDigits))
        {
            return $"target '{target.Describe()}' has lower date/time precision or range than source '{source.Describe()}'; values are rounded and out-of-range values are reported as failures";
        }

        if (source.Kind == ValueKind.Float && target.Kind == ValueKind.Float && target.IsSingle && !source.IsSingle)
        {
            return $"target '{target.Describe()}' is single precision; values are rounded";
        }

        if (target.Kind == ValueKind.Decimal && source.Kind is ValueKind.Decimal or ValueKind.Float
            && target.Precision > 0 && (source.Precision == 0 || source.Precision - source.Scale > target.Precision - target.Scale || source.Scale > target.Scale))
        {
            return $"target '{target.Describe()}' is narrower than source '{source.Describe()}'; values that do not fit will be reported as failures";
        }

        if (target.Kind == ValueKind.Integer && source.Kind == ValueKind.Integer
            && (target.IntegerMin > source.IntegerMin || target.IntegerMax < source.IntegerMax))
        {
            return $"target '{target.Describe()}' has a narrower range than source '{source.Describe()}'; values out of range will be reported as failures";
        }

        return null;
    }

    /// <summary>
    /// 値を変換する。NULL の扱いは呼び出し側（NULL は変換先が許すかだけを見る）。
    /// </summary>
    /// <param name="value">変換元のドライバが返した値（<see cref="DBNull"/> 以外）。</param>
    /// <param name="source">変換元の列の型。</param>
    /// <param name="target">変換先の列の型。</param>
    /// <param name="rounded">変換先の精度へ丸めたら true。</param>
    /// <exception cref="ValueConversionException">変換先へ入らない値。</exception>
    public object Convert(object value, ColumnType source, ColumnType target, out bool rounded)
    {
        rounded = false;
        return target.Kind switch
        {
            ValueKind.Boolean => ToBoolean(value, target),
            ValueKind.Integer => ToInteger(value, target),
            ValueKind.Decimal => ToDecimal(value, target),
            ValueKind.Float => ToFloat(value, target, out rounded),
            ValueKind.String => ToString(value, target),
            ValueKind.Binary => ToBinary(value, target),
            ValueKind.DateTime => ToDateTime(value, target, out rounded),
            ValueKind.Guid => ToGuid(value),
            _ => throw new ValueConversionException($"target type '{target.NativeName}' is not supported"),
        };
    }

    private static bool IsIntegral(object v)
        => v is sbyte or byte or short or ushort or int or uint or long or ulong;

    private static object ToBoolean(object value, ColumnType target)
    {
        if (value is bool b)
        {
            return b;
        }

        if (IsIntegral(value))
        {
            var n = System.Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            if (n == 0m)
            {
                return false;
            }

            if (n == 1m)
            {
                return true;
            }

            throw new ValueConversionException($"integer value {n} cannot be converted to boolean (only 0 and 1)");
        }

        throw Incompatible(value, target);
    }

    private static object ToInteger(object value, ColumnType target)
    {
        decimal n;
        if (value is bool b)
        {
            n = b ? 1m : 0m;
        }
        else if (IsIntegral(value))
        {
            n = System.Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        else if (value is decimal d)
        {
            n = d;
        }
        else if (value is double or float)
        {
            var f = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(f) || double.IsInfinity(f) || Math.Abs(f) > 9.3e18)
            {
                throw new ValueConversionException($"value {f.ToString("R", CultureInfo.InvariantCulture)} is out of range for {target.Describe()}");
            }

            n = (decimal)f;
        }
        else
        {
            throw Incompatible(value, target);
        }

        if (n != decimal.Truncate(n))
        {
            throw new ValueConversionException($"value {n.ToString(CultureInfo.InvariantCulture)} has a fractional part and cannot be stored in {target.Describe()}");
        }

        if (n < target.IntegerMin || n > target.IntegerMax)
        {
            throw new ValueConversionException(
                $"integer value {n.ToString(CultureInfo.InvariantCulture)} is out of range for {target.Describe()} ({target.IntegerMin}..{target.IntegerMax})");
        }

        return (long)n;
    }

    private static object ToDecimal(object value, ColumnType target)
    {
        decimal d;
        try
        {
            if (value is bool b)
            {
                d = b ? 1m : 0m;
            }
            else if (value is decimal dec)
            {
                d = dec;
            }
            else if (IsIntegral(value))
            {
                d = System.Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            }
            else if (value is double or float)
            {
                var f = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(f) || double.IsInfinity(f))
                {
                    throw new ValueConversionException($"value {f.ToString("R", CultureInfo.InvariantCulture)} cannot be converted to a decimal");
                }

                // double の 17 桁表記を経由する（(decimal)double は 15 桁へ丸めるため、桁あふれの判定がぶれる）
                d = decimal.Parse(f.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            else
            {
                throw Incompatible(value, target);
            }
        }
        catch (OverflowException)
        {
            throw new ValueConversionException($"value is out of range for {target.Describe()}");
        }

        d = Normalize(d);
        if (target.Precision > 0)
        {
            var scale = ScaleOf(d);
            if (scale > target.Scale)
            {
                throw new ValueConversionException(
                    $"decimal value {d.ToString(CultureInfo.InvariantCulture)} has {scale} fractional digits but {target.Describe()} allows {target.Scale}");
            }

            var integerDigits = IntegerDigitsOf(d);
            if (integerDigits > target.Precision - target.Scale)
            {
                throw new ValueConversionException(
                    $"decimal value {d.ToString(CultureInfo.InvariantCulture)} has {integerDigits} integer digits but {target.Describe()} allows {target.Precision - target.Scale}");
            }
        }

        return d;
    }

    private object ToFloat(object value, ColumnType target, out bool rounded)
    {
        rounded = false;
        double f;
        if (value is bool b)
        {
            f = b ? 1d : 0d;
        }
        else if (value is float or double or decimal || IsIntegral(value))
        {
            f = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        else
        {
            throw Incompatible(value, target);
        }

        if ((double.IsNaN(f) || double.IsInfinity(f)) && _target != Dbms.PostgreSql)
        {
            throw new ValueConversionException($"value {f.ToString("R", CultureInfo.InvariantCulture)} (NaN or infinity) cannot be stored in {_target.ToDisplayName()}");
        }

        if (target.IsSingle)
        {
            var narrowed = (float)f;
            if (float.IsInfinity(narrowed) && !double.IsInfinity(f))
            {
                throw new ValueConversionException($"value {f.ToString("R", CultureInfo.InvariantCulture)} is out of range for {target.Describe()}");
            }

            rounded = (double)narrowed != f && !double.IsNaN(f);
            f = narrowed;
        }

        return f == 0d ? 0d : f;
    }

    private object ToString(object value, ColumnType target)
    {
        string s;
        if (value is string str)
        {
            s = str;
        }
        else if (value is Guid g)
        {
            s = g.ToString("D", CultureInfo.InvariantCulture);
        }
        else
        {
            throw Incompatible(value, target);
        }

        if (_target == Dbms.PostgreSql && s.Contains('\0', StringComparison.Ordinal))
        {
            throw new ValueConversionException("string contains a NUL character (U+0000), which PostgreSQL cannot store");
        }

        if (_target != Dbms.SqlServer && HasLoneSurrogate(s))
        {
            throw new ValueConversionException($"string contains an unpaired surrogate (invalid UTF-16), which cannot be stored in {_target.ToDisplayName()}");
        }

        if (target.MaxLength is { } max)
        {
            var length = target.Unit switch
            {
                LengthUnit.Utf16Units => (long)s.Length,
                LengthUnit.CodePoints => CountCodePoints(s),
                LengthUnit.Utf8Bytes => Encoding.UTF8.GetByteCount(s),
                _ => 0L,
            };
            if (length > max)
            {
                var unit = target.Unit switch
                {
                    LengthUnit.Utf16Units => "UTF-16 code units",
                    LengthUnit.CodePoints => "characters",
                    LengthUnit.Utf8Bytes => "UTF-8 bytes",
                    _ => "units",
                };
                throw new ValueConversionException($"string length {length} {unit} exceeds {target.Describe()}");
            }
        }

        return s;
    }

    private static object ToBinary(object value, ColumnType target)
    {
        if (value is not byte[] bytes)
        {
            throw Incompatible(value, target);
        }

        if (target.MaxLength is { } max && bytes.Length > max)
        {
            throw new ValueConversionException($"binary length {bytes.Length} bytes exceeds {target.Describe()}");
        }

        return bytes;
    }

    private DateTime ToDateTime(object value, ColumnType target, out bool rounded)
    {
        rounded = false;
        if (value is not DateTime dt)
        {
            throw Incompatible(value, target);
        }

        dt = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
        DateTime result;
        if (target.IsLegacyDateTime)
        {
            // SQL Server の datetime: 1753-01-01 〜 9999-12-31 23:59:59.997、1/300 秒刻み。
            // SqlDateTime が範囲検査と丸め（SQL Server と同じ規則）を行う
            try
            {
                result = DateTime.SpecifyKind(new SqlDateTime(dt).Value, DateTimeKind.Unspecified);
            }
            catch (Exception ex) when (ex is SqlTypeException or OverflowException or ArgumentException)
            {
                throw new ValueConversionException(
                    $"date/time {FormatDateTime(dt)} is out of range for {target.Describe()} (1753-01-01 00:00:00 to 9999-12-31 23:59:59.997)");
            }
        }
        else
        {
            if (_target == Dbms.MySql && dt < MySqlMinDateTime)
            {
                throw new ValueConversionException(
                    $"date/time {FormatDateTime(dt)} is out of range for {target.Describe()} (MySQL supports 1000-01-01 or later)");
            }

            result = RoundFraction(dt, target.FractionDigits, target);
        }

        rounded = result != dt;
        return result;
    }

    private static DateTime RoundFraction(DateTime dt, int digits, ColumnType target)
    {
        if (digits >= 7)
        {
            return dt;
        }

        var unit = (long)Math.Pow(10, 7 - digits);
        var remainder = dt.Ticks % unit;
        if (remainder == 0)
        {
            return dt;
        }

        var rounded = dt.Ticks - remainder + (remainder * 2 >= unit ? unit : 0);
        if (rounded > DateTime.MaxValue.Ticks)
        {
            throw new ValueConversionException($"date/time {FormatDateTime(dt)} is out of range for {target.Describe()} after rounding");
        }

        return new DateTime(rounded, DateTimeKind.Unspecified);
    }

    private static object ToGuid(object value)
    {
        if (value is Guid g)
        {
            return g;
        }

        if (value is string s && Guid.TryParse(s, out var parsed))
        {
            return parsed;
        }

        throw new ValueConversionException("value is not a valid GUID");
    }

    private static ValueConversionException Incompatible(object value, ColumnType target)
        => new($"value of type {value.GetType().Name} cannot be converted to {target.Describe()}");

    /// <summary>日時を人が読める形にする（診断用）。</summary>
    internal static string FormatDateTime(DateTime dt)
        => dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    /// <summary>末尾の 0 を落とし、桁数を最小にする。</summary>
    internal static decimal Normalize(decimal d) => d / 1.0000000000000000000000000000m;

    /// <summary>小数部の桁数（<see cref="Normalize"/> 済みの値を想定）。</summary>
    internal static int ScaleOf(decimal d) => (decimal.GetBits(d)[3] >> 16) & 0xFF;

    /// <summary>整数部の桁数（0 は 0 桁）。</summary>
    internal static int IntegerDigitsOf(decimal d)
    {
        var integral = decimal.Truncate(Math.Abs(d));
        return integral == 0m ? 0 : integral.ToString(CultureInfo.InvariantCulture).Length;
    }

    /// <summary>対にならないサロゲートを含むか。</summary>
    public static bool HasLoneSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    i++;
                    continue;
                }

                return true;
            }

            if (char.IsLowSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>コードポイント数（対のサロゲートを 1 と数える）。</summary>
    public static long CountCodePoints(string s)
    {
        long count = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++;
            }

            count++;
        }

        return count;
    }
}
