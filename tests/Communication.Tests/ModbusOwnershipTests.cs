using System.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Communication.App.ViewModels;
using Communication.Core;

internal static class ModbusOwnershipTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
    private static async Task Eventually(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (predicate())
                return;
            await Task.Delay(10);
        }
        throw new Exception("Modbus traffic synchronization timed out.");
    }
    public static async Task Run()
    {
        var path = Path.Combine(Path.GetTempPath(), $"studio-ownership-{Guid.NewGuid():N}.json");
        var workspace = new WorkspaceViewModel(path);
        var peers = new ConcurrentDictionary<string, ModbusRtuTests.MemoryPort>();
        var modbus = new ModbusViewModel(options =>
        {
            if (options.PortName == "COM9")
                throw new UnauthorizedAccessException("已占有");
            var pair = ModbusRtuTests.MemoryPort.Pair();
            peers[options.PortName] = pair.Item2;
            return pair.Item1;
        });
        var shell = new ShellViewModel(workspace, modbus);
        try
        {
            var serial = new ConnectionViewModel(new()
            {
                Name = "Serial test",
                Kind = TransportKind.Serial,
                SerialPort = "com1"
            });
            workspace.Connections.Add(serial);
            workspace.Selected = serial;
            modbus.Transport = ModbusTransport.Rtu;
            modbus.ResponseTimeout = "100";
            await modbus.ConnectRtuCommand.ExecuteAsync();
            Check(serial.IsOccupied && serial.StateLabel == "已占用" && workspace.Connections.Count == 1, "Existing serial profile is marked occupied without duplicate mirror");
            peers["COM1"].Write(new byte[] { 0xDE, 0xAD, 0xBE });
            await Eventually(() => serial.Statistics.RxBytes == 3 && workspace.Traffic.Any(row => row.ConnectionId == serial.Id));
            Check(workspace.Traffic.Where(row => row.ConnectionId == serial.Id && row.Direction == "RX").SelectMany(row => row.Record.Data).SequenceEqual(new byte[] { 0xDE, 0xAD, 0xBE }), "Idle RTU master mirrors unsolicited external bytes without a UI request");
            Check(serial.Rx == "3 B" && workspace.TotalRx == "3 B", "Connection and workspace statistics include unsolicited RX");
            var alias = new ConnectionViewModel(serial.Profile with
            {
                Id = Guid.NewGuid(),
                Name = "Serial alias",
                SerialPort = @"\\.\COM1"
            });
            workspace.Connections.Add(alias);
            workspace.SynchronizeModbusConnections();
            Check(alias.Statistics.RxBytes == 3 && workspace.TotalRx == "3 B", "Duplicate profiles share port counters without double-counting workspace totals");
            workspace.Connections.Remove(alias);
            serial.State = ConnectionState.Connected;
            Check(!workspace.SendCommand.CanExecute(null) && !workspace.PeriodicCommand.CanExecute(null) && !workspace.CanUseSendArea && !workspace.DisconnectCommand.CanExecute(null), "Modbus ownership blocks raw and periodic sends even for an ONLINE display state");
            serial.State = ConnectionState.Disconnected;
            Check(!workspace.ConnectCommand.CanExecute(null) && serial.CanEdit, "Occupied normal profile cannot connect but can choose another port");
            modbus.SlaveMode = true;
            modbus.SerialPortName = "COM2";
            await modbus.StartSlaveCommand.ExecuteAsync();
            var slave = workspace.Connections.Single(connection => connection.IsModbusManaged);
            workspace.Selected = slave;
            Check(slave.SerialPort == "COM2" && slave.IsOccupied && !slave.CanEdit && !workspace.CanUseSendArea && !workspace.ConnectCommand.CanExecute(null), "RTU slave appears as a read-only occupied connection");
            var badCrc = ModbusRtuCodec.Encode(1, new byte[] { 6, 0, 10, 0, 42 });
            badCrc[^1] ^= 1;
            peers["COM2"].Write(badCrc);
            await Eventually(() => slave.Statistics.RxBytes == 8 && workspace.Traffic.Where(row => row.ConnectionId == slave.Id && row.Direction == "RX").Sum(row => row.Length) == 8);
            Check(slave.Statistics.TxBytes == 0, "Invalid CRC bytes are mirrored despite protocol rejection");
            var wrongUnit = ModbusRtuCodec.Encode(2, new byte[] { 6, 0, 10, 0, 42 });
            peers["COM2"].Write(wrongUnit);
            var externalWrite = ModbusRtuCodec.Encode(1, new byte[] { 6, 0, 10, 0, 42 });
            peers["COM2"].Write(externalWrite);
            await Eventually(() => slave.Statistics.RxBytes == 24 && slave.Statistics.TxBytes == 8 && workspace.Traffic.Where(row => row.ConnectionId == slave.Id).Sum(row => row.Length) == 32);
            Check(workspace.Traffic.Where(row => row.ConnectionId == slave.Id && row.Direction == "RX").SelectMany(row => row.Record.Data).SequenceEqual(badCrc.Concat(wrongUnit).Concat(externalWrite)), "External requests and ignored addresses retain every RX byte");
            Check(workspace.Traffic.Single(row => row.ConnectionId == slave.Id && row.Direction == "TX").Record.Data.SequenceEqual(externalWrite), "Slave response raw TX is mirrored once");
            var beforePause = workspace.Traffic.Count;
            workspace.Paused = true;
            peers["COM2"].Write(ModbusRtuCodec.Encode(1, new byte[] { 6, 0, 11, 0, 43 }));
            await Eventually(() => slave.Statistics.RxBytes == 32 && slave.Statistics.TxBytes == 16);
            await Task.Delay(200);
            Check(workspace.Traffic.Count == beforePause, "Paused monitor continues statistics without adding visible rows");
            workspace.Paused = false;
            serial.SerialPort = "COM3";
            workspace.SynchronizeModbusConnections();
            Check(!serial.IsOccupied && workspace.Connections.Count(connection => connection.IsModbusManaged) == 2, "Moving normal profile to free port reveals both active Modbus resources");
            await workspace.SaveCommand.ExecuteAsync();
            var saved = await new ProfileStore(path).LoadAsync();
            Check(saved.Count == 1 && saved[0].SerialPort == "COM3", "Transient Modbus connections are excluded from configuration persistence");
            modbus.SlaveMode = false;
            await modbus.DisconnectRtuCommand.ExecuteAsync();
            Check(workspace.Connections.Count(connection => connection.IsModbusManaged) == 1, "Closing master removes only its mirror");
            modbus.SlaveMode = true;
            await modbus.StopSlaveCommand.ExecuteAsync();
            Check(!workspace.Connections.Any(connection => connection.IsModbusManaged), "Stopping slave removes its mirror");
            modbus.SlaveMode = false;
            modbus.SerialPortName = "COM3";
            await modbus.ConnectRtuCommand.ExecuteAsync();
            Check(serial.IsOccupied, "Reopening on a new port updates occupancy");
            await modbus.ReadCommand.ExecuteAsync();
            Check(!modbus.IsMasterConnected && !serial.IsOccupied && modbus.OwnedConnections.Count == 0, "RTU timeout immediately releases workspace occupancy");
            modbus.SerialPortName = "COM9";
            await modbus.ConnectRtuCommand.ExecuteAsync();
            Check(modbus.OwnedConnections.Count == 0 && !workspace.Connections.Any(connection => connection.IsModbusManaged), "Failed open creates no occupied connection");

            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var listener = new ConnectionViewModel(new()
            {
                Name = "TCP listener test",
                Kind = TransportKind.TcpServer,
                LocalAddress = "127.0.0.1",
                LocalPort = port
            });
            var client = new ConnectionViewModel(new()
            {
                Name = "TCP client test",
                Kind = TransportKind.TcpClient,
                Host = "127.0.0.1",
                Port = port
            });
            var udp = new ConnectionViewModel(new()
            {
                Name = "UDP test",
                Kind = TransportKind.Udp,
                LocalAddress = "127.0.0.1",
                LocalPort = port
            });
            workspace.Connections.Add(listener);
            workspace.Connections.Add(client);
            workspace.Connections.Add(udp);
            modbus.Transport = ModbusTransport.Tcp;
            modbus.SlaveMode = true;
            modbus.LocalAddress = "0.0.0.0";
            modbus.LocalPort = port.ToString();
            await modbus.StartSlaveCommand.ExecuteAsync();
            workspace.Selected = listener;
            Check(listener.IsOccupied && !workspace.ConnectCommand.CanExecute(null) && !workspace.CanUseSendArea, "Wildcard TCP Modbus listener marks conflicting local listener occupied");
            Check(!client.IsOccupied && !udp.IsOccupied, "Remote TCP destination and UDP port do not share TCP bind ownership");
            using (var external = new TcpClient())
            {
                await external.ConnectAsync(IPAddress.Loopback, port);
                var stream = external.GetStream();
                var request = new byte[] { 0, 91, 0, 0, 0, 6, 1, 6, 0, 14, 0, 232 };
                await stream.WriteAsync(request.AsMemory(0, 3));
                await Eventually(() => listener.Statistics.RxBytes == 3 && workspace.Traffic.Where(row => row.ConnectionId == listener.Id).Sum(row => row.Length) == 3);
                Check(listener.Statistics.TxBytes == 0, "TCP partial bytes appear before frame completion");
                await stream.WriteAsync(request.AsMemory(3));
                var reply = new byte[12];
                await stream.ReadExactlyAsync(reply);
                await Eventually(() => listener.Statistics.RxBytes == 12 && listener.Statistics.TxBytes == 12 && workspace.Traffic.Where(row => row.ConnectionId == listener.Id).Sum(row => row.Length) == 24);
                Check(reply.SequenceEqual(request) && workspace.Traffic.Where(row => row.ConnectionId == listener.Id && row.Direction == "RX").SelectMany(row => row.Record.Data).SequenceEqual(request), "Independent TCP sender and slave response mirror complete wire bytes");
                var local = (IPEndPoint)external.Client.LocalEndPoint!;
                Check(workspace.Traffic.Where(row => row.ConnectionId == listener.Id).All(row => row.Kind == "MODBUS TCP" && IPEndPoint.Parse(row.Endpoint) is { } peer && peer.Port == local.Port && peer.Address.MapToIPv6().Equals(local.Address.MapToIPv6())), "Mirrored TCP records preserve source peer and transport label");
                await stream.WriteAsync(new byte[] { 0, 92, 0, 0, 0, 0, 1 });
                await Eventually(() => listener.Statistics.RxBytes == 19 && workspace.Traffic.Where(row => row.ConnectionId == listener.Id && row.Direction == "RX").Sum(row => row.Length) == 19);
            }
            modbus.LocalPort = (port == 65535 ? port - 1 : port + 1).ToString();
            workspace.SynchronizeModbusConnections();
            Check(listener.IsOccupied, "Ownership snapshot preserves actual bound port despite draft edits");
            await modbus.StopSlaveCommand.ExecuteAsync();
            Check(!listener.IsOccupied && workspace.ConnectCommand.CanExecute(null) && workspace.CanUseSendArea, "Stop listening restores connection and send area availability");
        }
        finally
        {
            await shell.ShutdownAsync();
            foreach (var peer in peers.Values)
                peer.Dispose();
            File.Delete(path);
        }

    }
}
