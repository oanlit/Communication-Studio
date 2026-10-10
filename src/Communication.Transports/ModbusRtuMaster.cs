using Communication.Core;

namespace Communication.Transports;

public sealed class ModbusRtuMaster(Func<ModbusSerialOptions, IModbusRtuPort>? portFactory = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IModbusRtuPort? _port;
    private ModbusSerialOptions? _options;
    private CancellationTokenSource? _lifetime;
    private Task _idleReceiveTask = Task.CompletedTask;
    public bool IsConnected => _port is not null;
    public event Action<ModbusTrafficRecord>? Traffic;
    public event Action<TrafficRecord>? RawTraffic;
    public event Action<string>? Activity;
    public async Task ConnectAsync(ModbusSerialOptions options, CancellationToken token = default)
    {
        options.Validate();
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (IsConnected)
                throw new InvalidOperationException("RTU 主机串口已经打开。");
            await _idleReceiveTask.ConfigureAwait(false);
            var port = await Task.Run(() => (portFactory ?? (settings => new SerialRtuPort(settings)))(options), token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                port.Dispose();
                token.ThrowIfCancellationRequested();
            }
            _port = port;
            _options = options;
            _lifetime = new();
            var lifetimeToken = _lifetime.Token;
            _idleReceiveTask = Task.Run(() => ReceiveIdle(port, options.PortName, lifetimeToken));
        }
        finally
        {
            _gate.Release();
        }

    }
    public async Task<byte[]> ExchangeAsync(byte unit, byte[] pdu, CancellationToken token = default)
    {
        if (unit is < 1 or > 247)
            throw new ArgumentException("主机从站地址范围为 1–247。");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var port = _port ?? throw new InvalidOperationException("请先打开 RTU 主机串口。");
            var options = _options!;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime!.Token);
            try
            {
                return await Task.Run(() =>
                {
                    linked.Token.ThrowIfCancellationRequested();
                    var stale = new byte[256];
                    for (var attempt = 0; ; attempt++)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        var count = port.Read(stale, 0, stale.Length, 1);
                        if (count == 0)
                            break;
                        PublishUnsolicited(options.PortName, stale.AsSpan(0, count).ToArray());
                        if (attempt >= 31)
                            throw new IOException("RTU 线路持续收到非请求数据，请检查总线后重新打开串口。");
                    }
                    if (linked.Token.WaitHandle.WaitOne(options.FrameGapMs))
                        linked.Token.ThrowIfCancellationRequested();
                    var frame = ModbusRtuCodec.Encode(unit, pdu);
                    port.Write(frame);
                    RawTraffic?.Invoke(new(DateTimeOffset.Now, TrafficDirection.Tx, PayloadKind.StreamChunk, frame, options.PortName, "MODBUS RTU"));
                    Traffic?.Invoke(new(ModbusRole.Master, TrafficDirection.Tx, options.PortName, frame, transport: ModbusTransport.Rtu));
                    var response = ModbusRtuFraming.Read(port, options, ModbusRole.Master, record => Traffic?.Invoke(record), linked.Token, record => RawTraffic?.Invoke(record));
                    var decoded = ModbusRtuCodec.Decode(response);
                    if (response[0] != unit)
                        throw new IOException("Modbus RTU 响应从站地址不匹配。");
                    return decoded;
                }, linked.Token).ConfigureAwait(false);
            }
            catch
            {
                // A late response has no transaction id. Close on cancellation/timeout or
                // invalid framing so it cannot be mistaken for a subsequent transaction.
                ClosePort();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }

    }
    public async Task DisconnectAsync()
    {
        // Exchange may dispose this lifetime concurrently while completing a failed request.
        try
        {
            _lifetime?.Cancel();
        }
        catch (ObjectDisposedException)
        {

        }

        await _gate.WaitAsync().ConfigureAwait(false);
        Task idle;
        try
        {
            idle = _idleReceiveTask;
            await Task.Run(ClosePort).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        await idle.ConfigureAwait(false);
    }
    private void PublishUnsolicited(string endpoint, byte[] bytes)
    {
        RawTraffic?.Invoke(new(DateTimeOffset.Now, TrafficDirection.Rx, PayloadKind.StreamChunk, bytes, endpoint, "MODBUS RTU"));
        Traffic?.Invoke(new(ModbusRole.Master, TrafficDirection.Rx, endpoint, bytes, ModbusRtuCodec.HasValidCrc(bytes), ModbusTransport.Rtu));
    }
    private void ReceiveIdle(IModbusRtuPort port, string endpoint, CancellationToken token)
    {
        var buffer = new byte[256];
        while (!token.IsCancellationRequested)
        {
            // The transaction owns the same gate across write + response read. Idle
            // monitoring never races it or consumes its expected response.
            if (!_gate.Wait(0))
            {
                Thread.Sleep(5);
                continue;
            }
            try
            {
                if (token.IsCancellationRequested || !ReferenceEquals(_port, port))
                    return;
                var count = port.Read(buffer, 0, buffer.Length, 25);
                if (count > 0)
                    PublishUnsolicited(endpoint, buffer.AsSpan(0, count).ToArray());
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    ClosePort();
                    Activity?.Invoke($"RTU 主机接收结束：{ex.Message}");
                }
                return;
            }
            finally
            {
                _gate.Release();
            }

        }
    }
    private void ClosePort()
    {
        var port = _port;
        var lifetime = _lifetime;
        _port = null;
        _options = null;
        _lifetime = null;
        try
        {
            lifetime?.Cancel();
            port?.Dispose();
        }
        finally
        {
            lifetime?.Dispose();
        }

    }
    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}
