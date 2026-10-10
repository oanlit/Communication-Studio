using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Communication.App.Presentation;
using Communication.App.ViewModels;
using Communication.App.Views;
using Communication.Core;

internal static class SendRoutingTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 150 && !condition(); i++)
            await Task.Delay(10);
        Check(condition(), "Send routing state did not update");
    }
    private static int Port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    public static async Task Run()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ics-send-routing-{Guid.NewGuid():N}.json");
        var workspace = new WorkspaceViewModel(path);
        using var external = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var externalPort = ((IPEndPoint)external.Client.LocalEndPoint!).Port;
        var a = new ConnectionViewModel(new()
        {
            Kind = TransportKind.Udp,
            LocalAddress = "127.0.0.1",
            LocalPort = Port()
        });
        var b = new ConnectionViewModel(new()
        {
            Name = "Receiver B",
            Kind = TransportKind.Udp,
            LocalAddress = "127.0.0.1",
            LocalPort = Port()
        });
        var server = new ConnectionViewModel(new()
        {
            Name = "Listener",
            Kind = TransportKind.TcpServer,
            LocalAddress = "127.0.0.1",
            LocalPort = Port()
        });
        var serial = new ConnectionViewModel(new()
        {
            Name = "Serial",
            Kind = TransportKind.Serial
        });
        var view = new ConnectionsView { DataContext = workspace };
        var window = new Window { Content = view, Width = 1450, Height = 850, ShowInTaskbar = false };
        try
        {
            workspace.Connections.Add(a);
            workspace.Connections.Add(b);
            workspace.Connections.Add(server);
            workspace.Connections.Add(serial);
            workspace.Connections.Add(new(new()
            {
                Name = b.Name,
                Kind = TransportKind.Udp,
                LocalAddress = "127.0.0.1",
                LocalPort = Port()
            }));
            a.Validate();
            Check(a.Name == "" && a.DisplayName == a.DefaultName && a.DefaultName.Contains(a.LocalEndpoint), "Empty endpoint names use an address-based default without modifying the profile");
            var ipPicker = new ComboBox { Style = (Style)Application.Current.FindResource("IpAddressPicker"), ItemsSource = IpAddressPresets.Bind };
            ipPicker.SetBinding(ComboBox.TextProperty, new System.Windows.Data.Binding(nameof(ConnectionViewModel.LocalAddress)) { Source = a, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
            ipPicker.ApplyTemplate();
            ipPicker.SelectedItem = IpAddressPresets.Bind.First(preset => preset.Address == "0.0.0.0");
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(ipPicker.IsEditable && a.LocalAddress == "0.0.0.0", "Selecting semantic bind preset stores the actual IP rather than its description");
            ipPicker.Text = "192.0.2.5";
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(a.LocalAddress == "192.0.2.5", "IP picker accepts arbitrary manual input");
            ipPicker.Text = "127.0.0.1";
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(IpAddressPresets.Remote.All(preset => preset.Address is not "0.0.0.0" and not "::") && IpAddressPresets.Bind.Any(preset => preset.Address == "::"), "Presets distinguish remote destinations from wildcard bind addresses");
            workspace.Selected = a;
            await workspace.ConnectCommand.ExecuteAsync();
            workspace.Selected = b;
            await workspace.ConnectCommand.ExecuteAsync();
            Check(workspace.SendEndpoint == a && workspace.SendEndpoints.SequenceEqual(new[] { a, b }), "Sender selection is independent of sidebar and lists only online resources");
            Check(workspace.ReceiverOptions.Any(option => option.Address == "255.255.255.255:5000"), "IPv4 UDP destination list includes a broadcast preset with the current destination port");
            window.Show();
            ((TabControl)view.FindName("LowerPane")).SelectedIndex = 1;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var endpointList = (ListBox)view.FindName("EndpointList");
            var offlineItem = (ListBoxItem)endpointList.ItemContainerGenerator.ContainerFromItem(serial);
            workspace.Selected = b;
            typeof(ConnectionsView).GetMethod("EndpointContextMenuOpening", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(view, new object?[] { offlineItem, null });
            var endpointMenu = offlineItem.ContextMenu;
            endpointMenu.PlacementTarget = offlineItem;
            endpointMenu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(workspace.Selected == serial && endpointMenu.IsOpen && ReferenceEquals(endpointMenu.DataContext, workspace), "Endpoint context menu targets the right-clicked card and remains open");
            Check(((MenuItem)endpointMenu.Items[0]).IsEnabled && !((MenuItem)endpointMenu.Items[1]).IsEnabled && ((MenuItem)endpointMenu.Items[3]).IsEnabled, "Offline endpoint menu enables open and remove, disables close");
            endpointMenu.IsOpen = false;
            var onlineItem = (ListBoxItem)endpointList.ItemContainerGenerator.ContainerFromItem(b);
            typeof(ConnectionsView).GetMethod("EndpointContextMenuOpening", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(view, new object?[] { onlineItem, null });
            endpointMenu = onlineItem.ContextMenu;
            endpointMenu.PlacementTarget = onlineItem;
            endpointMenu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(!((MenuItem)endpointMenu.Items[0]).IsEnabled && ((MenuItem)endpointMenu.Items[1]).IsEnabled && ((MenuItem)endpointMenu.Items[3]).IsEnabled, "Online endpoint menu enables close and remove, disables open");
            endpointMenu.IsOpen = false;
            var senderPicker = (ComboBox)view.FindName("SendEndpointPicker");
            var receiverPicker = (ComboBox)view.FindName("ReceiverPicker");
            senderPicker.ApplyTemplate();
            receiverPicker.ApplyTemplate();
            workspace.Selected = a;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var nameEditor = (TextBox)view.FindName("EndpointNameEditor");
            nameEditor.ApplyTemplate();
            Check(((FrameworkElement)nameEditor.Template.FindName("Watermark", nameEditor)).Visibility == Visibility.Visible && Equals(nameEditor.Tag, a.DefaultName), "Empty name editor displays its default-name watermark");
            a.Name = "Named sender";
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(a.DisplayName == "Named sender" && ((FrameworkElement)nameEditor.Template.FindName("Watermark", nameEditor)).Visibility == Visibility.Collapsed, "Explicit name replaces the watermark");
            a.Name = "";
            workspace.Selected = b;
            await workspace.SaveCommand.ExecuteAsync();
            Check((await new ProfileStore(path).LoadAsync()).Single(profile => profile.Id == a.Id).Name == "", "Saving preserves an unset name rather than baking in its default");
            Check(DropDownPlacement.GetValue(senderPicker) == PlacementMode.Top && ((Popup)senderPicker.Template.FindName("PART_Popup", senderPicker)).Placement == PlacementMode.Top, "Sender picker opens upward");
            receiverPicker.SelectedItem = workspace.ReceiverOptions.Single(option => option.Address == b.LocalEndpoint);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(workspace.ReceiverAddress == b.LocalEndpoint, "Selecting among duplicate UDP names still uses the chosen destination address");
            Check(receiverPicker.Text == b.DisplayName && Equals(receiverPicker.ToolTip, b.LocalEndpoint), "Receiver displays the name and exposes the address as a tooltip");
            Capture(view, "send-selectors-dark");
            Appearance.Current.Apply("Light", "zh-CN", false);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Capture(view, "send-selectors-light");
            Appearance.Current.Apply("Dark", "zh-CN", false);
            workspace.SendText = "A1 B2";
            await workspace.SendCommand.ExecuteAsync();
            await Eventually(() => b.Statistics.RxBytes == 2 && workspace.Traffic.Any(row => row.ConnectionId == a.Id));
            Check(a.Statistics.TxBytes == 2 && b.Statistics.TxBytes == 0, "Send goes from chosen sender to chosen UDP receiver");
            var txRow = workspace.Traffic.First(row => row.ConnectionId == a.Id && row.Direction == "TX");
            Check(txRow.Connection == a.DefaultName && txRow.PeerName == b.DisplayName && txRow.Endpoint == b.LocalEndpoint, "Traffic retains routing addresses while displaying default source and named peer");
            Check(txRow.ConnectionAddress == a.LocalEndpoint && txRow.ConnectionAddress != txRow.Endpoint, "Traffic source tooltip uses the source address rather than its peer address");
            var peerOption = workspace.TrafficEndpointFilter.Options.Single(option => option.Value == b.LocalEndpoint);
            Check(peerOption.Label == b.DisplayName && peerOption.ToolTip == b.LocalEndpoint, "Peer filter list displays endpoint names with address tooltips");
            workspace.ReceiverAddress = $"127.0.0.1:{externalPort}";
            await workspace.SendCommand.ExecuteAsync();
            var datagram = await external.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Check(datagram.Buffer.SequenceEqual(new byte[] { 0xA1, 0xB2 }) && datagram.RemoteEndPoint.Port == a.LocalPort, "Manual UDP address sends from selected socket to an external peer");
            workspace.IntervalMs = 50;
            workspace.PeriodicCommand.Execute(null);
            workspace.SendEndpoint = b;
            workspace.ReceiverAddress = "invalid";
            datagram = await external.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Check(datagram.RemoteEndPoint.Port == a.LocalPort, "Periodic send freezes source and destination even after picker changes");
            workspace.PeriodicCommand.Execute(null);
            await Eventually(() => !workspace.IsPeriodic);

            workspace.Selected = server;
            await workspace.ConnectCommand.ExecuteAsync();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.LocalPort);
            await Eventually(() => server.Peers.Count == 1);
            workspace.SendEndpoint = server;
            Check(workspace.CanChooseReceiver && workspace.ReceiverOptions.Count == 1, "TCP listener receiver list contains accepted clients");
            workspace.ReceiverAddress = server.Peers.Single().Split(" · ")[0];
            await workspace.SendCommand.ExecuteAsync();
            var bytes = new byte[2];
            await client.GetStream().ReadExactlyAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Check(bytes.SequenceEqual(new byte[] { 0xA1, 0xB2 }), "Manually entered TCP peer address routes through existing accepted client");
            workspace.ReceiverAddress = "127.0.0.1:1";
            var tx = server.Statistics.TxBytes;
            await workspace.SendCommand.ExecuteAsync();
            Check(server.Statistics.TxBytes == tx && workspace.Logs[0].Level == "ERROR" && workspace.Logs[0].Connection == server.Name, "Unaccepted TCP destination is rejected without sending and logs the sender");
            client.Close();
            await Eventually(() => server.Peers.Count == 0 && workspace.ReceiverOptions.Count == 0);
            var knownClient = new ConnectionViewModel(new()
            {
                Name = "Known TCP client",
                Host = "127.0.0.1",
                Port = server.LocalPort
            });
            workspace.Connections.Add(knownClient);
            workspace.Selected = knownClient;
            await workspace.ConnectCommand.ExecuteAsync();
            await Eventually(() => server.Peers.Count == 1 && workspace.ReceiverOptions.Count == 1);
            Check(workspace.ReceiverOptions[0].Name == knownClient.DisplayName, "Accepted TCP peer is resolved to this workspace's client name by its actual local socket");
            workspace.ReceiverAddress = workspace.ReceiverOptions[0].Address;
            await workspace.SendCommand.ExecuteAsync();
            await Eventually(() => knownClient.Statistics.RxBytes == 2);
            workspace.SendEndpoint = knownClient;
            Check(!workspace.CanChooseReceiver && workspace.ReceiverText == server.DisplayName, "TCP client displays the known listener name as a read-only receiver");
            workspace.SendEndpoint = server;
            workspace.Selected = a;
            await workspace.DisconnectCommand.ExecuteAsync();
            Check(!workspace.SendEndpoints.Contains(a) && workspace.SendEndpoint == server, "Disconnected sender is removed without changing another chosen sender");
            workspace.Selected = server;
            await workspace.DisconnectCommand.ExecuteAsync();
            Check(workspace.SendEndpoint is null && !workspace.SendCommand.CanExecute(null), "Closing the chosen sender clears selection instead of rerouting silently");
            workspace.SendEndpoint = b;

            serial.State = ConnectionState.Connected;
            workspace.RefreshCommands();
            workspace.SendEndpoint = serial;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(!workspace.CanChooseReceiver && !receiverPicker.IsEnabled && workspace.ReceiverAddress == "—", "Serial receiver has disabled noneditable appearance");
            serial.State = ConnectionState.Disconnected;
            workspace.RefreshCommands();
            Check(!workspace.SendEndpoints.Contains(serial), "Offline serial resource cannot appear in the sender picker");

            workspace.Selected = serial;
            senderPicker.SelectedItem = workspace.SendSourceOptions.Single(option => option.IsCurrent);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(workspace.SelectedSendSource?.IsCurrent == true && workspace.SendEndpoint == serial && !workspace.SendCommand.CanExecute(null) && !workspace.PeriodicCommand.CanExecute(null), "Current endpoint can be selected while offline, with sending and periodic sending disabled");
            workspace.Selected = b;
            Check(workspace.SendEndpoint == b && workspace.SendCommand.CanExecute(null), "Current sender follows sidebar selection to an online endpoint");
            await workspace.DisconnectCommand.ExecuteAsync();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(workspace.SelectedSendSource?.IsCurrent == true && workspace.SendEndpoint == b && !workspace.SendCommand.CanExecute(null), "Closing current endpoint preserves the current-source option while disabling send");
            await workspace.ConnectCommand.ExecuteAsync();
            Check(workspace.SelectedSendSource?.IsCurrent == true && workspace.SendCommand.CanExecute(null), "Reopening current endpoint enables sending without selecting the sender again");
            senderPicker.SelectedItem = workspace.SendSourceOptions.Single(option => option.Endpoint == b);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            workspace.Selected = serial;
            Check(workspace.SendEndpoint == b && workspace.SelectedSendSource?.IsCurrent == false, "Explicit sender remains independent of the sidebar after leaving current mode");

            workspace.Selected = server;
            server.LocalAddress = "192.0.2.30";
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var localAddresses = await IpAddressPresets.DiscoverLocalAsync();
            var bindPicker = (ComboBox)view.FindName("BindIpPicker");
            bindPicker.IsDropDownOpen = true;
            await Eventually(() => localAddresses.All(address => bindPicker.Items.OfType<IpAddressPreset>().Contains(address)));
            Check(server.LocalAddress == "192.0.2.30" && bindPicker.Text == "192.0.2.30", "Refreshing interface choices preserves manually entered IP and its model binding");
            Check(IpAddressPresets.Bind.All(preset => bindPicker.Items.OfType<IpAddressPreset>().Contains(preset)), "Interface discovery retains wildcard and loopback presets");
            if (localAddresses.Count > 0)
            {
                bindPicker.SelectedItem = bindPicker.Items.OfType<IpAddressPreset>().First(option => option == localAddresses[0]);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Check(server.LocalAddress == localAddresses[0].Address, "Selecting an interface fills its actual address rather than its display name");
            }
            bindPicker.IsDropDownOpen = false;

            Check(!new ConnectionViewModel(new(), modbusManaged: true).CanRemove, "Modbus-owned mirror cannot be removed through endpoint management");
            serial.SetOccupiedBy("Modbus");
            Check(!serial.CanRemove, "Occupied endpoint cannot be removed");
            serial.SetOccupiedBy(null);
            workspace.Selected = b;
            var aItem = (ListBoxItem)endpointList.ItemContainerGenerator.ContainerFromItem(a);
            var removeButton = Visuals<Button>(aItem).Single(button => Equals(button.Content, "×"));
            removeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Eventually(() => !workspace.Connections.Contains(a));
            Check(workspace.Connections.Contains(b) && workspace.Connections.Contains(serial), "Card close icon removes its own endpoint instead of the previously selected endpoint");

            workspace.SendEndpoint = b;
            workspace.ReceiverAddress = $"127.0.0.1:{externalPort}";
            workspace.IntervalMs = 50;
            workspace.PeriodicCommand.Execute(null);
            var removedSession = b.Session!;
            workspace.Selected = b;
            await workspace.RemoveCommand.ExecuteAsync();
            Check(!workspace.IsPeriodic && !workspace.Connections.Contains(b) && !workspace.SendEndpoints.Contains(b) && workspace.SendEndpoint is null && removedSession.State == ConnectionState.Disconnected, "Removing an online endpoint stops its periodic send, closes its socket and clears sender choices");
            using var rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback, b.LocalPort));
            Check(workspace.Connections.Contains(server) && workspace.Connections.Contains(serial), "Removing an endpoint preserves other resources and releases the port for reuse");
        }
        finally
        {
            window.Close();
            await workspace.ShutdownAsync();
            if (File.Exists(path))
                File.Delete(path);
        }

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
    private static void Capture(FrameworkElement view, string name)
    {
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var directory = Path.Combine(Path.GetTempPath(), "CommunicationStudio-appearance-preview");
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, name + ".png"));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(output);
    }
}
