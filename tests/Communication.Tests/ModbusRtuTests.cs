using System.Collections.Concurrent;
using System.IO;
using Communication.Core;
using Communication.Transports;

internal static class ModbusRtuTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
    private static async Task Eventually(Func<bool> predicate)
    {
        for (var i = 0; i < 150; i++)
        {
            if (predicate())
                return;
            await Task.Delay(10);
        }
        throw new Exception("RTU operation did not complete.");
    }
    public static async Task MasterSlave()
    {
        var standard = ModbusRtuCodec.Encode(1, new byte[] { 3, 0, 0, 0, 2 });
        Check(PayloadCodec.ToHex(standard) == "01 03 00 00 00 02 C4 0B", "RTU standard frame and low-byte-first CRC");
        var (masterPort, slavePort) = MemoryPort.Pair();
        var bank = new ModbusRegisterBank();
        bank.Set(ModbusTable.HoldingRegisters, 0, [0x1234, 0xABCD]);
        bank.Set(ModbusTable.InputRegisters, 0, [100, 200]);
        bank.Set(ModbusTable.Coils, 0, [1, 0, 1]);
        bank.Set(ModbusTable.DiscreteInputs, 0, [0, 1, 1]);
        var options = new ModbusSerialOptions("memory", ResponseTimeoutMs: 1000);
        await using var server = new ModbusRtuServer(bank, _ => slavePort);
        await using var transport = new ModbusRtuMaster(_ => masterPort);
        var masterTraffic = new ConcurrentQueue<ModbusTrafficRecord>();
        var slaveTraffic = new ConcurrentQueue<ModbusTrafficRecord>();
        transport.Traffic += masterTraffic.Enqueue;
        server.Traffic += slaveTraffic.Enqueue;
        await server.StartAsync(options, 1);
        await transport.ConnectAsync(options);
        var client = new ModbusMasterClient(transport);
        foreach (var table in Enum.GetValues<ModbusTable>())
            Check((await client.ReadAsync("", 0, 1, table, 0, 2)).SequenceEqual(bank.Read(table, 0, 2)), $"RTU FC{(int)table:D2}");
        await client.WriteSingleAsync("", 0, 1, ModbusTable.HoldingRegisters, 1, 555);
        await client.WriteSingleAsync("", 0, 1, ModbusTable.Coils, 1, 1);
        await client.WriteMultipleAsync("", 0, 1, ModbusTable.HoldingRegisters, 10, [10, 20, 65535]);
        await client.WriteMultipleAsync("", 0, 1, ModbusTable.Coils, 10, [1, 0, 1, 0, 1, 1, 0, 0, 1]);
        Check(bank.Read(ModbusTable.HoldingRegisters, 1, 1)[0] == 555 && bank.Read(ModbusTable.Coils, 1, 1)[0] == 1, "RTU FC05/06 update bank");
        Check(bank.Read(ModbusTable.HoldingRegisters, 10, 3).SequenceEqual(new ushort[] { 10, 20, 65535 }), "RTU FC16 values");
        Check(bank.Read(ModbusTable.Coils, 10, 9).SequenceEqual(new ushort[] { 1, 0, 1, 0, 1, 1, 0, 0, 1 }), "RTU FC15 bit packing");
        var unsupported = await transport.ExchangeAsync(1, new byte[] { 99 });
        Check(unsupported.SequenceEqual(new byte[] { 0xE3, 1 }), "RTU unsupported function exception");
        Check(transport.IsConnected, "RTU connection is reused between transactions");
        await Eventually(() => slaveTraffic.Count == masterTraffic.Count);
        foreach (var record in masterTraffic)
        {
            Check(record.Transport == ModbusTransport.Rtu && record.ValidCrc == true && record.Transaction is null && record.IsComplete, "RTU logs contain original address/PDU/CRC");
            Check(slaveTraffic.Any(r => r.Hex == record.Hex && r.Direction != record.Direction), "RTU master/slave frame logs match");
        }
        await transport.DisconnectAsync();
        await server.StopAsync();
        Check(!transport.IsConnected && !server.IsRunning && masterPort.Disposed && slavePort.Disposed, "RTU shutdown releases ports");
    }
    public static async Task ValidationAndCancellation()
    {
        var (raw, slavePort) = MemoryPort.Pair();
        var bank = new ModbusRegisterBank();
        var received = new ConcurrentQueue<ModbusTrafficRecord>();
        await using var server = new ModbusRtuServer(bank, _ => slavePort);
        var options = new ModbusSerialOptions("memory", ResponseTimeoutMs: 150);
        server.Traffic += received.Enqueue;
        await server.StartAsync(options, 1);
        var corrupt = ModbusRtuCodec.Encode(1, new byte[] { 6, 0, 0, 0, 42 });
        corrupt[^1] ^= 0x80;
        raw.Write(corrupt);
        await Eventually(() => received.Any(r => r.ValidCrc == false));
        Check(bank.Read(ModbusTable.HoldingRegisters, 0, 1)[0] == 0, "Bad CRC cannot change bank");
        raw.Write(ModbusRtuCodec.Encode(2, new byte[] { 6, 0, 0, 0, 42 }));
        raw.Write(ModbusRtuCodec.Encode(0, new byte[] { 6, 0, 0, 0, 99 }));
        await Eventually(() => bank.Read(ModbusTable.HoldingRegisters, 0, 1)[0] == 99);
        Check(!received.Any(r => r.Direction == TrafficDirection.Tx), "Wrong address and broadcasts produce no response");
        var frame = ModbusRtuCodec.Encode(1, new byte[] { 3, 0, 0, 0, 1 });
        foreach (var b in frame)
        {
            raw.Write([b]);
            await Task.Delay(2);
        }
        await Eventually(() => received.Any(r => r.Direction == TrafficDirection.Tx));
        var response = ReadFrame(raw, 7);
        Check(ModbusRtuCodec.Decode(response).SequenceEqual(new byte[] { 3, 2, 0, 99 }), "Fragmented request is assembled before reply");
        raw.Write(new byte[] { 1, 6, 0 });
        await Eventually(() => received.Any(r => !r.IsComplete && r.Length == 3));
        await server.StopAsync();

        var (silent, unused) = MemoryPort.Pair();
        await using var master = new ModbusRtuMaster(_ => silent);
        var logs = new ConcurrentQueue<ModbusTrafficRecord>();
        master.Traffic += logs.Enqueue;
        await master.ConnectAsync(options);
        using var cancellation = new CancellationTokenSource();
        var pending = master.ExchangeAsync(1, new byte[] { 3, 0, 0, 0, 1 }, cancellation.Token);
        await Eventually(() => logs.Any(r => r.Direction == TrafficDirection.Tx));
        cancellation.Cancel();
        try
        {
            await pending;
            throw new Exception("RTU cancellation was ignored");
        }
        catch (OperationCanceledException)
        {

        }

        Check(!master.IsConnected && silent.Disposed, "Cancelled transaction closes serial port to reject late response");
        unused.Dispose();

        var (timeoutPort, timeoutPeer) = MemoryPort.Pair();
        await using var timedMaster = new ModbusRtuMaster(_ => timeoutPort);
        await timedMaster.ConnectAsync(options);
        try
        {
            await timedMaster.ExchangeAsync(1, new byte[] { 3, 0, 0, 0, 1 });
            throw new Exception("RTU timeout was ignored");
        }
        catch (TimeoutException)
        {

        }

        Check(!timedMaster.IsConnected, "RTU timeout closes serial port");
        timeoutPeer.Dispose();

        var (fragmentPort, fragmentPeer) = MemoryPort.Pair();
        await using var fragmentMaster = new ModbusRtuMaster(_ => fragmentPort);
        await fragmentMaster.ConnectAsync(options with
        {
            ResponseTimeoutMs = 1000
        });
        var fragmentedExchange = fragmentMaster.ExchangeAsync(1, new byte[] { 3, 0, 0, 0, 1 });
        ReadFrame(fragmentPeer, 8);
        foreach (var b in ModbusRtuCodec.Encode(1, new byte[] { 3, 2, 0x12, 0x34 }))
        {
            fragmentPeer.Write([b]);
            await Task.Delay(2);
        }
        Check((await fragmentedExchange).SequenceEqual(new byte[] { 3, 2, 0x12, 0x34 }), "Fragmented master response is assembled");
        var cancelledByClose = fragmentMaster.ExchangeAsync(1, new byte[] { 3, 0, 0, 0, 1 });
        ReadFrame(fragmentPeer, 8);
        await Task.WhenAll(fragmentMaster.DisconnectAsync(), fragmentMaster.DisconnectAsync());
        try
        {
            await cancelledByClose;
            throw new Exception("Disconnect did not cancel active RTU request");
        }
        catch (OperationCanceledException)
        {

        }

        Check(!fragmentMaster.IsConnected && fragmentPort.Disposed, "Concurrent disconnect safely cancels active RTU request");
        fragmentPeer.Dispose();

        var (crcPort, crcPeer) = MemoryPort.Pair();
        await using var crcMaster = new ModbusRtuMaster(_ => crcPort);
        await crcMaster.ConnectAsync(options);
        var exchange = crcMaster.ExchangeAsync(1, new byte[] { 3, 0, 0, 0, 1 });
        ReadFrame(crcPeer, 8);
        var badReply = ModbusRtuCodec.Encode(1, new byte[] { 3, 2, 0, 10 });
        badReply[^1] ^= 1;
        crcPeer.Write(badReply);
        try
        {
            await exchange;
            throw new Exception("Bad response CRC was accepted");
        }
        catch (IOException)
        {

        }

        Check(!crcMaster.IsConnected, "Bad response CRC invalidates transaction and closes port");
        crcPeer.Dispose();
    }
    private static byte[] ReadFrame(MemoryPort port, int length)
    {
        var frame = new byte[length];
        var count = 0;
        while (count < length)
        {
            var n = port.Read(frame, count, length - count, 1000);
            if (n == 0)
                throw new Exception("Test serial read timed out");
            count += n;
        }
        return frame;
    }
    internal sealed class MemoryPort(BlockingCollection<byte> incoming, BlockingCollection<byte> outgoing) : IModbusRtuPort
    {
        public bool Disposed
        {
            get; private set;
        }
        public static (MemoryPort, MemoryPort) Pair()
        {
            var a = new BlockingCollection<byte>();
            var b = new BlockingCollection<byte>();
            return (new(a, b), new(b, a));
        }
        public int Read(byte[] buffer, int offset, int count, int timeoutMs)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (!incoming.TryTake(out var first, timeoutMs))
                return 0;
            buffer[offset] = first;
            var read = 1;
            while (read < count && incoming.TryTake(out var b))
                buffer[offset + read++] = b;
            return read;
        }
        public void Write(byte[] data)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            foreach (var b in data)
                outgoing.Add(b);
        }
        public void DiscardInput()
        {
            while (incoming.TryTake(out _))
            {
            }
        }
        public void Dispose() => Disposed = true;
    }
}
