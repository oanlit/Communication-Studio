using System.Buffers.Binary;

namespace Communication.Core;

public static class ModbusRtuCodec
{
    public static byte[] Encode(byte unit, ReadOnlySpan<byte> pdu)
    {
        if (unit > 247 || pdu.Length is < 1 or > 253)
            throw new ArgumentException("RTU 地址或 PDU 长度无效。");
        var frame = new byte[pdu.Length + 3];
        frame[0] = unit;
        pdu.CopyTo(frame.AsSpan(1));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frame.Length - 2), Checksums.ModbusCrc16(frame.AsSpan(0, frame.Length - 2)));
        return frame;
    }
    public static bool HasValidCrc(ReadOnlySpan<byte> frame) => frame.Length is >= 4 and <= 256 &&
        BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]) == Checksums.ModbusCrc16(frame[..^2]);
    public static byte[] Decode(ReadOnlySpan<byte> frame)
    {
        if (!HasValidCrc(frame))
            throw new IOException("Modbus RTU CRC 校验失败或帧长度无效。");
        return frame[1..^2].ToArray();
    }
}
