using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Communication.App;
using Communication.App.Presentation;
using Communication.App.ViewModels;
using Communication.App.Views;
using Communication.Core;

internal static class AppearanceTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    public static async Task Run()
    {
        var timing = System.Diagnostics.Stopwatch.StartNew();
        void Stage(string name) => Console.WriteLine($"Appearance {timing.Elapsed.TotalSeconds:F1}s: {name}");
        Stage("start");
        var settingsPath = Path.Combine(Path.GetTempPath(), $"ics-appearance-{Guid.NewGuid():N}.json");
        var profilePath = Path.Combine(Path.GetTempPath(), $"ics-appearance-workspace-{Guid.NewGuid():N}.json");
        var appearance = Appearance.Current;
        var mainPath = Path.Combine(Path.GetTempPath(), $"ics-appearance-main-{Guid.NewGuid():N}.json");
        var shell = new ShellViewModel(new WorkspaceViewModel(mainPath));
        var main = new MainWindow(shell);
        var modbus = new ModbusViewModel();
        var workspace = new WorkspaceViewModel(profilePath);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Window? testWindow = null;
        try
        {
            Appearance.WritePreferences(settingsPath, new("Light", "en-US"));
            Check(Appearance.ReadPreferences(settingsPath) == new AppearancePreferences("Light", "en-US"), "Appearance preferences round-trip");
            File.WriteAllText(settingsPath, "broken JSON");
            Check(Appearance.ReadPreferences(settingsPath) == new AppearancePreferences(), "Invalid appearance preferences use safe defaults");
            File.WriteAllText(settingsPath, "{\"Theme\":\"invalid\",\"Language\":\"invalid\"}");
            Check(Appearance.ReadPreferences(settingsPath) == new AppearancePreferences(), "Unknown preference values use defaults");
            await new ProfileStore(mainPath).SaveAsync([new ConnectionProfile { Name = "Retained configuration" }]);

            await new ProfileStore(profilePath).SaveAsync([new ConnectionProfile { Name = "保持寄存器 test", Port = ((IPEndPoint)listener.LocalEndpoint).Port }]);
            await workspace.InitializeAsync();
            var accepting = listener.AcceptTcpClientAsync();
            await workspace.ConnectCommand.ExecuteAsync();
            using var peer = await accepting.WaitAsync(TimeSpan.FromSeconds(2));
            Stage("connected");
            var session = workspace.Selected!.Session;
            Check(workspace.SendHex && workspace.SendSize == "14 bytes", "Send panel defaults to valid HEX input");
            workspace.SendText = "线圈 user payload";
            var connectionView = new ConnectionsView { DataContext = workspace };
            var checksum = new ChecksumViewModel();
            var checksumView = new ChecksumView { DataContext = checksum };
            var modbusView = new ModbusView { DataContext = modbus };
            var dialog = new RegisterSectionDialog();
            var host = new Grid();
            host.Children.Add(connectionView);
            host.Children.Add(checksumView);
            host.Children.Add(modbusView);
            testWindow = new Window { Content = host, Width = 1450, Height = 850, ShowInTaskbar = false };
            testWindow.Show();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Stage("rendered");
            var revision = modbus.SelectedSection!.Revision;
            var rows = modbus.Rows;
            var selectedSection = modbus.SelectedSection;
            var log = ModbusLogRow.FromTraffic(new(ModbusRole.Master, TrafficDirection.Rx, "COM1", ModbusRtuCodec.Encode(1, new byte[] { 3, 2, 0x12, 0x34 }), transport: ModbusTransport.Rtu));
            modbus.Logs.Add(log);
            modbus.SelectedLog = log;
            modbus.Logs.Add(new(DateTimeOffset.Now, "INFO", "主机", "—", "", "就绪", Type: "操作事件"));
            modbus.TypeFilter.Options.Single(option => option.Value == "操作事件").IsSelected = false;
            foreach (var view in new FrameworkElement[] { connectionView, checksumView, modbusView })
                Layout(view);

            var grid = Visuals<DataGrid>(checksumView).Single();
            var headerBefore = grid.Columns[0].Header;
            var scroll = (ScrollViewer)grid.Template.FindName("DG_ScrollViewer", grid);
            var bar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
            bar.ApplyTemplate();
            var track = (Track)bar.Template.FindName("PART_Track", bar);
            var thumb = track.Thumb;
            var oldColor = ((SolidColorBrush)thumb.Background).Color;

            appearance.Apply("Light", "en-US", false);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Stage("light English");
            foreach (var view in new FrameworkElement[] { connectionView, checksumView, modbusView })
                Layout(view);
            Check(grid.Columns[0].Header.ToString() == "Algorithm" && headerBefore!.ToString() == "算法", "Existing grid headers translate in place");
            Check(((SolidColorBrush)grid.Background).Color == Color.FromRgb(243, 245, 248), $"Existing grid switches to light background: grid={grid.Background}, app={Application.Current.FindResource("Background")}, style={grid.FindResource("Background")}");
            Check(((SolidColorBrush)thumb.Background).Color != oldColor && ((SolidColorBrush)thumb.Background).Color == Color.FromRgb(165, 178, 194), "Existing scrollbar thumb changes palette");
            Check(Visuals<TextBlock>(checksumView).Any(text => text.Text.Contains("Select an algorithm")), "Generated checksum status translates");
            Check(Visuals<TextBlock>(modbusView).Any(text => text.Text.Contains("Holding registers", StringComparison.OrdinalIgnoreCase)), "Register section descriptions translate");
            Check(Visuals<Button>(modbusView).Any(button => Equals(button.Content, "Master mode")), "Existing static button labels translate");
            Check(Visuals<TextBlock>(modbusView).Any(text => text.Text == "Transaction ID" || text.Text == "Slave address"), "Packet semantic fields translate");
            Check(((DataGrid)modbusView.FindName("LogGrid")).Columns[2].Header.ToString() == "Type", "Modbus log headers translate");
            Check(((TextBlock)dialog.FindName("DialogHeading")).Text == "New register tab", "Existing register dialog heading translates");
            Check(((SolidColorBrush)dialog.Background).Color == Color.FromRgb(243, 245, 248), "Existing dialog changes theme");
            Check(ReferenceEquals(session, workspace.Selected.Session) && session!.State == ConnectionState.Connected, "Appearance switching preserves active connection");
            Check(workspace.SendText == "线圈 user payload" && workspace.Selected.Name == "保持寄存器 test", "Editable user data is never translated");
            Check(ReferenceEquals(selectedSection, modbus.SelectedSection) && ReferenceEquals(rows, modbus.Rows) && selectedSection.Revision == revision, "Switching preserves register section and rows");
            Check(ReferenceEquals(log, modbus.SelectedLog) && modbus.TypeFilter.Matches(log.Type) && !modbus.TypeFilter.Matches("操作事件"), "Switching preserves packet selection and filter values");
            peer.GetStream().Write(new byte[] { 0x12, 0x34 });
            for (var i = 0; i < 40 && workspace.Selected.Statistics.RxBytes == 0; i++)
                await Task.Delay(10);
            Check(workspace.Selected.Statistics.RxBytes == 2, "Live reception continues after switching");
            Visuals<TabControl>(connectionView).Single().SelectedIndex = 1;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var formatMenu = (Menu)connectionView.FindName("SendFormatMenu");
            var formatRoot = (MenuItem)formatMenu.Items[0];
            var hexItem = (MenuItem)formatRoot.Items[0];
            var textItem = (MenuItem)formatRoot.Items[1];
            Check(Equals(formatRoot.Header, "HEX") && hexItem.IsChecked && Equals(textItem.Header, "Text"), "Send format menu translates and defaults to HEX");
            workspace.SendText = "41 42";
            Check(workspace.SendCommand.CanExecute(null), $"Sender remains selectable after UI rendering: {workspace.SendEndpoint?.Name ?? "null"}");
            await workspace.SendCommand.ExecuteAsync();
            var sent = new byte[2];
            await peer.GetStream().ReadExactlyAsync(sent).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Stage("HEX send");
            Check(sent.SequenceEqual(new byte[] { 0x41, 0x42 }), "HEX input sends exact bytes");
            var encodingCases = new (int Index, string Text, byte[] Bytes)[]
            {
                (0, "中", [0xE4, 0xB8, 0xAD]), (1, "A", [0x41]),
                (2, "中", [0x2D, 0x4E]), (3, "中", [0x4E, 0x2D]), (4, "中", [0xD6, 0xD0])
            };
            foreach (var encodingCase in encodingCases)
            {
                var item = (MenuItem)textItem.Items[encodingCase.Index];
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                workspace.SendText = encodingCase.Text;
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Check(!workspace.SendHex && workspace.SendEncoding == (string)item.Tag && item.IsChecked, "Text submenu chooses and highlights the encoding");
                Check(workspace.SendCommand.CanExecute(null), $"Sender remains active for {item.Tag}: {workspace.SendEndpoint?.Name ?? "null"}");
                await workspace.SendCommand.ExecuteAsync();
                sent = new byte[encodingCase.Bytes.Length];
                await peer.GetStream().ReadExactlyAsync(sent).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                Check(sent.SequenceEqual(encodingCase.Bytes), $"{item.Tag} encoding sends expected wire bytes without BOM");
            }
            ((MenuItem)textItem.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            workspace.SendText = "中";
            Check(workspace.SendSize == "所选编码无法表示输入字符", "ASCII rejects unrepresentable characters instead of replacing them");
            workspace.SendEncoding = "UTF-8";
            workspace.SendText = "线圈 user payload";
            hexItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(workspace.SendHex && workspace.SendText == "线圈 user payload", "Switching format preserves editor contents");
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var lineEndingPanel = (FrameworkElement)connectionView.FindName("SendLineEnding");
            Check(lineEndingPanel.Visibility == Visibility.Collapsed, "HEX hides the line-ending option");
            workspace.SendHex = false;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(lineEndingPanel.Visibility == Visibility.Visible, "Text input shows the line-ending option");
            workspace.SendHex = true;
            var pane = (TabControl)connectionView.FindName("LowerPane");
            var paneRow = (RowDefinition)connectionView.FindName("LowerPaneRow");
            var toggle = (Button)pane.Template.FindName("PaneToggle", pane);
            var paneContent = (FrameworkElement)pane.Template.FindName("PaneContent", pane);
            var oldPaneHeight = paneRow.Height;
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(paneContent.Visibility == Visibility.Collapsed && paneRow.Height.IsAuto && paneRow.MinHeight == 0, "Collapse hides the complete lower content and releases its height");
            Check(pane.SelectedIndex == 1 && workspace.SendText == "线圈 user payload" && session!.State == ConnectionState.Connected, "Collapse preserves tab, input and live session");
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(paneContent.Visibility == Visibility.Visible && paneRow.Height == oldPaneHeight, "Expand restores the previous panel height");
            var previousEndpoint = workspace.Selected;
            var udpEndpoint = new ConnectionViewModel(new()
            {
                Kind = TransportKind.Udp,
                LocalAddress = "127.0.0.1",
                LocalPort = 23571,
                Host = "127.0.0.1",
                Port = 23572
            });
            workspace.Selected = udpEndpoint;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(((FrameworkElement)connectionView.FindName("RemoteSettings")).Visibility == Visibility.Collapsed && ((FrameworkElement)connectionView.FindName("SendEndpointSelectors")).Visibility == Visibility.Visible, "UDP displays one bind section and generic send-area endpoints");
            Check(udpEndpoint.Endpoint == "127.0.0.1:23571" && udpEndpoint.Host == "127.0.0.1" && udpEndpoint.Port == 23572, "UDP identity is its local binding, independent of destination");
            workspace.Selected = previousEndpoint;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var serialRow = new TrafficRow(1, Guid.NewGuid(), "COM2", new(DateTimeOffset.Now, TrafficDirection.Tx, PayloadKind.StreamChunk, [0x41], "COM2"), true, TransportKind.Serial);
            Check(serialRow.Endpoint == "—" && serialRow.Record.Endpoint == "COM2", "Serial peer is unknown while original port metadata is retained");
            Check(Translation.Text("TCP · 主机 · 正确") == "TCP · Master · Valid", "Generated event labels translate without changing protocol identifiers");
            shell.SelectedPageIndex = 1;
            Stage("routing and encoding checked");
            shell.Modbus.Logs.Add(log);
            shell.Modbus.SelectedLog = log;
            Check(shell.IsModbus && shell.SelectedPageIndex == 1, "Navigation remains independent of language");
            await Capture(main, "light-en");
            Stage("first capture");

            appearance.Apply("Light", "zh-CN", false);
            shell.SelectedPageIndex = 0;
            await Capture(main, "light-zh");

            appearance.Apply("Dark", "zh-CN", false);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(grid.Columns[0].Header.ToString() == "算法" && ((TextBlock)dialog.FindName("DialogHeading")).Text == "新建寄存器标签", "Switching back restores Chinese");
            Check(((SolidColorBrush)thumb.Background).Color == oldColor, "Switching back restores dark scrollbar");
            shell.SelectedPageIndex = 0;
            await Capture(main, "dark-zh", sendPanel: true);
            shell.Workspace.Connections.Add(udpEndpoint);
            shell.Workspace.Selected = udpEndpoint;
            await Capture(main, "udp-send-dark", sendPanel: true);
            appearance.Apply("Light", "zh-CN", false);
            await Capture(main, "udp-send-light", sendPanel: true);
            Stage("captures done");
        }
        finally
        {
            Stage("cleanup");
            testWindow?.Close();
            appearance.Apply("Dark", "zh-CN", false);
            await modbus.ShutdownAsync();
            await workspace.ShutdownAsync();
            await shell.ShutdownAsync();
            Check((await new ProfileStore(mainPath).LoadAsync()).Single().Name == "Retained configuration", "Closing before initialization preserves saved connection profiles");
            File.Delete(settingsPath);
            File.Delete(profilePath);
            File.Delete(mainPath);
        }
    }

    private static void Layout(FrameworkElement view)
    {
        view.Measure(new Size(1450, 850));
        view.Arrange(new Rect(0, 0, 1450, 850));
        view.UpdateLayout();
    }

    private static async Task Capture(MainWindow window, string name, bool sendPanel = false)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var host = new Window { Content = content, DataContext = window.DataContext, Width = 1450, Height = 850, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        host.Show();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        if (sendPanel)
        {
            var connectionPage = Visuals<ConnectionsView>(content).Single();
            Visuals<TabControl>(connectionPage).Single().SelectedIndex = 1;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        }
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var directory = Path.Combine(Path.GetTempPath(), "CommunicationStudio-appearance-preview");
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, name + ".png"));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(output);
        host.Content = null;
        host.Close();
        window.Content = content;
    }

    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                yield return match;
            foreach (var nested in Visuals<T>(child))
                yield return nested;
        }
    }
}
