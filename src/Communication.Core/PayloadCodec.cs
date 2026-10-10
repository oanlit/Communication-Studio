using System.Text;

namespace Communication.Core;

public static class PayloadCodec
{
    public static string ToHex(ReadOnlySpan<byte> data) => BitConverter.ToString(data.ToArray()).Replace('-', ' ');
    public static byte[] Parse(string text, bool hex, string suffix = "")
    {
        if (!hex)
            return Encoding.UTF8.GetBytes(text + suffix);
        var compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        if ((compact.Length & 1) != 0)
            throw new FormatException("HEX 必须成对输入，例如 01 03 00 00。");
        return Convert.FromHexString(compact);
    }

    public static string ToText(ReadOnlySpan<byte> data) => Encoding.UTF8.GetString(data)
        .Replace("\0", "␀").Replace("\r", "␍").Replace("\n", "␊").Replace("\t", "⇥");
}
