using Communication.Core;

namespace Communication.Transports;

public sealed class ModbusRtuServer(ModbusRegisterBank bank, Func<ModbusSerialOptions, IModbusRtuPort>? portFactory = null) : IAsyncDisposable
{
    private readonly ModbusSlaveProtocol _protocol = new(bank);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IModbusRtuPort? _port;
    private CancellationTokenSource? _lifetime;
    private Task _receiveTask = Task.CompletedTask;
    public bool IsRunning => _port is not null;
    public event Action<string>? Activity;
    public event Action<ModbusTrafficRecord>? Traffic;
    public event Action<TrafficRecord>? RawTraffic;
    public async Task StartAsync(ModbusSerialOptions options, byte unit, CancellationToken token = default)
    {
        options.Validate();
        if (unit is < 1 or > 247)
            throw new ArgumentException("从机地址范围为 1–247。");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (IsRunning)
                throw new InvalidOperationException("RTU 从机已经启动。");
            await _receiveTask.ConfigureAwait(false);
            _lifetime?.Dispose();
            var port = await Task.Run(() => (portFactory ?? (settings => new SerialRtuPort(settings)))(options), token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                port.Dispose();
                token.ThrowIfCancellationRequested();
            }
            _port = port;
            _lifetime = new();
            var lifetimeToken = _lifetime.Token;
            _receiveTask = Task.Run(() => Receive(port, options, unit, lifetimeToken));
        }
        finally
        {
            _gate.Release();
        }

    }
    private void Receive(IModbusRtuPort port, ModbusSerialOptions options, byte unit, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                byte[] frame;
                try
                {
                    frame = ModbusRtuFraming.Read(port, options, ModbusRole.Slave, record => Traffic?.Invoke(record), token, record => RawTraffic?.Invoke(record));
                }
                catch (TimeoutException)
                {
                    continue;
                }

                if (!ModbusRtuCodec.HasValidCrc(frame))
                {
                    Activity?.Invoke("RTU CRC 错误 / 不完整帧，已丢弃");
                    continue;
                }
                if (frame[0] != unit && frame[0] != 0)
                    continue;
                var pdu = ModbusRtuCodec.Decode(frame);
                if (frame[0] == 0 && pdu[0] is not (5 or 6 or 15 or 16))
                    continue;
                var response = _protocol.Process(pdu);
                if (frame[0] == 0)
                {
                    Activity?.Invoke($"RTU 广播写入 · FC {pdu[0]:D2} · {(response[0] >= 0x80 ? "请求无效" : "完成")} · 不发送响应");
                    continue;
                }
                if (token.WaitHandle.WaitOne(options.FrameGapMs))
                    token.ThrowIfCancellationRequested();
                var packet = ModbusRtuCodec.Encode(unit, response);
                port.Write(packet);
                RawTraffic?.Invoke(new(DateTimeOffset.Now, TrafficDirection.Tx, PayloadKind.StreamChunk, packet, options.PortName, "MODBUS RTU"));
                Traffic?.Invoke(new(ModbusRole.Slave, TrafficDirection.Tx, options.PortName, packet, transport: ModbusTransport.Rtu));
                Activity?.Invoke($"{options.PortName} · Unit {unit} · FC {pdu[0]:D2} · {(response[0] >= 0x80 ? "异常响应" : "完成")}");
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                Activity?.Invoke($"RTU 从机结束：{ex.Message}");
        }

        finally
        {
            try
            {
                port.Dispose();
            }
            finally
            {
                _port = null;
            }

        }

    }
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _lifetime?.Cancel();
            await _receiveTask.ConfigureAwait(false);
            _port = null;
            _lifetime?.Dispose();
            _lifetime = null;
        }
        finally
        {
            _gate.Release();
        }

    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
