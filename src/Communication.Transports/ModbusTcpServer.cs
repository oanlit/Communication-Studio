using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Communication.Core;

namespace Communication.Transports;

public sealed class ModbusTcpServer(ModbusRegisterBank bank) : IAsyncDisposable
{
    private readonly ModbusSlaveProtocol _protocol = new(bank);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<long, (TcpClient Client, Task Task)> _peers = new();
    private CancellationTokenSource? _lifetime;
    private TcpListener? _listener;
    private Task _acceptTask = Task.CompletedTask;
    private long _nextId;
    private byte _unit;
    public bool IsRunning => _listener is not null;
    public event Action<string>? Activity;
    public event Action<ModbusTrafficRecord>? Traffic;
    public event Action<TrafficRecord>? RawTraffic;
    public async Task StartAsync(string address, int port, byte unit, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (IsRunning)
                throw new InvalidOperationException("从机已经启动。");
            if (port is < 1 or > 65535 || unit is < 1 or > 247)
                throw new ArgumentException("端口范围 1–65535，Unit ID 范围 1–247。");
            _unit = unit;
            var listener = new TcpListener(IPAddress.Parse(address), port);
            listener.Start(32);
            _listener = listener;
            _lifetime = new CancellationTokenSource();
            _acceptTask = Task.Run(() => AcceptAsync(listener, _lifetime.Token));
        }
        finally
        {
            _gate.Release();
        }

    }
    private async Task AcceptAsync(TcpListener listener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                if (_peers.Count >= 32)
                {
                    client.Dispose();
                    continue;
                }
                client.NoDelay = true;
                var id = Interlocked.Increment(ref _nextId);
                var task = ServeAsync(client, token);
                _peers[id] = (client, task);
                _ = task.ContinueWith(t =>
{
    _peers.TryRemove(id, out _);
}
, TaskScheduler.Default);
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {

        }

        catch (Exception ex)
        {
            Activity?.Invoke($"监听失败：{ex.Message}");
        }

    }
    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "未知端点";
            var stream = client.GetStream();
            while (!token.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                idle.CancelAfter(TimeSpan.FromSeconds(30));
                var request = await ModbusWire.ReadPacketAsync(stream, ModbusRole.Slave, endpoint, record => Traffic?.Invoke(record), idle.Token, record => RawTraffic?.Invoke(record)).ConfigureAwait(false);
                var header = request.AsSpan(0, 7).ToArray();
                var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                if (header[2] != 0 || header[3] != 0 || length is < 2 or > 254)
                    throw new IOException("无效的 MBAP 请求。");
                var pdu = request.AsSpan(7).ToArray();
                var response = header[6] == _unit ? _protocol.Process(pdu) : ExceptionPdu(pdu[0], 0x0B);
                BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)(response.Length + 1));
                var packet = new byte[7 + response.Length];
                header.CopyTo(packet, 0);
                response.CopyTo(packet, 7);
                await stream.WriteAsync(packet, idle.Token).ConfigureAwait(false);
                RawTraffic?.Invoke(new(DateTimeOffset.Now, TrafficDirection.Tx, PayloadKind.StreamChunk, packet, endpoint, "MODBUS TCP"));
                Traffic?.Invoke(new(ModbusRole.Slave, TrafficDirection.Tx, endpoint, packet));
                Activity?.Invoke($"{endpoint} · Unit {header[6]} · FC {pdu[0]:D2} · {(response[0] >= 0x80 ? $"异常 0x{response[1]:X2}" : "完成")}");
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            if (!token.IsCancellationRequested && ex is not EndOfStreamException)
                Activity?.Invoke($"客户端结束：{ex.Message}");
        }
        finally
        {
            client.Dispose();
        }

    }
    private static byte[] ExceptionPdu(byte function, byte code) => [(byte)(function | 0x80), code];
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_listener is null)
                return;
            _lifetime!.Cancel();
            _listener.Stop();
            await _acceptTask.ConfigureAwait(false);
            var peers = _peers.Values.ToArray();
            foreach (var peer in peers)
                peer.Client.Dispose();
            await Task.WhenAll(peers.Select(p => p.Task)).ConfigureAwait(false);
            _peers.Clear();
            _listener = null;
            _lifetime.Dispose();
            _lifetime = null;
        }
        finally
        {
            _gate.Release();
        }

    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
