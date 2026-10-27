using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VehicleVision.PleasanterTools.DatabaseConverter.Core.Schema;

namespace VehicleVision.PleasanterTools.DatabaseConverter.Core.Values;

/// <summary>
/// 表の内容の要約（件数と、行順に依存しないチェックサム）。
/// 変換先へ書いた値（変換後）と、変換先から読み戻した値を、同じ規則で要約して突き合わせる。
/// </summary>
/// <remarks>
/// 値は変換先の列の型で正規化してから SHA-256 の先頭 8 バイトにする（件数×列数の加算なので行順に依存しない）。
/// 列ごとの合計（どの列が違うかを示す）、行ごとの合計（行の中の組み合わせの違い・行をまたぐ値の入れ替えを見つける）、
/// 主キーの合計（キーの集合）を持つ。
/// </remarks>
public sealed class TableDigest
{
    private readonly ColumnType[] _types;
    private readonly string[] _names;
    private readonly int[] _primaryKeyIndexes;
    private readonly ulong[] _columnSums;

    /// <param name="columnNames">列名（変換先の名前。要約する順）。</param>
    /// <param name="types">列の型（変換先）。</param>
    /// <param name="primaryKeyIndexes">主キーの列の位置（<paramref name="columnNames"/> の中の添字）。無ければ空。</param>
    public TableDigest(IReadOnlyList<string> columnNames, IReadOnlyList<ColumnType> types, IReadOnlyList<int> primaryKeyIndexes)
    {
        _names = columnNames.ToArray();
        _types = types.ToArray();
        _primaryKeyIndexes = primaryKeyIndexes.ToArray();
        _columnSums = new ulong[_names.Length];
    }

    /// <summary>行数。</summary>
    public long Count { get; private set; }

    /// <summary>主キーの合計。主キーが無いときは 0。</summary>
    public ulong PrimaryKeySum { get; private set; }

    /// <summary>行ごとの合計。</summary>
    public ulong RowSum { get; private set; }

    /// <summary>行を足す。<paramref name="values"/> は列の順。</summary>
    public void Add(IReadOnlyList<object?> values)
    {
        Count++;
        var rowBytes = new byte[8 * values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var h = HashValue(values[i], _types[i]);
            _columnSums[i] = unchecked(_columnSums[i] + h);
            BitConverter.TryWriteBytes(rowBytes.AsSpan(i * 8, 8), h);
        }

        RowSum = unchecked(RowSum + Hash64(rowBytes));
        if (_primaryKeyIndexes.Length > 0)
        {
            var keyBytes = new byte[8 * _primaryKeyIndexes.Length];
            for (var k = 0; k < _primaryKeyIndexes.Length; k++)
            {
                var index = _primaryKeyIndexes[k];
                BitConverter.TryWriteBytes(keyBytes.AsSpan(k * 8, 8), HashValue(values[index], _types[index]));
            }

            PrimaryKeySum = unchecked(PrimaryKeySum + Hash64(keyBytes));
        }
    }

    /// <summary>別の要約と比べ、違う点を返す（英語）。同じなら空。</summary>
    public IReadOnlyList<string> Compare(TableDigest actual)
    {
        var differences = new List<string>();
        if (Count != actual.Count)
        {
            differences.Add($"row count differs (expected {Count}, actual {actual.Count})");
        }

        if (_primaryKeyIndexes.Length > 0 && PrimaryKeySum != actual.PrimaryKeySum)
        {
            differences.Add("primary key set differs");
        }

        for (var i = 0; i < _columnSums.Length; i++)
        {
            if (_columnSums[i] != actual._columnSums[i])
            {
                differences.Add($"checksum of column '{_names[i]}' differs");
            }
        }

        if (differences.Count == 0 && RowSum != actual.RowSum)
        {
            differences.Add("row contents differ (values are combined differently across rows)");
        }

        return differences;
    }

    /// <summary>
    /// 値を正規化した文字列にする（テスト・診断用）。NULL は <see langword="null"/>。
    /// 変換元と変換先の同じ値が、同じ文字列になるようにする。
    /// </summary>
    public static string? Canonicalize(object? value, ColumnType type)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        switch (type.Kind)
        {
            case ValueKind.Boolean:
                return value is bool b
                    ? (b ? "1" : "0")
                    : (System.Convert.ToDecimal(value, CultureInfo.InvariantCulture) != 0m ? "1" : "0");
            case ValueKind.Integer:
                return value is bool bi
                    ? (bi ? "1" : "0")
                    : System.Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            case ValueKind.Decimal:
                return ValueConverter.Normalize(System.Convert.ToDecimal(value, CultureInfo.InvariantCulture))
                    .ToString(CultureInfo.InvariantCulture);
            case ValueKind.Float:
            {
                var f = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (type.IsSingle)
                {
                    f = (float)f;
                }

                return f == 0d ? "0" : f.ToString("R", CultureInfo.InvariantCulture);
            }

            case ValueKind.String:
            {
                var s = value is Guid g ? g.ToString("D", CultureInfo.InvariantCulture) : (string)value;
                return type.IsFixedLength ? s.TrimEnd(' ') : s;
            }

            case ValueKind.Binary:
                return System.Convert.ToHexString((byte[])value);
            case ValueKind.DateTime:
                return ((DateTime)value).Ticks.ToString(CultureInfo.InvariantCulture);
            case ValueKind.Guid:
                return value is Guid guid
                    ? guid.ToString("D", CultureInfo.InvariantCulture)
                    : Guid.Parse((string)value).ToString("D", CultureInfo.InvariantCulture);
            default:
                return value.ToString();
        }
    }

    private static ulong HashValue(object? value, ColumnType type)
    {
        if (type.Kind == ValueKind.Binary && value is byte[] bytes)
        {
            return Hash64(bytes, prefix: (byte)'X');
        }

        var text = Canonicalize(value, type);
        return text is null
            ? Hash64(Array.Empty<byte>(), prefix: (byte)'N')
            : Hash64(Encoding.UTF8.GetBytes(text), prefix: (byte)'V');
    }

    private static ulong Hash64(byte[] data, byte prefix = (byte)'R')
    {
        Span<byte> hash = stackalloc byte[32];
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData([prefix]);
        sha.AppendData(data);
        sha.GetHashAndReset(hash);
        return BitConverter.ToUInt64(hash[..8]);
    }
}
