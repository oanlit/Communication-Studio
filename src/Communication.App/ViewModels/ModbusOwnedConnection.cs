using System.Net;
using Communication.Core;

namespace Communication.App.ViewModels;

// A display/ownership snapshot, never a second reader or sender for Modbus I/O.
public sealed record ModbusOwnedConnection(ConnectionProfile Profile, string Owner)
{
    public ModbusTrafficCounters Counters { get; } = new();
    public bool ConflictsWith(ConnectionProfile other)
    {
        if (Profile.Kind == TransportKind.Serial && other.Kind == TransportKind.Serial)
            return string.Equals(NormalizePort(Profile.SerialPort), NormalizePort(other.SerialPort), StringComparison.OrdinalIgnoreCase);
        if (Profile.Kind != TransportKind.TcpServer || other.Kind != TransportKind.TcpServer || Profile.LocalPort != other.LocalPort)
            return false;
        if (!IPAddress.TryParse(Profile.LocalAddress, out var owned) || !IPAddress.TryParse(other.LocalAddress, out var candidate))
            return false;
        return owned.Equals(candidate) || (owned.AddressFamily == candidate.AddressFamily && (owned.Equals(IPAddress.Any) || candidate.Equals(IPAddress.Any) || owned.Equals(IPAddress.IPv6Any) || candidate.Equals(IPAddress.IPv6Any)));
    }
    private static string NormalizePort(string value)
    {
        var port = value.Trim();
        return port.StartsWith(@"\\.\", StringComparison.Ordinal) ? port[4..] : port;
    }
}

public sealed class ModbusTrafficCounters
{
    private long _txBytes, _rxBytes, _txOperations, _rxOperations;
    public SessionStatistics Snapshot => new(Interlocked.Read(ref _txBytes), Interlocked.Read(ref _rxBytes), Interlocked.Read(ref _txOperations), Interlocked.Read(ref _rxOperations));
    public void Record(TrafficRecord record)
    {
        if (record.Direction == TrafficDirection.Tx)
        {
            Interlocked.Add(ref _txBytes, record.Data.Length);
            Interlocked.Increment(ref _txOperations);
        }
        else
        {
            Interlocked.Add(ref _rxBytes, record.Data.Length);
            Interlocked.Increment(ref _rxOperations);
        }
    }
}
