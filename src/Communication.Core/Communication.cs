namespace Communication.Core;

public enum ConnectionState
{
    Disconnected, Connecting, Connected, Disconnecting, Faulted
}
public enum TransportKind
{
    TcpClient, TcpServer, Udp, Serial
}
public enum TrafficDirection
{
    Tx, Rx
}
public enum PayloadKind
{
    StreamChunk, Datagram, CanFrame
}

// Stream chunks reflect reads, never application-message boundaries.
public sealed record TrafficRecord(DateTimeOffset Timestamp, TrafficDirection Direction,
    PayloadKind Kind, byte[] Data, string Endpoint, string? Protocol = null);
public sealed record ConnectionChange(ConnectionState State, string Detail);
public sealed record SessionStatistics(long TxBytes, long RxBytes, long TxOperations, long RxOperations);

public sealed record ConnectionProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public TransportKind Kind
    {
        get; init;
    }
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 5000;
    public string LocalAddress { get; init; } = "0.0.0.0";
    public int LocalPort { get; init; } = 5001;
    public string SerialPort { get; init; } = "COM1";
    public int BaudRate { get; init; } = 115200;
    public int DataBits { get; init; } = 8;
    public string Parity { get; init; } = "None";
    public string StopBits { get; init; } = "One";
    public string Handshake { get; init; } = "None";
    public override string ToString() => Name;
}

public interface ICommunicationSession : IAsyncDisposable
{
    ConnectionState State
    {
        get;
    }
    SessionStatistics Statistics
    {
        get;
    }
    event Action<ConnectionChange>? StateChanged;
    event Action<TrafficRecord>? Traffic;
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync();
}

public interface IStreamSession : ICommunicationSession
{
    // For servers, peer must be explicitly selected; writes are serialized per session.
    IReadOnlyList<string> Peers
    {
        get;
    }
    Task WriteAsync(ReadOnlyMemory<byte> data, string? peer, CancellationToken cancellationToken = default);
}

public interface IDatagramSession : ICommunicationSession
{
    Task SendDatagramAsync(ReadOnlyMemory<byte> data, string host, int port, CancellationToken cancellationToken = default);
}

// A future CAN adapter preserves identifier, flags and frame boundaries.
public sealed record CanFrame(uint Identifier, byte[] Data, bool Extended = false, bool Remote = false, bool Fd = false);
public interface ICanSession : ICommunicationSession
{
    event Action<CanFrame>? FrameReceived;
    Task SendFrameAsync(CanFrame frame, CancellationToken cancellationToken = default);
}
