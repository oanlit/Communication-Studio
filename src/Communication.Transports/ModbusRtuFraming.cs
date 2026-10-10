using System.Diagnostics;
using Communication.Core;

namespace Communication.Transports;

internal static class ModbusRtuFraming
{
    public static byte[] Read(IModbusRtuPort port, ModbusSerialOptions options, ModbusRole role, Action<ModbusTrafficRecord> publish, CancellationToken token, Action<TrafficRecord>? rawTraffic = null)
    {
        var buffer = new byte[256];
        var count = 0;
        var complete = false;
        var started = Stopwatch.StartNew();
        var lastByte = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var expected = ExpectedLength(buffer.AsSpan(0, count), role == ModbusRole.Slave);
                if (expected is > 256)
                    return buffer.AsSpan(0, count).ToArray();
                if (expected == count && count > 0)
                {
                    complete = true;
                    return buffer.AsSpan(0, count).ToArray();
                }
                if (count == 256)
                    return buffer.ToArray();
                if (count > 0 && lastByte.ElapsedMilliseconds >= options.FrameGapMs)
                {
                    complete = expected is null && ModbusRtuCodec.HasValidCrc(buffer.AsSpan(0, count));
                    return buffer.AsSpan(0, count).ToArray();
                }
                if (started.ElapsedMilliseconds >= options.ResponseTimeoutMs)
                    throw new TimeoutException("Modbus RTU 接收超时。");
                var wanted = expected is { } size ? size - count : 1;
                var timeout = Math.Min(25, options.ResponseTimeoutMs - (int)started.ElapsedMilliseconds);
                if (count > 0)
                    timeout = Math.Min(timeout, options.FrameGapMs - (int)lastByte.ElapsedMilliseconds);
                var received = port.Read(buffer, count, Math.Max(1, wanted), Math.Max(1, timeout));
                if (received > 0)
                {
                    rawTraffic?.Invoke(new(DateTimeOffset.Now, TrafficDirection.Rx, PayloadKind.StreamChunk, buffer.AsSpan(count, received).ToArray(), options.PortName, "MODBUS RTU"));
                    count += received;
                    lastByte.Restart();
                }
            }
        }
        finally
        {
            if (count > 0)
                publish(new(role, TrafficDirection.Rx, options.PortName, buffer.AsSpan(0, count), complete, ModbusTransport.Rtu));
        }
    }
    private static int? ExpectedLength(ReadOnlySpan<byte> frame, bool request)
    {
        if (frame.Length < 2)
            return null;
        var function = frame[1];
        if (!request && function >= 0x80)
            return 5;
        if (function is 5 or 6 || request && function is >= 1 and <= 4 || !request && function is 15 or 16)
            return 8;
        if (request && function is 15 or 16)
            return frame.Length < 7 ? null : 9 + frame[6];
        if (!request && function is >= 1 and <= 4)
            return frame.Length < 3 ? null : 5 + frame[2];
        return null;
    }
}
