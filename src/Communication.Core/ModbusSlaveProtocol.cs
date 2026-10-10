using System.Buffers.Binary;

namespace Communication.Core;

public sealed class ModbusSlaveProtocol(ModbusRegisterBank bank)
{
    public byte[] Process(byte[] pdu)
    {
        var fc = pdu[0];
        if (fc is not (1 or 2 or 3 or 4 or 5 or 6 or 15 or 16))
            return ExceptionPdu(fc, 1);
        if (pdu.Length < 5)
            return ExceptionPdu(fc, 3);
        var start = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
        var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
        if (fc <= 4)
        {
            var bits = fc <= 2;
            if (pdu.Length != 5 || count < 1 || count > (bits ? 2000 : 125))
                return ExceptionPdu(fc, 3);
            if (start + count > 65536)
                return ExceptionPdu(fc, 2);
            var values = bank.Read((ModbusTable)fc, start, count);
            var size = bits ? (count + 7) / 8 : count * 2;
            var result = new byte[size + 2];
            result[0] = fc;
            result[1] = (byte)size;
            for (var i = 0; i < count; i++)
            {
                if (bits)
                    result[2 + i / 8] |= (byte)(values[i] << (i % 8));
                else
                    BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2 + i * 2), values[i]);
            }
            return result;
        }
        if (fc is 5 or 6)
        {
            if (pdu.Length != 5 || (fc == 5 && count is not (0 or 0xFF00)))
                return ExceptionPdu(fc, 3);
            bank.Set(fc == 5 ? ModbusTable.Coils : ModbusTable.HoldingRegisters, start, [(ushort)(fc == 5 ? (count == 0 ? 0 : 1) : count)]);
            return pdu;
        }
        var coils = fc == 15;
        if (count < 1 || count > (coils ? 1968 : 123) || pdu.Length < 6)
            return ExceptionPdu(fc, 3);
        if (start + count > 65536)
            return ExceptionPdu(fc, 2);
        var byteCount = coils ? (count + 7) / 8 : count * 2;
        if (pdu[5] != byteCount || pdu.Length != byteCount + 6)
            return ExceptionPdu(fc, 3);
        var written = new ushort[count];
        for (var i = 0; i < count; i++)
            written[i] = coils ? (ushort)((pdu[6 + i / 8] >> (i % 8)) & 1) : BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(6 + i * 2));
        bank.Set(coils ? ModbusTable.Coils : ModbusTable.HoldingRegisters, start, written);
        return pdu.AsSpan(0, 5).ToArray();
    }
    private static byte[] ExceptionPdu(byte function, byte code) => [(byte)(function | 0x80), code];
}
