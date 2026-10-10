using System.Net.Sockets;
using Communication.Core;

namespace Communication.Transports;

public sealed class TcpClientSession(ConnectionProfile profile) : SessionBase, IStreamSession
{
    private TcpClient? _client;
    public string? LocalEndpoint
    {
        get; private set;
    }
    private string Endpoint => $"{profile.Host}:{profile.Port}";
    public IReadOnlyList<string> Peers => State == ConnectionState.Connected ? [Endpoint] : [];
    protected override async Task OpenAsync(CancellationToken token)
    {
        _client = new TcpClient { NoDelay = true };
        await _client.ConnectAsync(profile.Host, profile.Port, token).ConfigureAwait(false);
        LocalEndpoint = _client.Client.LocalEndPoint?.ToString();
    }
    protected override async Task ReceiveAsync(CancellationToken token)
    {
        var stream = _client!.GetStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0)
                return;
            Publish(TrafficDirection.Rx, PayloadKind.StreamChunk, buffer.AsSpan(0, count), Endpoint);
        }
    }
    public async Task WriteAsync(ReadOnlyMemory<byte> data, string? peer, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Lifetime);
        await SendGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            linked.Token.ThrowIfCancellationRequested();
            try
            {
                await _client!.GetStream().WriteAsync(data, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                FailWrite(ex);
                throw;
            }
            Publish(TrafficDirection.Tx, PayloadKind.StreamChunk, data.Span, Endpoint);
        }
        finally
        {
            SendGate.Release();
        }

    }
    protected override void Close() => _client?.Dispose();
}
