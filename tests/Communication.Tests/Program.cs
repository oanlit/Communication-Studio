using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Input;
using Communication.App.ViewModels;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Communication.Core;
using Communication.Transports;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Text / HEX codec", Codec),
    ("Data converter formats, endian, boundaries and invalid input", DataConversionTests.Run),
    ("TCP receive, send, remote EOF and reconnect", TcpLifecycle),
    ("TCP server multi-peer routing and peer disconnect", ServerRouting),
    ("UDP datagram boundaries including empty datagram", DatagramBoundaries),
    ("Cancellation, connect failure and concurrent disconnect", Cancellation),
    ("Concurrent stream writes preserve write ordering boundaries", ConcurrentWrites),
    ("Blocked stream write cancellation closes ambiguous session", BlockedWrite),
    ("Modbus fragmented response and validation", Modbus),
    ("Checksum standard vectors and Modbus wire order", ChecksumVectors),
    ("Modbus master/slave all tables, writes, exceptions and shutdown", ModbusMasterSlave),
    ("Modbus RTU all functions, CRC and original frames", ModbusRtuTests.MasterSlave),
    ("Modbus RTU fragmented frames, broadcasts, CRC errors, timeout and cancellation", ModbusRtuTests.ValidationAndCancellation),
    ("Modbus packet semantics, field grouping and incomplete frames", ModbusPacketTests.Decode),
    ("Atomic profile round-trip and malformed config", Profiles),
    ("WPF dispatcher traffic, filtering, bounded view and shutdown", WpfWorkspace)
};
var failed = 0;
foreach (var test in tests)
{
    // UI checks include multiple full-window renders, routing and live appearance switching.
    try
    {
        await test.Run().WaitAsync(TimeSpan.FromSeconds(test.Run == WpfWorkspace ? 90 : 20));
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL  {test.Name}\n{ex}");
    }

}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} checks passed");
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

static async Task Eventually(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    while (!condition())
        await Task.Delay(10, timeout.Token);
}
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try
    {
        await action();
    }
    catch (T)
    {
        return;
    }

    throw new Exception($"Expected {typeof(T).Name}");
}
static TcpListener Listen()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return listener;
}
static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;
static Task Codec()
{
    Check(PayloadCodec.Parse("00 01\r\nfe FF", true).SequenceEqual(new byte[] { 0, 1, 254, 255 }), "HEX parse");
    Check(PayloadCodec.Parse("设备", false).Length == 6, "UTF-8 byte count");
    Check(PayloadCodec.Parse("a", false, "\r\n").SequenceEqual(new byte[] { 97, 13, 10 }), "Line suffix");
    return Throws<FormatException>(() => Task.FromResult(PayloadCodec.Parse("123", true)));
}
static async Task TcpLifecycle()
{
    using var listener = Listen();
    await using var session = new TcpClientSession(new()
    {
        Port = Port(listener)
    });
    var received = Channel.CreateUnbounded<TrafficRecord>();
    session.Traffic += row => received.Writer.TryWrite(row);
    for (var cycle = 0; cycle < 2; cycle++)
    {
        await session.ConnectAsync();
        using var peer = await listener.AcceptTcpClientAsync();
        await session.WriteAsync(new byte[] { 1, 2, 3 }, null);
        var request = new byte[3];
        await peer.GetStream().ReadExactlyAsync(request);
        Check(request.SequenceEqual(new byte[] { 1, 2, 3 }), "TX bytes");
        await peer.GetStream().WriteAsync(new byte[] { 4, 5 });
        await Eventually(() => session.Statistics.RxBytes == (cycle + 1) * 2);
        peer.Dispose();
        await Eventually(() => session.State == ConnectionState.Disconnected);
    }
    Check(session.Statistics.TxBytes == 6 && session.Statistics.RxBytes == 4, "Counters survive direct session reconnect");
    Check(received.Reader.Count >= 4, "Traffic published");
}
static async Task ServerRouting()
{
    using var reservation = Listen();
    var port = Port(reservation);
    reservation.Stop();
    await using var server = new TcpServerSession(new()
    {
        LocalAddress = "127.0.0.1",
        LocalPort = port
    });
    await server.ConnectAsync();
    using var first = new TcpClient();
    using var second = new TcpClient();
    await first.ConnectAsync(IPAddress.Loopback, port);
    await second.ConnectAsync(IPAddress.Loopback, port);
    await Eventually(() => server.Peers.Count == 2);
    var local = (IPEndPoint)first.Client.LocalEndPoint!;
    var firstEndpoint = new IPEndPoint(local.Address.MapToIPv4(), local.Port).ToString();
    var selected = server.Peers.Single(p => p.StartsWith(firstEndpoint + " ·", StringComparison.Ordinal));
    await server.WriteAsync(new byte[] { 0xAA }, selected);
    var data = new byte[1];
    await first.GetStream().ReadExactlyAsync(data);
    Check(data[0] == 0xAA && second.Available == 0, "Send targets one peer");
    await Throws<InvalidOperationException>(() => server.WriteAsync(data, null));
    first.Dispose();
    await Eventually(() => server.Peers.Count == 1);
    Check(server.State == ConnectionState.Connected, "Listener survives peer EOF");
    await second.GetStream().WriteAsync(new byte[] { 1, 2 });
    await Eventually(() => server.Statistics.RxBytes == 2);
    await server.DisconnectAsync();
    Check(server.Peers.Count == 0, "All peers closed");
    await server.ConnectAsync();
    await server.DisconnectAsync();
}
static async Task DatagramBoundaries()
{
    using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
    reservation.Dispose();
    await using var session = new UdpSession(new()
    {
        LocalAddress = "127.0.0.1",
        LocalPort = port
    });
    var received = Channel.CreateUnbounded<TrafficRecord>();
    session.Traffic += row =>
{
    if (row.Direction == TrafficDirection.Rx)
        received.Writer.TryWrite(row);
}
;
    await session.ConnectAsync();
    await sender.SendAsync(new byte[] { 1, 2 }, new IPEndPoint(IPAddress.Loopback, port));
    await sender.SendAsync(Array.Empty<byte>(), new IPEndPoint(IPAddress.Loopback, port));
    var a = await received.Reader.ReadAsync();
    var b = await received.Reader.ReadAsync();
    Check(a.Data.Length == 2 && b.Data.Length == 0 && b.Kind == PayloadKind.Datagram, "Datagram boundaries");
    await session.SendDatagramAsync(new byte[] { 9 }, "127.0.0.1", ((IPEndPoint)sender.Client.LocalEndPoint!).Port);
    Check((await sender.ReceiveAsync()).Buffer[0] == 9, "UDP TX");
    Check(session.Statistics.RxOperations == 2, "Empty datagram counted");
}
static async Task Cancellation()
{
    using var listener = Listen();
    await using var session = new TcpClientSession(new()
    {
        Port = Port(listener)
    });
    await Throws<OperationCanceledException>(() => session.ConnectAsync(new CancellationToken(true)));
    await session.ConnectAsync();
    using var peer = await listener.AcceptTcpClientAsync();
    await Throws<OperationCanceledException>(() => session.WriteAsync(new byte[] { 1 }, null, new CancellationToken(true)));
    await Task.WhenAll(session.DisconnectAsync(), session.DisconnectAsync());
    Check(session.State == ConnectionState.Disconnected, "Concurrent disconnect");
    var port = Port(listener);
    listener.Stop();
    await using var failed = new TcpClientSession(new()
    {
        Port = port
    });
    await Throws<SocketException>(() => failed.ConnectAsync());
    Check(failed.State == ConnectionState.Faulted, "Failure state");
    await failed.DisconnectAsync();
}
static async Task Modbus()
{
    for (var mode = 0; mode < 4; mode++)
    {
        using var listener = Listen();
        var currentMode = mode;
        var responder = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync();
            var stream = peer.GetStream();
            var request = new byte[12];
            await stream.ReadExactlyAsync(request);
            Check(request[7] == 3 && request[11] == 2, "Request PDU");
            var response = new byte[] { request[0], request[1], 0, 0, 0, 7, 1, 3, 4, 0x12, 0x34, 0xAB, 0xCD };
            if (currentMode == 1)
                response[1]++;
            if (currentMode == 2)
                response = [request[0], request[1], 0, 0, 0, 3, 1, 0x83, 2];
            if (currentMode == 3)
                response[8] = 2;
            foreach (var value in response)
            {
                await stream.WriteAsync(new byte[] { value });
                await Task.Delay(1);
            }
        });
        var operation = new ModbusTcpClient().ReadHoldingRegistersAsync("127.0.0.1", Port(listener), 1, 0, 2);
        if (mode == 0)
            Check((await operation).SequenceEqual(new ushort[] { 0x1234, 0xABCD }), "Fragmented MBAP / PDU");
        else
            await Throws<IOException>(async () => await operation);
        try
        {
            await responder;
        }
        catch (IOException) when (mode != 0)
        {

        }

    }
    await Throws<ArgumentOutOfRangeException>(() => new ModbusTcpClient().ReadHoldingRegistersAsync("localhost", 502, 1, 65535, 2));
}
static async Task ConcurrentWrites()
{
    using var listener = Listen();
    await using var session = new TcpClientSession(new()
    {
        Port = Port(listener)
    });
    await session.ConnectAsync();
    using var peer = await listener.AcceptTcpClientAsync();
    const int size = 4096;
    var buffer = new byte[32 * size];
    var reading = peer.GetStream().ReadExactlyAsync(buffer).AsTask();
    await Task.WhenAll(Enumerable.Range(0, 32).Select(i => session.WriteAsync(Enumerable.Repeat((byte)i, size).ToArray(), null)));
    await reading;
    var seen = new HashSet<byte>();
    for (var offset = 0; offset < buffer.Length; offset += size)
    {
        var marker = buffer[offset];
        Check(buffer.AsSpan(offset, size).ToArray().All(b => b == marker), "Writes must not interleave bytes");
        Check(seen.Add(marker), "Each write appears once");
    }
    Check(session.Statistics.TxOperations == 32, "TX operation count");
    await session.DisposeAsync();
    await Throws<ObjectDisposedException>(() => session.ConnectAsync());
}
static async Task BlockedWrite()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Server.ReceiveBufferSize = 1024;
    listener.Start();
    await using var session = new TcpClientSession(new()
    {
        Port = Port(listener)
    });
    await session.ConnectAsync();
    using var peer = await listener.AcceptTcpClientAsync();
    peer.ReceiveBufferSize = 4096;
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
    var payload = new byte[1024 * 1024];
    await Throws<OperationCanceledException>(async () =>
    {
        // Windows may buffer the first send. Keep writing until real backpressure
        // blocks the socket; the peer deliberately never consumes the stream.
        while (true)
            await session.WriteAsync(payload, null, cancellation.Token);
    });
    await Eventually(() => session.State is ConnectionState.Faulted or ConnectionState.Disconnected);
    await session.DisconnectAsync();
    await session.ConnectAsync();
    using var nextPeer = await listener.AcceptTcpClientAsync();
    await session.WriteAsync(new byte[] { 0x42 }, null);
    var received = new byte[1];
    await nextPeer.GetStream().ReadExactlyAsync(received);
    Check(received[0] == 0x42, "New session works after cancelled partial write");
}
static async Task Profiles()
{
    var path = Path.Combine(Path.GetTempPath(), $"ics-test-{Guid.NewGuid():N}.json");
    try
    {
        var store = new ProfileStore(path);
        var profile = new ConnectionProfile { Name = "设备 A", Kind = TransportKind.Udp };
        await store.SaveAsync([profile]);
        var loaded = await store.LoadAsync();
        Check(loaded.Count == 1 && loaded[0] == profile, "Profile round-trip");
        await store.SaveAsync([profile with { Port = 1234 }]);
        Check((await store.LoadAsync())[0].Port == 1234 && !File.Exists(path + ".tmp"), "Atomic replacement");
        await File.WriteAllTextAsync(path, "broken");
        await Throws<System.Text.Json.JsonException>(async () => await store.LoadAsync());
    }
    finally
    {
        File.Delete(path);
    }

}
static Task ChecksumVectors()
{
    var data = System.Text.Encoding.ASCII.GetBytes("123456789");
    Check(Checksums.ModbusCrc16(data) == 0x4B37, "CRC-16 / Modbus standard vector");
    Check(Checksums.CcittFalse(data) == 0x29B1, "CRC-16 / CCITT-FALSE standard vector");
    Check(Checksums.Crc32(data) == 0xCBF43926, "CRC-32 / ISO-HDLC standard vector");
    Check(Checksums.Sum8(data) == 0xDD && Checksums.Lrc(data) == 0x23 && Checksums.Xor8(data) == 0x31, "SUM / LRC / XOR vectors");
    var viewModel = new ChecksumViewModel();
    Check(viewModel.Results[0].WireBytes == "C4 0B" && viewModel.FramedHex == "01 03 00 00 00 02 C4 0B", "Modbus low byte first");
    viewModel.Input = "1";
    Check(viewModel.Results.Count == 0, "Invalid HEX clears old result");
    return Task.CompletedTask;
}
static async Task ModbusMasterSlave()
{
    using var reservation = Listen();
    var port = Port(reservation);
    reservation.Stop();
    var bank = new ModbusRegisterBank();
    bank.Set(ModbusTable.HoldingRegisters, 0, [0x1234, 0xABCD]);
    bank.Set(ModbusTable.InputRegisters, 0, [100, 200]);
    bank.Set(ModbusTable.Coils, 0, [1, 0, 1, 1, 0, 0, 1, 0, 1]);
    bank.Set(ModbusTable.DiscreteInputs, 0, [0, 1, 1]);
    await using var server = new ModbusTcpServer(bank);
    await server.StartAsync("127.0.0.1", port, 1);
    var master = new ModbusMasterClient();
    var masterTraffic = new ConcurrentQueue<ModbusTrafficRecord>();
    var slaveTraffic = new ConcurrentQueue<ModbusTrafficRecord>();
    master.Traffic += masterTraffic.Enqueue;
    server.Traffic += slaveTraffic.Enqueue;
    foreach (var table in Enum.GetValues<ModbusTable>())
    {
        var count = table == ModbusTable.Coils ? (ushort)9 : (ushort)2;
        Check((await master.ReadAsync("127.0.0.1", port, 1, table, 0, count)).SequenceEqual(bank.Read(table, 0, count)), $"FC {(int)table:D2} values");
    }
    await master.WriteSingleAsync("127.0.0.1", port, 1, ModbusTable.HoldingRegisters, 1, 555);
    await master.WriteSingleAsync("127.0.0.1", port, 1, ModbusTable.Coils, 1, 1);
    Check(bank.Read(ModbusTable.HoldingRegisters, 1, 1)[0] == 555 && bank.Read(ModbusTable.Coils, 1, 1)[0] == 1, "FC05 / FC06 writes");
    await master.WriteMultipleAsync("127.0.0.1", port, 1, ModbusTable.HoldingRegisters, 10, [10, 20, 65535]);
    await master.WriteMultipleAsync("127.0.0.1", port, 1, ModbusTable.Coils, 10, [1, 0, 1, 0, 1, 1, 0, 0, 1]);
    Check(bank.Read(ModbusTable.HoldingRegisters, 10, 3).SequenceEqual(new ushort[] { 10, 20, 65535 }), "FC16 atomic values");
    Check(bank.Read(ModbusTable.Coils, 10, 9).SequenceEqual(new ushort[] { 1, 0, 1, 0, 1, 1, 0, 0, 1 }), "FC15 bit packing");
    await Throws<IOException>(() => master.ReadAsync("127.0.0.1", port, 2, ModbusTable.HoldingRegisters, 0, 1));
    Check(masterTraffic.Any(r => r.Direction == TrafficDirection.Rx && r.Function == 0x83 && r.Hex.EndsWith("83 0B")), "Master logs exception response before throwing");
    await Throws<ArgumentException>(() => master.WriteSingleAsync("127.0.0.1", port, 1, ModbusTable.InputRegisters, 0, 1));
    using var raw = new TcpClient();
    await raw.ConnectAsync(IPAddress.Loopback, port);
    foreach (var request in new byte[][] { [0, 1, 0, 0, 0, 6, 1, 3, 255, 255, 0, 2], [0, 2, 0, 0, 0, 2, 1, 99] })
    {
        foreach (var b in request)
            await raw.GetStream().WriteAsync(new byte[] { b });
        var response = new byte[9];
        await raw.GetStream().ReadExactlyAsync(response);
        Check(response[0] == request[0] && response[1] == request[1] && response[7] == (request[7] | 0x80), "Server transaction/exception function");
        Check(response[8] == (request[7] == 3 ? 2 : 1), "Server exception code");
    }
    using (var incomplete = new TcpClient())
    {
        await incomplete.ConnectAsync(IPAddress.Loopback, port);
        await incomplete.GetStream().WriteAsync(new byte[] { 0, 9, 0 });
    }
    await Eventually(() => slaveTraffic.Any(r => !r.IsComplete && r.Hex == "00 09 00"));
    await server.StopAsync();
    Check(!server.IsRunning, "Slave stopped");
    Check(masterTraffic.Count >= 18 && masterTraffic.All(r => r.Role == ModbusRole.Master && r.IsComplete && r.Endpoint.Length > 0), "Master emits complete TX/RX with role and peer");
    foreach (var record in masterTraffic)
    {
        var counterpart = record.Direction == TrafficDirection.Tx ? TrafficDirection.Rx : TrafficDirection.Tx;
        Check(slaveTraffic.Any(r => r.Role == ModbusRole.Slave && r.Direction == counterpart && r.Hex == record.Hex), "Master/slave logs preserve identical complete MBAP packets");
    }
    await server.StartAsync("127.0.0.1", port, 1);
    await server.StopAsync();
}
static IEnumerable<T> FindVisuals<T>(DependencyObject root) where T : DependencyObject
{
    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
    {
        var child = VisualTreeHelper.GetChild(root, i);
        if (child is T found)
            yield return found;
        foreach (var descendant in FindVisuals<T>(child))
            yield return descendant;
    }
}
static Task WpfWorkspace()
{
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.UnhandledException += (_, args) =>
{
    completion.TrySetException(args.Exception);
    args.Handled = true;
}
;
        dispatcher.InvokeAsync(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"ics-wpf-{Guid.NewGuid():N}.json");
            WorkspaceViewModel? workspace = null;
            try
            {
                var theme = new ResourceDictionary { Source = new Uri("/CommunicationStudio;component/Themes/Dark.xaml", UriKind.Relative) };
                Application.Current.Resources.MergedDictionaries.Add(theme);
                Communication.App.Presentation.Appearance.Current.Apply("Dark", "zh-CN", false);
                var contextMenu = new ContextMenu { Style = (Style)theme[typeof(ContextMenu)] };
                contextMenu.Items.Add(new MenuItem { Header = "编辑分区…" });
                contextMenu.ApplyTemplate();
                var menuSurface = (Border)contextMenu.Template.FindName("MenuSurface", contextMenu);
                Check(menuSurface.Background == theme["Raised"], "Context menu uses dark surface without system gutter");
                var editablePort = new ComboBox { IsEditable = true, Text = "COM123", Style = (Style)theme[typeof(ComboBox)] };
                editablePort.ApplyTemplate();
                var portEditor = (TextBox)editablePort.Template.FindName("PART_EditableTextBox", editablePort);
                Check(portEditor.Visibility == Visibility.Visible && portEditor.Text == "COM123", "Serial combo exposes editable port name");
                portEditor.Text = "COM456";
                Check(editablePort.Text == "COM456", "Manual serial name updates combo text");
                foreach (var orientation in new[] { Orientation.Vertical, Orientation.Horizontal })
                {
                    var bar = new ScrollBar { Style = (Style)theme[typeof(ScrollBar)], Orientation = orientation, Maximum = 100, ViewportSize = 20, Value = 30 };
                    bar.ApplyTemplate();
                    var track = (Track)bar.Template.FindName("PART_Track", bar);
                    Check(track.Orientation == orientation && track.IsDirectionReversed == (orientation == Orientation.Vertical), "Scroll direction matches orientation");
                    track.Value = 50;
                    Check(bar.Value == 50, "Dragging track updates scrollbar value");
                }
                using var listener = Listen();
                await new ProfileStore(path).SaveAsync([new ConnectionProfile { Name = "WPF regression", Port = Port(listener) }]);
                workspace = new WorkspaceViewModel(path);
                await workspace.InitializeAsync();
                var connectionView = new Communication.App.Views.ConnectionsView { DataContext = workspace };
                connectionView.Measure(new Size(1260, 680));
                connectionView.Arrange(new Rect(0, 0, 1260, 680));
                var checksumView = new Communication.App.Views.ChecksumView { DataContext = new ChecksumViewModel() };
                checksumView.Measure(new Size(1260, 440));
                checksumView.Arrange(new Rect(0, 0, 1260, 440));
                checksumView.UpdateLayout();
                var checksumGrid = FindVisuals<DataGrid>(checksumView).Single();
                var checksumScroll = (ScrollViewer)checksumGrid.Template.FindName("DG_ScrollViewer", checksumGrid);
                var verticalBar = (ScrollBar)checksumScroll.Template.FindName("PART_VerticalScrollBar", checksumScroll);
                var headers = (DataGridColumnHeadersPresenter)checksumScroll.Template.FindName("PART_ColumnHeadersPresenter", checksumScroll);
                Check(verticalBar.TranslatePoint(new Point(), checksumScroll).Y >= headers.ActualHeight, "Grid scrollbar starts below header");
                checksumScroll.ScrollToBottom();
                await dispatcher.InvokeAsync(() =>
{

}
, DispatcherPriority.ContextIdle);
                checksumView.UpdateLayout();
                Check(checksumScroll.VerticalOffset > 0 && verticalBar.Value == checksumScroll.VerticalOffset, "Grid scroll updates themed scrollbar");
                var gridTrack = (Track)verticalBar.Template.FindName("PART_Track", verticalBar);
                gridTrack.Thumb.RaiseEvent(new DragDeltaEventArgs(0, -1000) { RoutedEvent = Thumb.DragDeltaEvent });
                await dispatcher.InvokeAsync(() =>
{

}
, DispatcherPriority.ContextIdle);
                checksumView.UpdateLayout();
                Check(checksumScroll.VerticalOffset == 0, "Thumb drag scrolls grid to start");
                checksumView.Measure(new Size(1260, 1000));
                checksumView.Arrange(new Rect(0, 0, 1260, 1000));
                checksumView.UpdateLayout();
                Check(verticalBar.Visibility == Visibility.Collapsed, "Grid scrollbar disappears when rows fit");
                var wideGrid = new DataGrid { Style = (Style)theme[typeof(DataGrid)], ItemsSource = new[] { new RegisterRow(0, 42) } };
                wideGrid.Columns.Add(new DataGridTextColumn { Header = "地址", Width = 300, Binding = new System.Windows.Data.Binding("Address") });
                wideGrid.Columns.Add(new DataGridTextColumn { Header = "数值", Width = 300, Binding = new System.Windows.Data.Binding("Value") });
                wideGrid.Measure(new Size(320, 150));
                wideGrid.Arrange(new Rect(0, 0, 320, 150));
                wideGrid.UpdateLayout();
                await dispatcher.InvokeAsync(() =>
{

}
, DispatcherPriority.ContextIdle);
                wideGrid.UpdateLayout();
                var wideScroll = (ScrollViewer)wideGrid.Template.FindName("DG_ScrollViewer", wideGrid);
                var horizontalBar = (ScrollBar)wideScroll.Template.FindName("PART_HorizontalScrollBar", wideScroll);
                var wideHeaders = (DataGridColumnHeadersPresenter)wideScroll.Template.FindName("PART_ColumnHeadersPresenter", wideScroll);
                var firstHeader = (DataGridColumnHeader)wideHeaders.ItemContainerGenerator.ContainerFromIndex(0);
                var headerStart = firstHeader.TranslatePoint(new Point(), wideGrid).X;
                Check(horizontalBar.Visibility == Visibility.Visible, "Wide table shows horizontal scrollbar");
                wideScroll.ScrollToHorizontalOffset(wideScroll.ScrollableWidth);
                await dispatcher.InvokeAsync(() =>
{

}
, DispatcherPriority.ContextIdle);
                wideGrid.UpdateLayout();
                Check(wideScroll.HorizontalOffset > 0 && firstHeader.TranslatePoint(new Point(), wideGrid).X < headerStart, "Horizontal scrolling keeps headers synchronized");
                var horizontalTrack = (Track)horizontalBar.Template.FindName("PART_Track", horizontalBar);
                horizontalTrack.Thumb.RaiseEvent(new DragDeltaEventArgs(-1000, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                await dispatcher.InvokeAsync(() =>
{

}
, DispatcherPriority.ContextIdle);
                wideGrid.UpdateLayout();
                Check(wideScroll.HorizontalOffset == 0, "Horizontal thumb drag scrolls table to start");
                var grid = new DataGrid { ItemsSource = workspace.TrafficView };
                workspace.TrafficView.MoveCurrentToFirst();
                await workspace.ConnectCommand.ExecuteAsync();
                Check(workspace.Selected!.State == ConnectionState.Connected, "UI connected");
                using var peer = await listener.AcceptTcpClientAsync();
                workspace.SendHex = false;
                workspace.SendText = "Hello";
                await workspace.SendCommand.ExecuteAsync();
                var data = new byte[5];
                await peer.GetStream().ReadExactlyAsync(data);
                await peer.GetStream().WriteAsync(new byte[] { 0x41, 0x42 });
                await Eventually(() => workspace.Traffic.Count == 2);
                Check(grid.Items.Count == 2, "Bound DataGrid receives traffic without deferred-refresh exception");
                workspace.TrafficDirectionFilter.Options.Single(option => option.Value == "TX").IsSelected = false;
                Check(grid.Items.Count == 1, "Direction filter");
                workspace.HexView = false;
                Check(((TrafficRow)grid.Items[0]).Preview == "AB", "Text view");
                workspace.SelectedTraffic = (TrafficRow)grid.Items[0];
                workspace.TrafficView.MoveCurrentToFirst();
                workspace.TrafficDirectionFilter.SelectAll(true);
                // Each send publishes a TX record, independently of TCP receive coalescing.
                workspace.SendText = "x";
                for (var i = 0; i < 1050; i++)
                    await workspace.SendCommand.ExecuteAsync();
                await Eventually(() => workspace.Selected.Session!.Statistics.TxOperations == 1051);
                await Task.Delay(600);
                Check(workspace.Traffic.Count <= 1000 && !workspace.Logs.Any(l => l.Level == "ERROR"), "Bounded traffic window");
                var beforePause = workspace.Traffic.Count;
                workspace.Paused = true;
                await peer.GetStream().WriteAsync(new byte[] { 0x43 });
                await Task.Delay(200);
                Check(workspace.Traffic.Count == beforePause && workspace.Selected.Session!.Statistics.RxBytes == 3, "Pause keeps RX statistics");
                await workspace.ShutdownAsync();
                Check(workspace.Selected.Session!.State == ConnectionState.Disconnected, "Shutdown closes UI session");
                workspace = null;
                using var modbusReservation = Listen();
                var modbusPort = Port(modbusReservation);
                modbusReservation.Stop();
                var modbus = new ModbusViewModel(_ => ModbusRtuTests.MemoryPort.Pair().Item1) { SlaveMode = true, LocalPort = modbusPort.ToString(), Port = modbusPort.ToString(), Count = "3" };
                try
                {
                    var modbusView = new Communication.App.Views.ModbusView { DataContext = modbus };
                    modbusView.Measure(new Size(1260, 680));
                    modbusView.Arrange(new Rect(0, 0, 1260, 680));
                    var bitRow = new RegisterRow(0, 0, true);
                    var bitEditor = (ComboBox)((DataTemplate)modbusView.Resources["BitValueEditor"]).LoadContent();
                    bitEditor.DataContext = bitRow;
                    bitEditor.ApplyTemplate();
                    await dispatcher.InvokeAsync(() =>
{

}
, DispatcherPriority.ContextIdle);
                    Check(!bitEditor.IsEditable && bitEditor.Items.Cast<string>().SequenceEqual(new[] { "0", "1" }), "Bit editor only offers 0 and 1");
                    bitEditor.SelectedItem = "1";
                    Check(bitRow.EditValue == "1" && bitRow.ParseEdit(true) == 1, "Bit dropdown updates pending value");
                    Check(((SolidColorBrush)bitEditor.Background).Color == Colors.Transparent, "Bit editor follows row background");
                    bitRow.EditValue = "2";
                    await Throws<ArgumentException>(() => Task.Run(() => bitRow.ParseEdit(true)));
                    var registerRow = new RegisterRow(0, 0x1234) { EditValue = "0x1234" };
                    Check(!registerRow.IsDirty, "Equivalent HEX value is not a pending edit");
                    var numberEditor = (TextBox)((DataTemplate)modbusView.Resources["RegisterValueEditor"]).LoadContent();
                    numberEditor.DataContext = registerRow;
                    numberEditor.ApplyTemplate();
                    await dispatcher.InvokeAsync(() =>
{

}
, DispatcherPriority.ContextIdle);
                    Check(numberEditor.Style == theme[typeof(TextBox)] && ((SolidColorBrush)numberEditor.Background).Color != Colors.White, "Register editor retains dark input style");
                    numberEditor.Text = "65535";
                    Check(registerRow.ParseEdit(false) == 65535, "Register editor accepts ushort upper bound");
                    numberEditor.Text = "65536";
                    numberEditor.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, numberEditor, null) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
                    Check(registerRow.HasEditError && registerRow.EditError.Contains("0–65535"), "Invalid register shows error on focus loss");
                    numberEditor.Text = "0xFFFF";
                    Check(!registerRow.HasEditError, "Correcting invalid value clears error immediately");
                    foreach (var invalid in new[] { "", "-1", "1.5", "abc", "0x10000" })
                    {
                        numberEditor.Text = invalid;
                        numberEditor.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, numberEditor, null) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
                        Check(registerRow.HasEditError, $"Rejects invalid register value {invalid}");
                    }
                    registerRow.RevertEdit();
                    Check(!registerRow.HasEditError, "Undo clears validation error");
                    modbus.LoadSlaveCommand.Execute(null);
                    Check(modbus.Rows.Count == 3, "Slave table loaded");
                    modbus.Rows[0].EditValue = "65536";
                    modbus.Rows[0].ValidateEdit();
                    Check(!modbus.ApplySlaveCommand.CanExecute(null) && modbus.EditValidationSummary.Contains("地址 0"), "Invalid row disables apply and supplies immediate feedback");
                    modbus.RevertEditsCommand.Execute(null);
                    Check(modbus.ApplySlaveCommand.CanExecute(null) && modbus.EditValidationSummary == "", "Undo restores apply availability");
                    modbus.Rows[0].EditValue = "0x1234";
                    modbus.ApplySlaveCommand.Execute(null);
                    await modbus.StartSlaveCommand.ExecuteAsync();
                    Check(modbus.IsSlaveRunning, "Slave view model started");
                    modbus.SlaveMode = false;
                    await modbus.ReadCommand.ExecuteAsync();
                    Check(modbus.Rows[0].Value == 0x1234, "Master reads local slave table");
                    await Eventually(() => new[] { ("主机", "TX"), ("主机", "RX"), ("从机", "TX"), ("从机", "RX") }.All(pair => modbus.Logs.Any(l => l.Role == pair.Item1 && l.Direction == pair.Item2 && l.Hex.Length > 0)));
                    Check(modbus.Logs.Where(l => l.Level == "TRAFFIC").All(l => l.Endpoint.Length > 0 && l.Message.Contains("TID") && l.Detail.Contains(l.Hex)), "UI logs include endpoint, transaction and copyable HEX");
                    modbus.Selected = modbus.Rows[1];
                    modbus.Selected.EditValue = "555";
                    await modbus.WriteSelectedCommand.ExecuteAsync();
                    modbus.SlaveMode = true;
                    modbus.LoadSlaveCommand.Execute(null);
                    Check(modbus.Rows[1].Value == 555, "Master write reflected in slave table");
                    modbus.SlaveMode = false;
                    modbus.PollInterval = "100";
                    modbus.PollCommand.Execute(null);
                    await Task.Delay(220);
                    modbus.PollCommand.Execute(null);
                    await Task.Delay(50);
                    Check(!modbus.IsPolling && !modbus.Logs.Any(l => l.Level == "ERROR"), "Polling starts and cancels cleanly");
                    var firstSection = modbus.SelectedSection!;
                    var pendingRead = modbus.ReadCommand.ExecuteAsync();
                    var secondSection = modbus.AddSection(new("线圈区", ModbusTable.Coils, 10, 4));
                    await pendingRead;
                    Check(secondSection.MasterRows.Count == 0 && firstSection.MasterRows.Count == 3, "In-flight read belongs to original tab");
                    modbus.SlaveMode = true;
                    Check(modbus.Rows.Count == 4 && modbus.Rows[0].Address == 10 && modbus.Rows.All(r => r.IsBit), "New slave bit section uses its range and editor type");
                    modbus.Rows[0].EditValue = "1";
                    Check(modbus.RevertEditsCommand.CanExecute(null), "Undo enabled immediately after bit edit");
                    modbus.RevertEditsCommand.Execute(null);
                    Check(modbus.Rows[0].EditValue == "0" && !modbus.RevertEditsCommand.CanExecute(null), "Undo restores current bit value");
                    modbus.Rows[0].EditValue = "1";
                    modbus.SelectedSection = firstSection;
                    modbus.SelectedSection = secondSection;
                    Check(modbus.Rows[0].EditValue == "1", "Switching sections preserves unsaved values");
                    modbus.EditSection(secondSection, secondSection.Configuration with
                    {
                        Name = "输出线圈"
                    });
                    Check(modbus.Rows[0].EditValue == "1", "Rename preserves edits");
                    modbus.EditSection(secondSection, secondSection.Configuration with
                    {
                        Start = 20
                    });
                    Check(modbus.Rows[0].Address == 20 && modbus.Rows[0].EditValue == "0", "Range edit rebuilds table");
                    modbus.SlaveMode = false;
                    modbus.SelectedSection = firstSection;
                    modbus.Rows[0].EditValue = "invalid";
                    modbus.RevertEditsCommand.Execute(null);
                    Check(modbus.Rows[0].EditValue == modbus.Rows[0].Value.ToString(), "Undo restores invalid numeric edit");
                    pendingRead = modbus.ReadCommand.ExecuteAsync();
                    modbus.EditSection(firstSection, firstSection.Configuration with
                    {
                        Start = 30
                    });
                    await pendingRead;
                    Check(firstSection.MasterRows.Count == 0, "Response after range change is discarded");
                    await Throws<ArgumentException>(() => Task.Run(() => new RegisterSectionConfiguration("越界", ModbusTable.HoldingRegisters, 65535, 2).Validate()));
                    modbus.RemoveSection(firstSection);
                    modbus.RemoveSection(secondSection);
                    Check(modbus.SelectedSection is null && modbus.Rows.Count == 0 && !modbus.ReadCommand.CanExecute(null), "Closing last tab leaves safe empty state");
                    await modbus.StopSlaveCommand.ExecuteAsync();
                    modbus.Transport = ModbusTransport.Rtu;
                    Check(modbus.IsRtu && !modbus.IsTcp && modbus.ConnectRtuCommand.CanExecute(null) && !modbus.ReadCommand.CanExecute(null), "RTU master requires explicit serial connection");
                    modbusView.Measure(new Size(1200, 750));
                    modbusView.Arrange(new Rect(0, 0, 1200, 750));
                    modbusView.UpdateLayout();
                    await dispatcher.InvokeAsync(() =>
{

}
, DispatcherPriority.ContextIdle);
                    modbusView.UpdateLayout();
                    var rtuSelectors = FindVisuals<ComboBox>(modbusView).Where(c => c.IsEditable).ToArray();
                    Check(rtuSelectors.Any(c => ReferenceEquals(c.ItemsSource, modbus.SerialPorts)), "RTU serial name is editable and discoverable");
                    await modbus.ConnectRtuCommand.ExecuteAsync();
                    Check(modbus.IsMasterConnected && !modbus.CanChangeProtocol && modbus.SlaveModeCommand.CanExecute(null), "Open RTU master locks protocol but allows role navigation");
                    modbus.SlaveMode = true;
                    Check(modbus.SlaveMode && modbus.IsMasterConnected && !modbus.StartSlaveCommand.CanExecute(null), "Role navigation preserves master port and prevents conflicting slave open");
                    Check(modbus.CanConfigureSerial && modbus.SerialWarning == "已占有", "Inactive slave serial remains editable with occupied-port warning");
                    modbus.SerialPortName = "COM2";
                    modbus.BaudRate = "19200";
                    Check(!modbus.SerialPortConflict && modbus.SerialWarning == "" && modbus.StartSlaveCommand.CanExecute(null), "Slave can select a different serial port while master remains connected");
                    await modbus.StartSlaveCommand.ExecuteAsync();
                    Check(modbus.IsSlaveRunning && modbus.IsMasterConnected, "RTU master and slave can run on distinct ports");
                    modbus.MasterModeCommand.Execute(null);
                    Check(modbus.MasterMode && modbus.IsMasterConnected, "Returning to RTU master preserves connection");
                    Check(modbus.SerialPortName == "COM1" && modbus.BaudRate == "9600", "Master serial settings remain independent");
                    await modbus.DisconnectRtuCommand.ExecuteAsync();
                    Check(modbus.CanConfigureSerial, "Master settings editable while slave runs on another port");
                    modbus.SerialPortName = "com2";
                    Check(modbus.SerialPortConflict && modbus.SerialWarning == "已占有" && !modbus.ConnectRtuCommand.CanExecute(null), "Slave port conflict uses case-insensitive comparison");
                    modbus.SerialPortName = "COM1";
                    Check(modbus.ConnectRtuCommand.CanExecute(null), "Slave on another port does not block master open");
                    modbus.SlaveMode = true;
                    Check(modbus.SerialPortName == "COM2" && modbus.BaudRate == "19200", "Slave settings preserved across navigation");
                    await modbus.StopSlaveCommand.ExecuteAsync();
                    Check(modbus.StartSlaveCommand.CanExecute(null) && !modbus.ConnectRtuCommand.CanExecute(null), "RTU slave has separate serial lifecycle");
                    await modbus.StartSlaveCommand.ExecuteAsync();
                    modbus.MasterModeCommand.Execute(null);
                    Check(modbus.MasterMode && modbus.IsSlaveRunning && !modbus.CanChangeProtocol && modbus.ConnectRtuCommand.CanExecute(null), "Running RTU slave permits master connection on a different port");
                    modbus.SlaveModeCommand.Execute(null);
                    await modbus.StopSlaveCommand.ExecuteAsync();
                    modbusView.UpdateLayout();
                    var rawRtu = ModbusRtuCodec.Encode(1, new byte[] { 3, 0, 0, 0, 2 });
                    var rtuLog = ModbusLogRow.FromTraffic(new(ModbusRole.Master, TrafficDirection.Tx, "COM1", rawRtu, transport: ModbusTransport.Rtu));
                    Check(rtuLog.Type == "RTU 传输" && rtuLog.Hex == "01 03 00 00 00 02 C4 0B" && rtuLog.Message.Contains("RTU") && !rtuLog.Message.Contains("TID"), "RTU UI log preserves CRC without TCP transaction id");
                }
                finally
                {
                    await modbus.ShutdownAsync();
                }

                var failedSerial = new ModbusViewModel(_ => throw new UnauthorizedAccessException("端口被其他程序使用。")) { Transport = ModbusTransport.Rtu };
                try
                {
                    var failures = new List<string>();
                    failedSerial.SerialOpenFailed += failures.Add;
                    await failedSerial.ConnectRtuCommand.ExecuteAsync();
                    Check(failures.Count == 1 && failures[0].Contains("端口被其他程序使用") && failedSerial.SerialWarning == "已占有", "Master serial open failure requests reason dialog and concise inline warning");
                    failedSerial.SlaveMode = true;
                    Check(failures.Count == 1 && failedSerial.SerialWarning == "", "Role navigation does not request error dialogs");
                    await failedSerial.StartSlaveCommand.ExecuteAsync();
                    Check(failures.Count == 2 && failedSerial.SerialWarning == "已占有", "Slave serial open failure requests reason dialog");
                }
                finally
                {
                    await failedSerial.ShutdownAsync();
                }

                var logModel = new ModbusViewModel();
                Window? logWindow = null;
                try
                {
                    var midnight = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.FromHours(8));
                    var older = new ModbusLogRow(midnight.AddMilliseconds(-1), "INFO", "主机", "—", "", "旧日志", Type: "连接事件");
                    var rtuMaster = new ModbusLogRow(midnight, "TRAFFIC", "主机", "RX", "COM1", "RTU 接收", "01 03", "RTU 传输");
                    var rtuSlave = new ModbusLogRow(midnight.AddSeconds(1), "TRAFFIC", "从机", "TX", "COM2", "RTU 发送", "01 03", "RTU 传输");
                    var tcp = new ModbusLogRow(midnight.AddSeconds(2), "TRAFFIC", "主机", "TX", "127.0.0.1:502", "TCP 发送", Type: "TCP 传输");
                    foreach (var row in new[] { older, rtuSlave, tcp, rtuMaster })
                        logModel.Logs.Add(row);
                    Check(logModel.LogsView.Cast<ModbusLogRow>().First() == tcp && !logModel.TimeAscending, "Modbus log defaults to newest first");
                    logModel.ToggleLogTimeSort();
                    Check(logModel.LogsView.Cast<ModbusLogRow>().First() == older && logModel.TimeAscending, "Time sorting uses full date across midnight");
                    logModel.TypeFilter.Options.Single(option => option.Value == "TCP 传输").IsSelected = false;
                    logModel.TypeFilter.Options.Single(option => option.Value == "连接事件").IsSelected = false;
                    Check(logModel.LogsView.Cast<ModbusLogRow>().SequenceEqual(new[] { rtuMaster, rtuSlave }), "Event type filter distinguishes transport logs from connection events");
                    logModel.RoleFilter.Options.Single(option => option.Value == "从机").IsSelected = false;
                    Check(logModel.LogsView.Cast<ModbusLogRow>().Single() == rtuMaster, "Column filters combine with AND");
                    logModel.EndpointFilter.SelectAll(false);
                    Check(!logModel.LogsView.Cast<ModbusLogRow>().Any(), "Clear selection hides all matching logs");
                    logModel.EndpointFilter.Options.Single(option => option.Value == "COM1").IsSelected = true;
                    Check(logModel.LogsView.Cast<ModbusLogRow>().Single() == rtuMaster, "Endpoint can be selected independently");
                    var incoming = rtuMaster with
                    {
                        Timestamp = midnight.AddSeconds(3),
                        Message = "新日志"
                    };
                    logModel.Logs.Add(incoming);
                    Check(logModel.LogsView.Cast<ModbusLogRow>().SequenceEqual(new[] { rtuMaster, incoming }), "Incoming logs preserve filter and sort");
                    logModel.ToggleLogTimeSort();
                    Check(logModel.LogsView.Cast<ModbusLogRow>().First() == incoming, "Descending sort applies with active filters");
                    foreach (var filter in new[] { logModel.LevelFilter, logModel.TypeFilter, logModel.RoleFilter, logModel.EndpointFilter })
                        filter.SelectAll(true);
                    Check(logModel.LogsView.Cast<ModbusLogRow>().Count() == 5, "Selecting all restores retained records");
                    logModel.LevelFilter.Options.Single(option => option.Value == "TRAFFIC").IsSelected = false;
                    logModel.Logs.Add(older with
                    {
                        Timestamp = midnight.AddSeconds(4),
                        Level = "ERROR",
                        Message = "新增级别"
                    });
                    Check(logModel.LogsView.Cast<ModbusLogRow>().Single() == older && !logModel.LevelFilter.Options.Single(option => option.Value == "ERROR").IsSelected, "New levels do not bypass active multi-selection filter");
                    logModel.LevelFilter.SelectAll(true);
                    var logView = new Communication.App.Views.ModbusView { DataContext = logModel };
                    logWindow = new Window { Content = logView, Width = 1260, Height = 750, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = -16000 };
                    logWindow.Show();
                    await dispatcher.InvokeAsync(() =>
{

}
, DispatcherPriority.ContextIdle);
                    logView.Measure(new Size(1260, 750));
                    logView.Arrange(new Rect(0, 0, 1260, 750));
                    logView.UpdateLayout();
                    var logGrid = (DataGrid)logView.FindName("LogGrid");
                    Check(logGrid.Columns[2].Header.ToString() == "类型", "Event type column sits between level and role");
                    var logHeaders = FindVisuals<DataGridColumnHeader>(logGrid).Where(header => header.Column is not null).ToArray();
                    var timeHeader = logHeaders.Single(header => header.Column == logGrid.Columns[0]);
                    Check(FindVisuals<System.Windows.Shapes.Path>(timeHeader).Count() >= 2, "Time header displays paired sort icons");
                    foreach (var tag in new[] { "Level", "Type", "Role", "Endpoint" })
                    {
                        var header = logHeaders.Single(header => Equals(header.Tag, tag));
                        var popup = (Popup)logView.FindName("LogFilterPopup");
                        header.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
                        Check(!popup.IsOpen, "Right press does not open filter before release");
                        header.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent });
                        Check(!popup.IsOpen, "Filter opening is deferred until release processing completes");
                        await dispatcher.InvokeAsync(() =>
{

}
, DispatcherPriority.ContextIdle);
                        Check(popup.IsOpen && popup.DataContext is MultiSelectFilter && ReferenceEquals(popup.PlacementTarget, header) && popup.Placement == PlacementMode.Top, $"Right-click filter opens above matching header: {tag}, open={popup.IsOpen}, filter={popup.DataContext is MultiSelectFilter}, target={ReferenceEquals(popup.PlacementTarget, header)}");
                        await dispatcher.InvokeAsync(() =>
{

}
, DispatcherPriority.ContextIdle);
                        Check(popup.IsOpen, "Filter remains open after right release and dispatcher idle");
                        popup.IsOpen = false;
                    }
                    var packetRow = ModbusLogRow.FromTraffic(new(ModbusRole.Master, TrafficDirection.Rx, "COM1", ModbusRtuCodec.Encode(1, new byte[] { 3, 4, 0x12, 0x34, 0xAB, 0xCD }), transport: ModbusTransport.Rtu));
                    logModel.Logs.Add(packetRow);
                    logGrid.SelectedItem = packetRow;
                    await dispatcher.InvokeAsync(() =>
{

}
, DispatcherPriority.ContextIdle);
                    logView.UpdateLayout();
                    var packetScroller = (ScrollViewer)logView.FindName("PacketScrollViewer");
                    var semanticGrid = (DataGrid)logView.FindName("PacketSemanticGrid");
                    Check(logModel.HasSelectedPacket && semanticGrid.Items.Count == packetRow.Packet!.Fields.Count && semanticGrid.Columns.Select(column => column.Header.ToString()).SequenceEqual(new[] { "字段", "10进制", "2进制" }), "Selected packet displays the three-column field table");
                    Check(FindVisuals<TextBox>(packetScroller).Any(box => box.Text == packetRow.Hex) && FindVisuals<TextBox>(packetScroller).Any(box => box.Text == packetRow.Packet!.Binary), "Packet detail exposes selectable raw HEX and field-separated binary");
                    packetScroller.ScrollToBottom();
                    await dispatcher.InvokeAsync(() =>
{

}
, DispatcherPriority.ContextIdle);
                    logView.UpdateLayout();
                    Check(packetScroller.VerticalOffset > 0, "Packet panel scrolls independently of log list");
                    packetScroller.ScrollToTop();
                    await dispatcher.InvokeAsync(() =>
{

}
, DispatcherPriority.ContextIdle);
                    logView.UpdateLayout();
                    var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                    semanticGrid.RaiseEvent(wheel);
                    await dispatcher.InvokeAsync(() =>
{

}
, DispatcherPriority.ContextIdle);
                    logView.UpdateLayout();
                    Check(wheel.Handled && packetScroller.VerticalOffset > 0, "Mouse wheel over nested field table scrolls packet panel");
                    var offsetBeforeUp = packetScroller.VerticalOffset;
                    semanticGrid.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                    await dispatcher.InvokeAsync(() =>
{

}
, DispatcherPriority.ContextIdle);
                    logView.UpdateLayout();
                    Check(packetScroller.VerticalOffset < offsetBeforeUp, "Mouse wheel over field table also scrolls upward");
                    logGrid.SelectedItem = older;
                    Check(!logModel.HasSelectedPacket, "Non-traffic event does not retain previous packet details");
                    logGrid.SelectedItem = packetRow;
                    logModel.ClearLogsCommand.Execute(null);
                    Check(logModel.Logs.Count == 0 && !logModel.LogsView.Cast<ModbusLogRow>().Any() && logModel.TypeFilter.Options.Count == 0, "Clear logs removes retained rows and filter choices");
                    Check(logModel.SelectedPacket is null, "Clear logs clears packet selection");
                    await Task.Delay(10);
                    logModel.SlaveMode = true;
                    Check(logModel.LogsView.Cast<ModbusLogRow>().Single().Type == "数据表事件", "New events remain visible after clearing logs");
                }
                finally
                {
                    logWindow?.Close();
                    await logModel.ShutdownAsync();
                }

                await ModbusOwnershipTests.Run();
                await TrafficMonitorTests.Run();
                Console.WriteLine("UI check: generic sender routing");
                await SendRoutingTests.Run();
                WindowWorkAreaTests.Run();
                await ToolWindowTests.Run();
                Console.WriteLine("UI check: appearance and encoding");
                await AppearanceTests.Run();
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }

            finally
            {
                if (workspace is not null)
                    await workspace.ShutdownAsync();
                File.Delete(path);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run();
    })
    {
        IsBackground = true,
        Name = "WPF regression dispatcher"
    };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    return completion.Task;
}
