using System.Net;
using System.Net.Sockets;
using Communication.Core;

namespace Communication.Transports;

public sealed class UdpSession(ConnectionProfile profile) : SessionBase, IDatagramSession
{
    private UdpClient? _client;
    protected override Task OpenAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _client = new UdpClient(new IPEndPoint(IPAddress.Parse(profile.LocalAddress), profile.LocalPort));
        if (_client.Client.AddressFamily == AddressFamily.InterNetwork)
            _client.EnableBroadcast = true;
        return Task.CompletedTask;
    }
    protected override async Task ReceiveAsync(CancellationToken token)
    {
        while (true)
        {
            var packet = await _client!.ReceiveAsync(token).ConfigureAwait(false);
            Publish(TrafficDirection.Rx, PayloadKind.Datagram, packet.Buffer, packet.RemoteEndPoint.ToString());
        }
    }
    public async Task SendDatagramAsync(ReadOnlyMemory<byte> data, string host, int port, CancellationToken cancellationToken = default)
    {
        if (data.Length > 65507)
            throw new ArgumentException("UDP 负载不能超过 65507 bytes。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Lifetime);
        await SendGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            await _client!.SendAsync(data, host, port, linked.Token).ConfigureAwait(false);
            Publish(TrafficDirection.Tx, PayloadKind.Datagram, data.Span, $"{host}:{port}");
        }
        finally
        {
            SendGate.Release();
        }

    }
    protected override void Close() => _client?.Dispose();
}
