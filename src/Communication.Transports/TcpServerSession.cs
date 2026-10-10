using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Communication.Core;

namespace Communication.Transports;

public sealed class TcpServerSession(ConnectionProfile profile) : SessionBase, IStreamSession
{
    private readonly ConcurrentDictionary<string, TcpClient> _clients = new();
    private readonly ConcurrentDictionary<long, Task> _receivers = new();
    private TcpListener? _listener;
    private long _nextId;
    public IReadOnlyList<string> Peers => _clients.Keys.Order().ToArray();
    public event Action<string>? PeerChanged;
    protected override Task OpenAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _listener = new TcpListener(IPAddress.Parse(profile.LocalAddress), profile.LocalPort);
        _listener.Start(32);
        return Task.CompletedTask;
    }
    protected override async Task ReceiveAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                var client = await _listener!.AcceptTcpClientAsync(token).ConfigureAwait(false);
                if (_clients.Count >= 64)
                {
                    client.Dispose();
                    continue;
                }
                client.NoDelay = true;
                var id = Interlocked.Increment(ref _nextId);
                var endpoint = $"{client.Client.RemoteEndPoint} · {id}";
                _clients[endpoint] = client;
                PeerChanged?.Invoke($"客户端接入 {endpoint}");
                var receiver = ReceivePeerAsync(client, endpoint, token);
                _receivers[id] = receiver;
                _ = receiver.ContinueWith(t =>
{
    _receivers.TryRemove(id, out _);
}
, TaskScheduler.Default);
            }
        }
        finally
        {
            foreach (var client in _clients.Values)
                client.Dispose();
            await Task.WhenAll(_receivers.Values).ConfigureAwait(false);
        }
    }
    private async Task ReceivePeerAsync(TcpClient client, string endpoint, CancellationToken token)
    {
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count == 0)
                    break;
                Publish(TrafficDirection.Rx, PayloadKind.StreamChunk, buffer.AsSpan(0, count), endpoint);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            if (!token.IsCancellationRequested)
                PeerChanged?.Invoke($"客户端结束 {endpoint}: {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(endpoint, out _);
            client.Dispose();
            PeerChanged?.Invoke($"客户端断开 {endpoint}");
        }
    }
    public async Task WriteAsync(ReadOnlyMemory<byte> data, string? peer, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Lifetime);
        await SendGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            if (peer is null || !_clients.TryGetValue(peer, out var client))
                throw new InvalidOperationException("请选择在线客户端。");
            linked.Token.ThrowIfCancellationRequested();
            try
            {
                await client.GetStream().WriteAsync(data, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // A partial stream write makes only this peer ambiguous, not the listener.
                client.Dispose();
                throw;
            }
            Publish(TrafficDirection.Tx, PayloadKind.StreamChunk, data.Span, peer);
        }
        finally
        {
            SendGate.Release();
        }

    }
    protected override void Close()
    {
        _listener?.Stop();
        foreach (var client in _clients.Values)
            client.Dispose();
    }
}
