using Communication.Core;

namespace Communication.App.ViewModels;

public sealed record ModbusLogRow(DateTimeOffset Timestamp, string Level, string Role, string Direction, string Endpoint, string Message, string Hex = "", string Type = "—")
{
    public ModbusPacketDetails? Packet
    {
        get; init;
    }
    public string Time => Timestamp.ToString("HH:mm:ss.fff");
    public string Detail => $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} · {Role} · {Level} · {Direction}\n{Endpoint}\n{Message}" + (Hex.Length == 0 ? "" : $"\n\n{Hex}");
    public static ModbusLogRow FromTraffic(ModbusTrafficRecord record) => new(record.Timestamp, "TRAFFIC",
        record.Role == ModbusRole.Master ? "主机" : "从机", record.Direction == TrafficDirection.Tx ? "TX" : "RX", record.Endpoint,
        (record.Transport == ModbusTransport.Tcp ? $"TCP · TID {record.Transaction?.ToString() ?? "—"}" : $"RTU · CRC {(record.ValidCrc == true ? "正确" : "错误")}") +
        $" · Unit {record.Unit?.ToString() ?? "—"} · FC {(record.Function is { } fc ? $"0x{fc:X2}" : "—")} · {record.Length} bytes" + (record.IsComplete ? "" : " · 不完整 / 无效长度"), record.Hex, record.Transport == ModbusTransport.Tcp ? "TCP 传输" : "RTU 传输")
    {
        Packet = ModbusPacketDetails.Decode(record)
    };
}
