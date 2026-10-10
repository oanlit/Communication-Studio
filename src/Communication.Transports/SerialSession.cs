using System.IO.Ports;
using Communication.Core;

namespace Communication.Transports;

public sealed class SerialSession(ConnectionProfile profile) : SessionBase, IStreamSession
{
    private SerialPort? _port;
    public IReadOnlyList<string> Peers => State == ConnectionState.Connected ? [profile.SerialPort] : [];
    protected override Task OpenAsync(CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        _port = new SerialPort(profile.SerialPort, profile.BaudRate, Enum.Parse<Parity>(profile.Parity),
            profile.DataBits, Enum.Parse<StopBits>(profile.StopBits))
        {
            Handshake = Enum.Parse<Handshake>(profile.Handshake),
            ReadTimeout = 200,
            WriteTimeout = 1000
        };
        _port.Open();
    }, token);

    // SerialPort cancellation differs across drivers. Bounded synchronous reads on a worker
    // ensure cancellation is observed even when BaseStream.ReadAsync ignores its token.
    protected override Task ReceiveAsync(CancellationToken token) => Task.Run(() =>
    {
        var buffer = new byte[8192];
        while (!token.IsCancellationRequested)
        {
            try
            {
                var count = _port!.Read(buffer, 0, buffer.Length);
                Publish(TrafficDirection.Rx, PayloadKind.StreamChunk, buffer.AsSpan(0, count), profile.SerialPort);
            }
            catch (TimeoutException)
            {

            }

        }
    }, token);

    public async Task WriteAsync(ReadOnlyMemory<byte> data, string? peer, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Lifetime);
        await SendGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            var bytes = data.ToArray();
            linked.Token.ThrowIfCancellationRequested();
            try
            {
                await Task.Run(() =>
{
    linked.Token.ThrowIfCancellationRequested();
    _port!.Write(bytes, 0, bytes.Length);
}
, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or OperationCanceledException)
            {
                FailWrite(ex);
                throw;
            }
            Publish(TrafficDirection.Tx, PayloadKind.StreamChunk, bytes, profile.SerialPort);
        }
        finally
        {
            SendGate.Release();
        }

    }
    protected override void Close() => _port?.Dispose();
}
