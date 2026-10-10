using System.Buffers.Binary;
using System.Net.Sockets;
using Communication.Core;

namespace Communication.Transports;

internal static class ModbusWire
{
    public static async Task<byte[]> ReadPacketAsync(NetworkStream stream, ModbusRole role, string endpoint, Action<ModbusTrafficRecord> publish, CancellationToken token, Action<TrafficRecord>? rawTraffic = null)
    {
        var buffer = new byte[261];
        var received = 0;
        var target = 7;
        var complete = false;
        try
        {
            while (received < target)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(received, target - received), token).ConfigureAwait(false);
                if (count == 0)
                    throw new EndOfStreamException("Modbus 对端已关闭连接。");
                rawTraffic?.Invoke(new(DateTimeOffset.Now, TrafficDirection.Rx, PayloadKind.StreamChunk, buffer.AsSpan(received, count).ToArray(), endpoint, "MODBUS TCP"));
                received += count;
                if (received == 7 && target == 7)
                {
                    var length = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4));
                    if (length is < 2 or > 254)
                        throw new IOException("无效的 Modbus MBAP 长度。");
                    target = 6 + length;
                }
            }
            complete = true;
            return buffer.AsSpan(0, received).ToArray();
        }
        finally
        {
            if (received > 0)
                publish(new(role, TrafficDirection.Rx, endpoint, buffer.AsSpan(0, received), complete));
        }
    }
}
