using Communication.Core;

namespace Communication.Transports;

// Compatibility entry point for the original FC03 tool; one shared protocol implementation.
public sealed class ModbusTcpClient
{
    private readonly ModbusMasterClient _master = new();
    public Task<ushort[]> ReadHoldingRegistersAsync(string host, int port, byte unit, ushort start, ushort count, CancellationToken cancellationToken = default) =>
        _master.ReadAsync(host, port, unit, ModbusTable.HoldingRegisters, start, count, cancellationToken);
}
