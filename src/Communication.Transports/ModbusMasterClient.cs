using System.Buffers.Binary;
using System.Net.Sockets;
using Communication.Core;

namespace Communication.Transports;

public sealed class ModbusMasterClient
{
    private readonly ModbusRtuMaster? _rtu;
    public ModbusMasterClient(ModbusRtuMaster? rtu = null)
    {
        _rtu = rtu;
        if (rtu is not null)
            rtu.Traffic += record => Traffic?.Invoke(record);
    }
    private int _transaction;
    public event Action<ModbusTrafficRecord>? Traffic;
    public async Task<ushort[]> ReadAsync(string host, int port, byte unit, ModbusTable table, ushort start, ushort count, CancellationToken token = default)
    {
        var bits = table is ModbusTable.Coils or ModbusTable.DiscreteInputs;
        if (!Enum.IsDefined(table) || count < 1 || count > (bits ? 2000 : 125) || start + count > 65536)
            throw new ArgumentOutOfRangeException(nameof(count), "读取数量或地址范围无效：位表最多 2000，寄存器最多 125。");
        var pdu = await ExchangeAsync(host, port, unit, AddressPdu((byte)table, start, count), token).ConfigureAwait(false);
        var size = bits ? (count + 7) / 8 : count * 2;
        if (pdu.Length != size + 2 || pdu[1] != size)
            throw new IOException("Modbus 响应数据长度不匹配。");
        var values = new ushort[count];
        for (var i = 0; i < count; i++)
            values[i] = bits ? (ushort)((pdu[2 + i / 8] >> (i % 8)) & 1) : BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(2 + i * 2));
        return values;
    }
    public async Task WriteSingleAsync(string host, int port, byte unit, ModbusTable table, ushort address, ushort value, CancellationToken token = default)
    {
        ValidateWritable(table);
        if (table == ModbusTable.Coils && value > 1)
            throw new ArgumentException("线圈值应为 0 或 1。");
        var request = AddressPdu(table == ModbusTable.Coils ? (byte)5 : (byte)6, address, table == ModbusTable.Coils ? (ushort)(value == 1 ? 0xFF00 : 0) : value);
        var response = await ExchangeAsync(host, port, unit, request, token).ConfigureAwait(false);
        if (!response.SequenceEqual(request))
            throw new IOException("Modbus 写入确认不匹配。");
    }
    public async Task WriteMultipleAsync(string host, int port, byte unit, ModbusTable table, ushort start, ushort[] values, CancellationToken token = default)
    {
        ValidateWritable(table);
        var bits = table == ModbusTable.Coils;
        if (values.Length < 1 || values.Length > (bits ? 1968 : 123) || start + values.Length > 65536)
            throw new ArgumentOutOfRangeException(nameof(values), "批量写入数量或地址范围无效。");
        if (bits && values.Any(v => v > 1))
            throw new ArgumentException("线圈值应为 0 或 1。");
        var size = bits ? (values.Length + 7) / 8 : values.Length * 2;
        var request = new byte[size + 6];
        AddressPdu(bits ? (byte)15 : (byte)16, start, (ushort)values.Length).CopyTo(request, 0);
        request[5] = (byte)size;
        for (var i = 0; i < values.Length; i++)
        {
            if (bits)
                request[6 + i / 8] |= (byte)(values[i] << (i % 8));
            else
                BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6 + i * 2), values[i]);
        }
        var response = await ExchangeAsync(host, port, unit, request, token).ConfigureAwait(false);
        if (!response.AsSpan().SequenceEqual(request.AsSpan(0, 5)))
            throw new IOException("Modbus 批量写入确认不匹配。");
    }
    private static void ValidateWritable(ModbusTable table)
    {
        if (table is not (ModbusTable.Coils or ModbusTable.HoldingRegisters))
            throw new ArgumentException("仅线圈和保持寄存器可通过协议写入。");
    }
    private static byte[] AddressPdu(byte function, ushort start, ushort value)
    {
        var pdu = new byte[5];
        pdu[0] = function;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), value);
        return pdu;
    }
    private async Task<byte[]> ExchangeAsync(string host, int port, byte unit, byte[] pdu, CancellationToken token)
    {
        if (_rtu is not null)
        {
            var received = await _rtu.ExchangeAsync(unit, pdu, token).ConfigureAwait(false);
            if (received.Length == 2 && received[0] == (pdu[0] | 0x80))
                throw new IOException($"Modbus RTU 异常响应：0x{received[1]:X2}");
            if (received.Length == 0 || received[0] != pdu[0])
                throw new IOException("Modbus RTU 功能码不匹配。");
            return received;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        var stream = client.GetStream();
        var transaction = unchecked((ushort)Interlocked.Increment(ref _transaction));
        var request = new byte[pdu.Length + 7];
        BinaryPrimitives.WriteUInt16BigEndian(request, transaction);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)(pdu.Length + 1));
        request[6] = unit;
        pdu.CopyTo(request, 7);
        await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);
        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? $"{host}:{port}";
        Traffic?.Invoke(new(ModbusRole.Master, TrafficDirection.Tx, endpoint, request));
        var packet = await ModbusWire.ReadPacketAsync(stream, ModbusRole.Master, endpoint, record => Traffic?.Invoke(record), timeout.Token).ConfigureAwait(false);
        var header = packet.AsSpan(0, 7).ToArray();
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        if (BinaryPrimitives.ReadUInt16BigEndian(header) != transaction || header[2] != 0 || header[3] != 0 || header[6] != unit || length is < 3 or > 254)
            throw new IOException("无效的 Modbus MBAP 响应。");
        var response = packet.AsSpan(7).ToArray();
        if (response[0] == (pdu[0] | 0x80) && response.Length == 2)
            throw new IOException($"Modbus 异常响应：0x{response[1]:X2}");
        if (response[0] != pdu[0])
            throw new IOException("Modbus 功能码不匹配。");
        return response;
    }
}
