using Communication.Core;

namespace Communication.App.ViewModels;

public sealed class TrafficRow(long sequence, Guid connectionId, string connection, TrafficRecord record, bool hex, TransportKind? transport = null, string? peerName = null, string? connectionAddress = null) : ObservableObject
{
    private bool _hex = hex;
    public long Sequence { get; } = sequence;
    public Guid ConnectionId { get; } = connectionId;
    public string Connection { get; } = connection;
    public string ConnectionAddress { get; } = connectionAddress ?? "";
    public TrafficRecord Record { get; } = record;
    public DateTimeOffset Timestamp => Record.Timestamp;
    public string Time => Record.Timestamp.ToString("HH:mm:ss.fff");
    public string Direction => Record.Direction.ToString().ToUpperInvariant();
    public int Length => Record.Data.Length;
    // Serial drivers expose the local port, not the identity of the remote device.
    public string Endpoint => transport == TransportKind.Serial ? "—" : Record.Endpoint;
    public string PeerName => peerName ?? Endpoint;
    public string Kind => Record.Protocol ?? (Record.Kind == PayloadKind.Datagram ? "DATAGRAM" : "STREAM");
    public string Preview => Format(Record.Data.AsSpan(0, Math.Min(256, Length))) + (Length > 256 ? " …" : "");
    public string Detail => $"{Connection}  ·  {Record.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}  ·  {Direction}  ·  {Kind}  ·  {Length} bytes\n{Endpoint}\n\n{Format(Record.Data)}";
    private string Format(ReadOnlySpan<byte> bytes) => _hex ? PayloadCodec.ToHex(bytes) : PayloadCodec.ToText(bytes);
    public void SetHex(bool value)
    {
        _hex = value;
        Notify(nameof(Preview));
        Notify(nameof(Detail));
    }
}

public sealed record LogRow(DateTimeOffset Timestamp, string Level, string Connection, string Message)
{
    public string Time => Timestamp.ToString("HH:mm:ss.fff");
}
