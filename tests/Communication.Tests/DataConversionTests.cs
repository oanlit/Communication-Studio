using Communication.Core;

internal static class DataConversionTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
    private static void Reject(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            return;
        }

        throw new Exception("Invalid conversion was accepted.");
    }
    public static Task Run()
    {
        var bytes = new byte[] { 0, 1, 127, 128, 255 };
        foreach (var format in new[] { DataFormat.Hex, DataFormat.Bin, DataFormat.Dec, DataFormat.Oct, DataFormat.Base64, DataFormat.UInt8, DataFormat.Int8 })
            Check(DataConversion.Decode(DataConversion.Encode(bytes, format), format).SequenceEqual(bytes), $"Byte representation {format}");
        Check(DataConversion.Decode("0x01FE, ff", DataFormat.Hex).SequenceEqual(new byte[] { 1, 254, 255 }), "HEX prefixes, continuous bytes and separators");
        Check(DataConversion.Encode([0x12, 0x34], DataFormat.UInt16, true) == "4660", "Big endian UInt16");
        Check(DataConversion.Encode([0x12, 0x34], DataFormat.UInt16) == "13330", "Little endian UInt16");
        Check(DataConversion.Encode([0x3F, 0x80, 0, 0], DataFormat.Single, true) == "1", "IEEE754 reinterpretation");
        var vectors = new (DataFormat Format, string Input)[]
        {
            (DataFormat.Int16, "-32768 32767"), (DataFormat.UInt16, "0 65535"),
            (DataFormat.Int32, "-2147483648 2147483647"), (DataFormat.UInt32, "0 4294967295"),
            (DataFormat.Int64, "-9223372036854775808 9223372036854775807"), (DataFormat.UInt64, "0 18446744073709551615"),
            (DataFormat.Half, "-1.5 65504"), (DataFormat.Single, "-1.5 3.4028235E+38"), (DataFormat.Double, "-1.5 1.7976931348623157E+308"),
            (DataFormat.Decimal, "-1.5 79228162514264337593543950335"), (DataFormat.Boolean, "true false"), (DataFormat.Char, "A 中")
        };
        foreach (var (format, input) in vectors)
            foreach (var big in new[] { true, false })
            {
                var raw = DataConversion.Decode(input, format, big);
                Check(DataConversion.Decode(DataConversion.Encode(raw, format, big), format, big).SequenceEqual(raw), $"Scalar boundary {format}, endian={big}");
            }
        foreach (var format in new[] { DataFormat.Utf8, DataFormat.Utf16Le, DataFormat.Utf16Be, DataFormat.Utf32, DataFormat.Gbk, DataFormat.Gb18030 })
            Check(DataConversion.Encode(DataConversion.Decode("设备\r\n A ", format), format) == "设备\r\n A ", $"Text whitespace and Chinese: {format}");
        Check(DataConversion.Decode("A", DataFormat.Utf8).SequenceEqual(new byte[] { 65 }), "Text without BOM");
        Check(DataConversion.Encode([0xE9], DataFormat.Latin1) == "é", "Latin1 mapping");
        Check(DataConversion.Encode([65], DataFormat.Ascii) == "A", "ASCII");
        Reject(() => DataConversion.Decode("1", DataFormat.Hex));
        Reject(() => DataConversion.Decode("999", DataFormat.Oct));
        Reject(() => DataConversion.Decode("256", DataFormat.Dec));
        Reject(() => DataConversion.Decode("65536", DataFormat.UInt16));
        Reject(() => DataConversion.Decode("1E100", DataFormat.Single));
        Reject(() => DataConversion.Decode("%%%", DataFormat.Base64));
        Reject(() => DataConversion.Decode("中", DataFormat.Ascii));
        Reject(() => DataConversion.Encode([255], DataFormat.Utf8));
        Reject(() => DataConversion.Encode([1], DataFormat.UInt16));
        Reject(() => DataConversion.Encode([2], DataFormat.Boolean));
        Reject(() => DataConversion.Encode(Enumerable.Repeat((byte)255, 16).ToArray(), DataFormat.Decimal));
        Reject(() => DataConversion.Decode(new string('0', 131073), DataFormat.Hex));
        return Task.CompletedTask;
    }
}
