using Communication.Core;

internal static class ModbusPacketTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
    private static ModbusPacketDetails Rtu(byte[] pdu, bool request = true) => ModbusPacketDetails.Decode(new(ModbusRole.Master, request ? TrafficDirection.Tx : TrafficDirection.Rx, "COM1", ModbusRtuCodec.Encode(1, pdu), transport: ModbusTransport.Rtu));
    public static Task Decode()
    {
        var write = Rtu([6, 0, 14, 0, 232]);
        Check(write.Fields.Count == 5 && write.Fields[2].Decimal == "14" && write.Fields[3].Decimal == "232", "FC06 address/value semantics");
        Check(write.Binary == "00000001, 00000110, 00000000 00001110, 00000000 11101000, 11101000 01000111", "Raw binary separates protocol fields and preserves CRC wire order");
        Check(write.Fields[^1].Decimal == "18408" && write.Fields[^1].Meaning.Contains("低字节"), "CRC decimal uses little endian");
        Check(write.Fields[^1].Field == "CRC16" && write.Fields[^1].Description == "低字节在前" && write.Fields[2].Field == "写入地址" && write.Fields[2].Description == "零基偏移", "Field labels move parenthetical explanation into tooltips");
        Check(new ModbusSemanticField("数据 (说明)（截断）", "1", "00000001") is { Field: "数据", Description: "说明\n截断" }, "Both parenthesis styles preserve tooltip notes");
        var response = Rtu([3, 4, 0x12, 0x34, 0xAB, 0xCD], false);
        Check(response.Fields.Where(field => field.Meaning.StartsWith("保持寄存器")).Select(field => field.Decimal).SequenceEqual(new[] { "4660", "43981" }), "Read response decodes big-endian registers without inventing addresses");
        var coils = Rtu([15, 0, 10, 0, 9, 2, 0x55, 1]);
        Check(coils.Fields.Any(field => field.Meaning.Contains("高位填充") && field.Decimal == "1"), "FC15 packed bits preserve padding and low-bit-first meaning");
        var multi = Rtu([16, 0, 10, 0, 2, 4, 0, 100, 0x12, 0x34]);
        Check(multi.Fields.Any(field => field.Meaning.StartsWith("保持寄存器 [1]") && field.Decimal == "4660"), "FC16 write payload semantics");
        foreach (var fc in new byte[] { 1, 2, 3, 4, 5, 6, 15, 16 })
        {
            var request = Rtu(fc is 15 or 16 ? new byte[] { fc, 0, 0, 0, 1, (byte)(fc == 15 ? 1 : 2), 0 }.Concat(fc == 16 ? new byte[] { 1 } : Array.Empty<byte>()).ToArray() : new byte[] { fc, 0, 0, 0, 1 });
            Check(request.Summary.Contains("请求") && !request.Fields.Any(field => field.Meaning == "额外数据"), $"FC{fc} request grouping");
        }
        var exception = Rtu([0x83, 2], false);
        Check(exception.Fields.Any(field => field.Meaning.Contains("非法地址") && field.Decimal == "2"), "Modbus exception semantics");
        var tcpBytes = new byte[] { 0x12, 0x34, 0, 0, 0, 6, 1, 3, 0, 10, 0, 2 };
        var tcp = ModbusPacketDetails.Decode(new(ModbusRole.Slave, TrafficDirection.Rx, "localhost", tcpBytes));
        Check(tcp.Fields[0].Decimal == "4660" && tcp.Fields[2].Decimal == "6" && tcp.Fields[5].Decimal == "10" && !tcp.Fields.Any(field => field.Meaning.StartsWith("CRC")), "TCP MBAP and slave RX request semantics");
        var frame = ModbusRtuCodec.Encode(1, [6, 0, 14, 0, 232]);
        frame[^1] ^= 1;
        Check(ModbusPacketDetails.Decode(new(ModbusRole.Master, TrafficDirection.Rx, "COM1", frame, transport: ModbusTransport.Rtu)).Summary.Contains("CRC 校验失败"), "Invalid CRC is explained without hiding raw bytes");
        var random = new Random(123);
        foreach (var transport in Enum.GetValues<ModbusTransport>())
            foreach (var role in Enum.GetValues<ModbusRole>())
                foreach (var direction in new[] { TrafficDirection.Tx, TrafficDirection.Rx })
                    for (var length = 0; length <= 260; length++)
                    {
                        var bytes = new byte[length];
                        random.NextBytes(bytes);
                        var details = ModbusPacketDetails.Decode(new(role, direction, "test", bytes, complete: false, transport));
                        var raw = string.Concat(bytes.Select(value => Convert.ToString(value, 2).PadLeft(8, '0')));
                        Check(details.Binary.Replace(",", "").Replace(" ", "") == raw, "Truncated/unknown frames retain every original bit exactly once");
                    }
        return Task.CompletedTask;
    }
}
