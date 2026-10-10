using System.Globalization;
using System.Text;

namespace Communication.Core;

public enum DataFormat
{
    Hex, Bin, Dec, Oct, Int8, UInt8, Int16, UInt16, Int32, UInt32, Int64, UInt64,
    Half, Single, Double, Decimal, Boolean, Char, Utf8, Utf16Le, Utf16Be, Utf32,
    Ascii, Latin1, Gbk, Gb18030, Base64
}

/// <summary>Lossless byte representation conversion; scalar formats reinterpret fixed-size units.</summary>
public static class DataConversion
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    static DataConversion() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    public static bool IsScalar(DataFormat format) => format is >= DataFormat.Int8 and <= DataFormat.Char;
    public static bool UsesByteOrder(DataFormat format) => IsScalar(format) && Size(format) > 1;
    public static int Size(DataFormat format) => format switch
    {
        DataFormat.Int8 or DataFormat.UInt8 or DataFormat.Boolean => 1,
        DataFormat.Int16 or DataFormat.UInt16 or DataFormat.Half or DataFormat.Char => 2,
        DataFormat.Int32 or DataFormat.UInt32 or DataFormat.Single => 4,
        DataFormat.Int64 or DataFormat.UInt64 or DataFormat.Double => 8,
        DataFormat.Decimal => 16,
        _ => 1
    };

    public static byte[] Decode(string input, DataFormat format, bool bigEndian = false)
    {
        if (input.Length > 131072)
            throw new ArgumentException("Input exceeds 128 Ki characters.");
        if (format == DataFormat.Base64)
            return Convert.FromBase64String(input);
        if (TextEncoding(format) is { } encoding)
            return encoding.GetBytes(input);
        var tokens = input.Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (format is DataFormat.Hex or DataFormat.Bin or DataFormat.Oct)
        {
            var radix = format == DataFormat.Hex ? 16 : format == DataFormat.Bin ? 2 : 8;
            var width = radix == 16 ? 2 : radix == 2 ? 8 : 3;
            var bytes = new List<byte>();
            foreach (var token in tokens)
            {
                var digits = token;
                var prefix = radix == 16 ? "0x" : radix == 2 ? "0b" : "0o";
                if (digits.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    digits = digits[2..];
                if (digits.Length == 0 || digits.Length % width != 0)
                    throw new FormatException($"{format}: each byte needs {width} digits.");
                for (var i = 0; i < digits.Length; i += width)
                    bytes.Add(Convert.ToByte(digits.Substring(i, width), radix));
            }
            return bytes.ToArray();
        }
        using var stream = new MemoryStream();
        foreach (var token in tokens)
        {
            byte[] bytes = format switch
            {
                DataFormat.Dec or DataFormat.UInt8 => [byte.Parse(token, Invariant)],
                DataFormat.Int8 => [unchecked((byte)sbyte.Parse(token, Invariant))],
                DataFormat.Int16 => BitConverter.GetBytes(short.Parse(token, Invariant)),
                DataFormat.UInt16 => BitConverter.GetBytes(ushort.Parse(token, Invariant)),
                DataFormat.Int32 => BitConverter.GetBytes(int.Parse(token, Invariant)),
                DataFormat.UInt32 => BitConverter.GetBytes(uint.Parse(token, Invariant)),
                DataFormat.Int64 => BitConverter.GetBytes(long.Parse(token, Invariant)),
                DataFormat.UInt64 => BitConverter.GetBytes(ulong.Parse(token, Invariant)),
                DataFormat.Half => BitConverter.GetBytes(ParseHalf(token)),
                DataFormat.Single => BitConverter.GetBytes(ParseSingle(token)),
                DataFormat.Double => BitConverter.GetBytes(ParseDouble(token)),
                DataFormat.Decimal => decimal.GetBits(decimal.Parse(token, NumberStyles.Float, Invariant)).SelectMany(BitConverter.GetBytes).ToArray(),
                DataFormat.Boolean => token.ToLowerInvariant() switch { "1" or "true" => [1], "0" or "false" => [0], _ => throw new FormatException("Boolean accepts true, false, 0 or 1.") },
                DataFormat.Char => token.Length == 1 ? BitConverter.GetBytes(token[0]) : throw new FormatException("Char requires one UTF-16 code unit per token."),
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            };
            // Decimal uses the .NET four-Int32 representation (lo, mid, hi, flags).
            if (UsesByteOrder(format) && bigEndian == BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            stream.Write(bytes);
        }
        return stream.ToArray();
    }

    public static string Encode(byte[] bytes, DataFormat format, bool bigEndian = false)
    {
        if (format == DataFormat.Base64)
            return Convert.ToBase64String(bytes);
        if (TextEncoding(format) is { } encoding)
            return encoding.GetString(bytes);
        if (!IsScalar(format))
            return string.Join(" ", bytes.Select(b => format switch
        {
            DataFormat.Hex => b.ToString("X2", Invariant),
            DataFormat.Bin => Convert.ToString(b, 2).PadLeft(8, '0'),
            DataFormat.Oct => Convert.ToString(b, 8).PadLeft(3, '0'),
            DataFormat.Dec => b.ToString(Invariant),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        }));
        var size = Size(format);
        if (bytes.Length % size != 0)
            throw new FormatException($"{format} requires a byte count divisible by {size}; received {bytes.Length}.");
        var values = new List<string>();
        for (var offset = 0; offset < bytes.Length; offset += size)
        {
            var unit = bytes.AsSpan(offset, size).ToArray();
            if (UsesByteOrder(format) && bigEndian == BitConverter.IsLittleEndian)
                Array.Reverse(unit);
            values.Add(format switch
            {
                DataFormat.Int8 => unchecked((sbyte)unit[0]).ToString(Invariant),
                DataFormat.UInt8 => unit[0].ToString(Invariant),
                DataFormat.Int16 => BitConverter.ToInt16(unit).ToString(Invariant),
                DataFormat.UInt16 => BitConverter.ToUInt16(unit).ToString(Invariant),
                DataFormat.Int32 => BitConverter.ToInt32(unit).ToString(Invariant),
                DataFormat.UInt32 => BitConverter.ToUInt32(unit).ToString(Invariant),
                DataFormat.Int64 => BitConverter.ToInt64(unit).ToString(Invariant),
                DataFormat.UInt64 => BitConverter.ToUInt64(unit).ToString(Invariant),
                DataFormat.Half => BitConverter.ToHalf(unit).ToString("R", Invariant),
                DataFormat.Single => BitConverter.ToSingle(unit).ToString("R", Invariant),
                DataFormat.Double => BitConverter.ToDouble(unit).ToString("R", Invariant),
                DataFormat.Decimal => new decimal(Enumerable.Range(0, 4).Select(i => BitConverter.ToInt32(unit, i * 4)).ToArray()).ToString(Invariant),
                DataFormat.Boolean => unit[0] switch { 0 => "false", 1 => "true", _ => throw new FormatException("Boolean bytes must be 00 or 01.") },
                DataFormat.Char => BitConverter.ToChar(unit).ToString(),
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            });
        }
        return string.Join(" ", values);
    }

    private static Half ParseHalf(string text)
    {
        var v = Half.Parse(text, NumberStyles.Float, Invariant);
        if (Half.IsInfinity(v) && !text.Contains("Infinity", StringComparison.OrdinalIgnoreCase))
            throw new OverflowException("Half overflow.");
        return v;
    }
    private static float ParseSingle(string text)
    {
        var v = float.Parse(text, NumberStyles.Float, Invariant);
        if (float.IsInfinity(v) && !text.Contains("Infinity", StringComparison.OrdinalIgnoreCase))
            throw new OverflowException("Single overflow.");
        return v;
    }
    private static double ParseDouble(string text)
    {
        var v = double.Parse(text, NumberStyles.Float, Invariant);
        if (double.IsInfinity(v) && !text.Contains("Infinity", StringComparison.OrdinalIgnoreCase))
            throw new OverflowException("Double overflow.");
        return v;
    }
    private static Encoding? TextEncoding(DataFormat format) => format switch
    {
        DataFormat.Utf8 => new UTF8Encoding(false, true),
        DataFormat.Utf16Le => new UnicodeEncoding(false, false, true),
        DataFormat.Utf16Be => new UnicodeEncoding(true, false, true),
        DataFormat.Utf32 => new UTF32Encoding(false, false, true),
        DataFormat.Ascii => Encoding.GetEncoding(20127, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        DataFormat.Latin1 => Encoding.GetEncoding(28591, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        DataFormat.Gbk => Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        DataFormat.Gb18030 => Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        _ => null
    };
}
