using Communication.Core;

namespace Communication.Transports;

public static class SessionFactory
{
    public static ICommunicationSession Create(ConnectionProfile profile) => profile.Kind switch
    {
        TransportKind.TcpClient => new TcpClientSession(profile),
        TransportKind.TcpServer => new TcpServerSession(profile),
        TransportKind.Udp => new UdpSession(profile),
        TransportKind.Serial => new SerialSession(profile),
        _ => throw new NotSupportedException($"Unsupported transport: {profile.Kind}")
    };
}
