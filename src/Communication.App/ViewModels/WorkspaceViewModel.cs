using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Windows.Data;
using System.Windows.Threading;
using Communication.Core;
using Communication.App.Presentation;
using Communication.Transports;
using Microsoft.Win32;

namespace Communication.App.ViewModels;

public sealed class WorkspaceViewModel : ObservableObject
{
    private readonly Channel<(ConnectionViewModel Connection, TrafficRecord Record)> _traffic;
    private readonly Channel<(ModbusOwnedConnection Owner, TrafficRecord Record)> _modbusTraffic;
    public void ReceiveModbusTraffic(ModbusOwnedConnection owner, TrafficRecord record) => _modbusTraffic.Writer.TryWrite((owner, record));
    private readonly Channel<(ConnectionViewModel Connection, ICommunicationSession Session, ConnectionChange? Change, string? Message)> _events =
        Channel.CreateBounded<(ConnectionViewModel, ICommunicationSession, ConnectionChange?, string?)>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly DispatcherTimer _timer;
    private readonly ProfileStore _store;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private long _dropped, _sequence, _pausedCount;
    private bool _closing;
    public bool HasInputErrors
    {
        get; set;
    }
    private bool _loadFailed;
    private bool _initialized;
    private CancellationTokenSource? _periodic;
    private Task _periodicTask = Task.CompletedTask;
    private Guid? _periodicEndpointId;
    public ObservableCollection<ConnectionViewModel> Connections { get; } = [];
    public ObservableCollection<ConnectionViewModel> SendEndpoints { get; } = [];
    private readonly SendSourceOption _currentSendSource = new(null, IsCurrent: true);
    public ObservableCollection<SendSourceOption> SendSourceOptions { get; } = [];
    private bool _followCurrentSender, _updatingCurrentSender;
    public SendSourceOption? SelectedSendSource
    {
        get => _followCurrentSender ? _currentSendSource : SendSourceOptions.FirstOrDefault(option => !option.IsCurrent && ReferenceEquals(option.Endpoint, SendEndpoint));
        set
        {
            if (value is null && _followCurrentSender)
                return;
            _followCurrentSender = value?.IsCurrent == true;
            if (_followCurrentSender)
                UpdateCurrentSender();
            else
                SendEndpoint = value?.Endpoint;
            Notify();
        }
    }
    private void UpdateCurrentSender()
    {
        _updatingCurrentSender = true;
        try
        {
            SendEndpoint = Selected;
        }
        finally
        {
            _updatingCurrentSender = false;
        }

    }
    public ObservableCollection<SendTargetOption> ReceiverOptions { get; } = [];
    private readonly Dictionary<(Guid Id, TransportKind Kind), string> _receiverByEndpoint = [];
    private ConnectionViewModel? _sendEndpoint;
    private bool _hasChosenSender;
    public ConnectionViewModel? SendEndpoint
    {
        get => _sendEndpoint;
        set
        {
            if (!_updatingCurrentSender)
                _followCurrentSender = false;
            if (!Set(ref _sendEndpoint, value))
            {
                Notify(nameof(SelectedSendSource));
                return;
            }
            if (value is not null)
                _hasChosenSender = true;
            ReceiverAddress = value is null ? "" : value.IsSerial ? "—" : value.IsTcpClient ? $"{value.Host}:{value.Port}"
                : _receiverByEndpoint.GetValueOrDefault((value.Id, value.Kind)) ?? (value.IsServer ? value.SelectedPeer ?? "" : $"{value.Host}:{value.Port}");
            Notify(nameof(CanChooseReceiver));
            UpdateReceiverOptions();
            SendCommand?.Refresh();
            PeriodicCommand?.Refresh();
            Notify(nameof(SelectedSendSource));
        }
    }
    private string _receiverAddress = "";
    public string ReceiverAddress
    {
        get => _receiverAddress;
        set
        {
            if (!Set(ref _receiverAddress, value))
                return;
            if (SendEndpoint is { } endpoint && CanChooseReceiver)
                _receiverByEndpoint[(endpoint.Id, endpoint.Kind)] = value;
            Notify(nameof(ReceiverText));
        }
    }
    public string ReceiverText
    {
        get => ReceiverOptions.FirstOrDefault(option => option.Address == ReceiverAddress)?.Name
            ?? ResolvePeerName(ReceiverAddress, SendEndpoint?.Kind);
        set
        {
            if (value == ReceiverText)
                return;
            var matches = ReceiverOptions.Where(option => option.Name == value).ToArray();
            ReceiverAddress = matches.Length == 1 ? matches[0].Address : value;
        }
    }
    public bool CanChooseReceiver => SendEndpoint?.Kind is TransportKind.Udp or TransportKind.TcpServer;
    private void UpdateSendEndpoints()
    {
        var available = Connections.Where(c => c.State == ConnectionState.Connected && !c.IsOccupied && !c.IsModbusManaged).ToArray();
        foreach (var stale in SendEndpoints.Where(c => !available.Contains(c)).ToArray())
            SendEndpoints.Remove(stale);
        foreach (var endpoint in available)
            if (!SendEndpoints.Contains(endpoint))
                SendEndpoints.Add(endpoint);
        if (!SendSourceOptions.Contains(_currentSendSource))
            SendSourceOptions.Insert(0, _currentSendSource);
        foreach (var stale in SendSourceOptions.Where(option => !option.IsCurrent && !available.Contains(option.Endpoint)).ToArray())
            SendSourceOptions.Remove(stale);
        foreach (var endpoint in available)
            if (!SendSourceOptions.Any(option => ReferenceEquals(option.Endpoint, endpoint)))
                SendSourceOptions.Add(new(endpoint));
        if (_followCurrentSender)
            UpdateCurrentSender();
        else if (SendEndpoint is not null && !available.Contains(SendEndpoint))
            SendEndpoint = null;
        if (!_followCurrentSender && SendEndpoint is null && !_hasChosenSender)
            SendEndpoint = available.FirstOrDefault(c => ReferenceEquals(c, Selected)) ?? available.FirstOrDefault();
        Notify(nameof(SelectedSendSource));
        UpdateReceiverOptions();
    }
    private void UpdateReceiverOptions()
    {
        SendTargetOption[] options = SendEndpoint?.Kind switch
        {
            TransportKind.Udp => Connections.Where(c => c.IsUdp && !ReferenceEquals(c, SendEndpoint))
                .Select(c => new SendTargetOption(c.DisplayName, $"{(c.LocalAddress == "0.0.0.0" ? "127.0.0.1" : c.LocalAddress == "::" ? "[::1]" : c.LocalAddress.Contains(':') ? $"[{c.LocalAddress}]" : c.LocalAddress)}:{c.LocalPort}"))
                .Concat(UdpSpecialTargets()).ToArray(),
            TransportKind.TcpServer => ((SendEndpoint.Session as IStreamSession)?.Peers ?? [])
                .Select(peer => new SendTargetOption(ResolvePeerName(peer, TransportKind.TcpServer), peer)).ToArray(),
            _ => []
        };
        if (ReceiverOptions.SequenceEqual(options))
            return;
        var address = ReceiverAddress;
        ReceiverOptions.Clear();
        foreach (var option in options)
            ReceiverOptions.Add(option);
        ReceiverAddress = address;
        Notify(nameof(ReceiverAddress));
        Notify(nameof(ReceiverText));
    }
    private IEnumerable<SendTargetOption> UdpSpecialTargets()
    {
        if (SendEndpoint is not { } sender)
            yield break;
        var port = Uri.TryCreate("udp://" + ReceiverAddress, UriKind.Absolute, out var target) && target.Port is > 0 and <= 65535
            ? target.Port : sender.Port;
        var presets = sender.LocalAddress.Contains(':') ? IpAddressPresets.UdpV6 : IpAddressPresets.UdpV4;
        foreach (var preset in presets)
            yield return new(Translation.Text(preset.Description), $"{(preset.Address.Contains(':') ? $"[{preset.Address}]" : preset.Address)}:{port}");
    }
    private string ResolvePeerName(string address, TransportKind? kind)
    {
        if (kind == TransportKind.Serial)
            return "—";
        var matches = Connections.Where(c => kind == TransportKind.Udp
            ? c.IsUdp && SameAddress(c.LocalEndpoint, address)
            : c.IsServer && SameAddress(c.LocalEndpoint, address)
                || c.IsTcpClient && c.Session is TcpClientSession { LocalEndpoint: { } local } && SameAddress(local, address)).ToArray();
        return matches.Length == 1 ? matches[0].DisplayName : address;
    }
    private static bool SameAddress(string local, string peer)
    {
        static bool Parse(string text, out string host, out int port)
        {
            text = text.Split(" · ")[0];
            if (System.Net.IPEndPoint.TryParse(text, out var ip))
            {
                host = ip.Address.ToString();
                port = ip.Port;
                return true;
            }
            if (Uri.TryCreate("tcp://" + text, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                host = uri.DnsSafeHost;
                port = uri.Port;
                return true;
            }
            host = "";
            port = 0;
            return false;
        }
        if (!Parse(local, out var host, out var port) || !Parse(peer, out var peerHost, out var peerPort) || port != peerPort)
            return false;
        if (host == peerHost)
            return true;
        if (!System.Net.IPAddress.TryParse(host, out var left) || !System.Net.IPAddress.TryParse(peerHost, out var right))
            return false;
        return left.MapToIPv6().Equals(right.MapToIPv6())
            || ((left.Equals(System.Net.IPAddress.Any) || left.Equals(System.Net.IPAddress.IPv6Any)) && System.Net.IPAddress.IsLoopback(right));
    }
    private Func<IReadOnlyList<ModbusOwnedConnection>>? _modbusConnections;
    public bool CanUseSendArea => !_closing && (SendEndpoint is { IsOccupied: false, IsModbusManaged: false } || Selected is { IsOccupied: false, IsModbusManaged: false });
    private bool CanSendFromEndpoint => SendEndpoint is { State: ConnectionState.Connected, IsOccupied: false, IsModbusManaged: false } endpoint && SendEndpoints.Contains(endpoint);
    public void SetModbusSource(Func<IReadOnlyList<ModbusOwnedConnection>> source)
    {
        _modbusConnections = source;
        SynchronizeModbusConnections();
    }
    public void SynchronizeModbusConnections()
    {
        if (_modbusConnections is null)
            return;
        var owned = _modbusConnections();
        foreach (var mirror in Connections.Where(connection => connection.IsModbusManaged).ToArray())
        {
            var resource = owned.FirstOrDefault(item => item.Profile.Id == mirror.Id);
            if (resource is null || Connections.Any(connection => !connection.IsModbusManaged && resource.ConflictsWith(connection.Profile)))
            {
                Connections.Remove(mirror);
                if (ReferenceEquals(Selected, mirror))
                    Selected = Connections.FirstOrDefault();
            }
            else
                mirror.UpdateManagedProfile(resource.Profile);
        }
        foreach (var resource in owned)
            if (!Connections.Any(connection => resource.ConflictsWith(connection.Profile)))
                Connections.Add(new(resource.Profile, modbusManaged: true));
        foreach (var connection in Connections)
        {
            var resource = owned.FirstOrDefault(item => item.ConflictsWith(connection.Profile));
            connection.SetOccupiedBy(resource?.Owner);
            if (resource is not null)
                connection.ObserveModbusCounters(resource.Counters);
        }
        Notify(nameof(Summary));
        RefreshCommands();
    }
    public ObservableCollection<TrafficRow> Traffic { get; } = [];
    public ObservableCollection<LogRow> Logs { get; } = [];
    public ICollectionView TrafficView
    {
        get;
    }
    public MultiSelectFilter TrafficDirectionFilter
    {
        get;
    }
    public MultiSelectFilter TrafficConnectionFilter
    {
        get;
    }
    public MultiSelectFilter TrafficEndpointFilter
    {
        get;
    }
    private bool _trafficFiltersDirty;
    private bool _timeAscending = true;
    public bool TimeAscending => _timeAscending;
    public void ToggleTrafficTimeSort()
    {
        _timeAscending = !_timeAscending;
        Notify(nameof(TimeAscending));
        SetTrafficSort();
    }
    private void SetTrafficSort()
    {
        var direction = TimeAscending ? ListSortDirection.Ascending : ListSortDirection.Descending;
        using (TrafficView.DeferRefresh())
        {
            TrafficView.SortDescriptions.Clear();
            TrafficView.SortDescriptions.Add(new(nameof(TrafficRow.Timestamp), direction));
            TrafficView.SortDescriptions.Add(new(nameof(TrafficRow.Sequence), direction));
        }
    }
    private void UpdateTrafficFilters()
    {
        if (!_trafficFiltersDirty)
            return;
        _trafficFiltersDirty = false;
        TrafficDirectionFilter.UpdateOptions(Traffic.Select(row => row.Direction));
        TrafficConnectionFilter.UpdateOptions(Traffic.Select(row => row.Connection));
        TrafficEndpointFilter.UpdateOptions(Traffic.Select(row => row.Endpoint));
        var peerLabels = Traffic.GroupBy(row => row.Endpoint).ToDictionary(group => group.Key, group => group.First().PeerName);
        foreach (var option in TrafficEndpointFilter.Options)
            option.SetLabel(peerLabels[option.Value]);
    }
    public TransportKind[] TransportKinds { get; } = [TransportKind.Serial, TransportKind.TcpServer, TransportKind.TcpClient, TransportKind.Udp];
    public string[] Parities { get; } = ["None", "Odd", "Even", "Mark", "Space"];
    public int[] DataBitsOptions { get; } = [5, 6, 7, 8];
    public string[] StopBitsOptions { get; } = ["One", "Two", "OnePointFive"];
    public string[] Handshakes { get; } = ["None", "XOnXOff", "RequestToSend", "RequestToSendXOnXOff"];
    public string[] LineEndings { get; } = ["None", "CR", "LF", "CRLF"];
    public ObservableCollection<string> SerialPorts { get; } = [];
    public AsyncCommand RefreshSerialPortsCommand
    {
        get;
    }
    public string ConfigurationPath
    {
        get;
    }

    private ConnectionViewModel? _selected;
    public ConnectionViewModel? Selected
    {
        get => _selected; set
        {
            Set(ref _selected, value);
            TrafficView.Refresh();
            RefreshCommands();
        }
    }
    private TrafficRow? _selectedTraffic;
    public TrafficRow? SelectedTraffic
    {
        get => _selectedTraffic;
        set
        {
            if (!Set(ref _selectedTraffic, value))
                return;
            // A filter refresh can clear grid selection; keep the chosen peer until another record is selected.
            if (value is not null && Set(ref _currentPeerAddress, value.Endpoint, nameof(CurrentPeerAddress)) && OnlySelectedPeer)
                TrafficView.Refresh();
        }
    }
    private string? _currentPeerAddress;
    public string? CurrentPeerAddress => _currentPeerAddress;
    private bool _onlySelectedPeer;
    public bool OnlySelectedPeer
    {
        get => _onlySelectedPeer; set
        {
            if (Set(ref _onlySelectedPeer, value))
                TrafficView.Refresh();
        }
    }
    private bool _hexView = true;
    public bool HexView
    {
        get => _hexView; set
        {
            Set(ref _hexView, value);
            foreach (var row in Traffic)
                row.SetHex(value);
        }
    }
    private bool _onlySelected;
    public bool OnlySelected
    {
        get => _onlySelected; set
        {
            Set(ref _onlySelected, value);
            TrafficView.Refresh();
        }
    }
    private string _filter = "";
    public string Filter
    {
        get => _filter; set
        {
            Set(ref _filter, value);
            TrafficView.Refresh();
        }
    }
    private bool _paused;
    public bool Paused
    {
        get => _paused; set => Set(ref _paused, value);
    }
    public bool FollowTail { get; set; } = true;
    private string _sendText = "48 65 6C 6C 6F 2C 20 64 65 76 69 63 65 21";
    public string SendText
    {
        get => _sendText; set
        {
            Set(ref _sendText, value);
            Notify(nameof(SendSize));
        }
    }
    private bool _sendHex = true;
    public bool SendHex
    {
        get => _sendHex; set
        {
            Set(ref _sendHex, value);
            Notify(nameof(SendSize));
            Notify(nameof(SendModeLabel));
            Notify(nameof(IsTextInput));
        }
    }
    private string _sendEncoding = "UTF-8";
    public string SendEncoding
    {
        get => _sendEncoding;
        set
        {
            Set(ref _sendEncoding, value);
            Notify(nameof(SendSize));
            Notify(nameof(SendModeLabel));
        }
    }
    public bool IsTextInput => !SendHex;
    public string SendModeLabel => SendHex ? "HEX" : $"文本 · {SendEncoding}";
    private string _lineEnding = "None";
    public string LineEnding
    {
        get => _lineEnding; set
        {
            Set(ref _lineEnding, value);
            Notify(nameof(SendSize));
        }
    }
    public int IntervalMs { get; set; } = 1000;
    public string SendSize
    {
        get
        {
            try
            {
                return $"{BuildPayload().Length:N0} bytes";
            }
            catch (FormatException)
            {
                return "HEX 格式无效";
            }

            catch (EncoderFallbackException)
            {
                return "所选编码无法表示输入字符";
            }

        }
    }
    public bool IsPeriodic => _periodic is not null;
    public string PeriodicLabel => IsPeriodic ? "停止周期发送" : "周期发送";
    private string _status = "就绪 · 添加端点，开始调试";
    public string Status
    {
        get => _status; set => Set(ref _status, value);
    }
    public string Summary => $"{Connections.Count(c => c.State == ConnectionState.Connected)} ONLINE / {Connections.Count} ENDPOINTS";
    public string BufferStatus => $"{Traffic.Count:N0} / 1,000 条 · 队列丢弃 {Interlocked.Read(ref _dropped):N0} · 暂停忽略 {_pausedCount:N0}";
    public string TotalTx => $"{Connections.DistinctBy(c => c.StatisticsSource).Sum(c => c.Statistics.TxBytes):N0} B";
    public string TotalRx => $"{Connections.DistinctBy(c => c.StatisticsSource).Sum(c => c.Statistics.RxBytes):N0} B";

    public RelayCommand AddCommand
    {
        get;
    }
    public AsyncCommand RemoveCommand
    {
        get;
    }
    public AsyncCommand ConnectCommand
    {
        get;
    }
    public AsyncCommand DisconnectCommand
    {
        get;
    }
    public AsyncCommand SaveCommand
    {
        get;
    }
    public AsyncCommand SendCommand
    {
        get;
    }
    public RelayCommand ClearCommand
    {
        get;
    }
    public AsyncCommand ExportCommand
    {
        get;
    }
    public RelayCommand PeriodicCommand
    {
        get;
    }

    public WorkspaceViewModel(string? configurationPath = null)
    {
        ConfigurationPath = configurationPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CommunicationStudio", "connections.json");
        _store = new ProfileStore(ConfigurationPath);
        _modbusTraffic = Channel.CreateBounded<(ModbusOwnedConnection, TrafficRecord)>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        }, _ => Interlocked.Increment(ref _dropped));
        _traffic = Channel.CreateBounded<(ConnectionViewModel, TrafficRecord)>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        }, _ => Interlocked.Increment(ref _dropped));
        TrafficView = CollectionViewSource.GetDefaultView(Traffic);
        TrafficDirectionFilter = new("I/O", () => TrafficView.Refresh());
        TrafficConnectionFilter = new("端点", () => TrafficView.Refresh());
        TrafficEndpointFilter = new("对端", () => TrafficView.Refresh());
        SetTrafficSort();
        Traffic.CollectionChanged += (_, _) => _trafficFiltersDirty = true;
        TrafficView.Filter = item => item is TrafficRow row && (!OnlySelected || row.ConnectionId == Selected?.Id)
            && TrafficDirectionFilter.Matches(row.Direction) && (OnlySelected || TrafficConnectionFilter.Matches(row.Connection))
            && (OnlySelectedPeer ? row.Endpoint == CurrentPeerAddress : TrafficEndpointFilter.Matches(row.Endpoint))
            && (string.IsNullOrWhiteSpace(Filter) || row.Connection.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                || row.Endpoint.Contains(Filter, StringComparison.OrdinalIgnoreCase) || row.PeerName.Contains(Filter, StringComparison.OrdinalIgnoreCase) || row.Preview.Contains(Filter, StringComparison.OrdinalIgnoreCase));
        AddCommand = new(() =>
{
    var connection = new ConnectionViewModel(new());
    Connections.Add(connection);
    Selected = connection;
}
);
        RemoveCommand = new(RemoveAsync, Report, () => !_closing && Selected is { CanRemove: true });
        ConnectCommand = new(ConnectAsync, Report, () => !_closing && !HasInputErrors && Selected is { CanEdit: true, IsOccupied: false });
        DisconnectCommand = new(DisconnectAsync, Report, () => Selected is { IsOccupied: false, IsModbusManaged: false } && Selected.State is ConnectionState.Connected or ConnectionState.Connecting);
        SaveCommand = new(SaveAsync, Report, () => !_closing && !HasInputErrors);
        SendCommand = new(SendAsync, ReportSend, () => !_closing && !HasInputErrors && CanUseSendArea && CanSendFromEndpoint && !IsPeriodic);
        ClearCommand = new(() =>
        {
            Traffic.Clear();
            SelectedTraffic = null;
            UpdateTrafficFilters();
            TrafficDirectionFilter.SelectAll(true);
            TrafficConnectionFilter.SelectAll(true);
            TrafficEndpointFilter.SelectAll(true);
        });
        ExportCommand = new(ExportAsync, Report);
        PeriodicCommand = new(TogglePeriodic, () => IsPeriodic || (!_closing && !HasInputErrors && CanUseSendArea && CanSendFromEndpoint));
        RefreshSerialPortsCommand = new(RefreshSerialPortsAsync, Report, () => !_closing);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Drain(), Dispatcher.CurrentDispatcher);
    }

    private async Task RefreshSerialPortsAsync()
    {
        try
        {
            var ports = await Task.Run(() => System.IO.Ports.SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray(), _shutdown.Token);
            var connection = Selected;
            var enteredPort = connection?.SerialPort;
            SerialPorts.Clear();
            foreach (var port in ports)
                SerialPorts.Add(port);
            if (connection is not null && enteredPort is not null)
                connection.SerialPort = enteredPort;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {

        }

        catch (Exception ex)
        {
            Report(ex);
        }

    }

    public async Task InitializeAsync()
    {
        await RefreshSerialPortsAsync();
        try
        {
            var profiles = await _store.LoadAsync(_shutdown.Token);
            foreach (var profile in profiles)
                Connections.Add(new(profile));
            if (Connections.Count == 0)
            {
                Connections.Add(new(new()
                {
                    Name = "Local TCP client"
                }));
                Connections.Add(new(new()
                {
                    Name = "TCP listener",
                    Kind = TransportKind.TcpServer,
                    LocalPort = 5000
                }));
                Connections.Add(new(new()
                {
                    Name = "UDP endpoint",
                    Kind = TransportKind.Udp,
                    Port = 5001
                }));
            }
            Selected = Connections.FirstOrDefault();
            _initialized = true;
            Log("INFO", "Workspace", "工作区已就绪；所有端点均需手动启动。");
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            Report(ex);
        }

    }

    private async Task ConnectAsync()
    {
        SynchronizeModbusConnections();
        var connection = Selected!;
        if (connection.IsOccupied || connection.IsModbusManaged)
            throw new InvalidOperationException("通信端口已被 Modbus 占用。");
        connection.Validate();
        if (connection.Session is not null)
            await Task.Run(async () => await connection.Session.DisposeAsync());
        var session = SessionFactory.Create(connection.Profile);
        connection.Session = session;
        session.Traffic += record => _traffic.Writer.TryWrite((connection, record));
        session.StateChanged += change => _events.Writer.TryWrite((connection, session, change, null));
        if (session is TcpServerSession server)
            server.PeerChanged += message => _events.Writer.TryWrite((connection, session, null, message));
        connection.State = ConnectionState.Connecting;
        RefreshCommands();
        // Move opening and device driver cleanup off the UI thread as well as all I/O.
        try
        {
            await Task.Run(() => session.ConnectAsync(_shutdown.Token));
        }
        finally
        {
            connection.State = session.State;
            RefreshCommands();
        }

    }

    private async Task DisconnectAsync()
    {
        var connection = Selected!;
        if (_periodicEndpointId == connection.Id)
            StopPeriodic();
        if (connection.Session is not null)
            await Task.Run(() => connection.Session.DisconnectAsync());
        connection.State = ConnectionState.Disconnected;
        RefreshCommands();
    }
    private async Task RemoveAsync()
    {
        var connection = Selected!;
        if (_periodicEndpointId == connection.Id)
        {
            StopPeriodic();
            await _periodicTask;
        }
        connection.State = ConnectionState.Disconnecting;
        RefreshCommands();
        try
        {
            if (connection.Session is not null)
                await Task.Run(async () => await connection.Session.DisposeAsync());
        }
        catch
        {
            connection.State = connection.Session?.State ?? ConnectionState.Faulted;
            RefreshCommands();
            throw;
        }
        connection.State = ConnectionState.Disconnected;
        Connections.Remove(connection);
        foreach (var key in _receiverByEndpoint.Keys.Where(key => key.Id == connection.Id).ToArray())
            _receiverByEndpoint.Remove(key);
        if (ReferenceEquals(Selected, connection))
            Selected = Connections.FirstOrDefault();
        RefreshCommands();
    }
    private async Task SaveAsync()
    {
        if (HasInputErrors)
            throw new ArgumentException("输入包含无效数字，未保存工作区；请修正标红字段。");
        foreach (var connection in Connections.Where(connection => !connection.IsModbusManaged))
            connection.Validate();
        var profiles = Connections.Where(connection => !connection.IsModbusManaged).Select(c => c.Profile).ToArray();
        await _saveGate.WaitAsync();
        try
        {
            await _store.SaveAsync(profiles);
            _loadFailed = false;
        }
        finally
        {
            _saveGate.Release();
        }

        Log("INFO", "Workspace", $"已保存 {profiles.Length} 个端点配置");
    }
    private byte[] BuildPayload()
    {
        if (SendHex)
            return PayloadCodec.Parse(SendText, true);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(SendEncoding switch
        {
            "UTF-8" => 65001,
            "ASCII" => 20127,
            "UTF-16 LE" => 1200,
            "UTF-16 BE" => 1201,
            "GB18030" => 54936,
            _ => throw new ArgumentException("不支持的文本编码")
        }, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        return encoding.GetBytes(SendText + (LineEnding switch
        {
            "CR" => "\r",
            "LF" => "\n",
            "CRLF" => "\r\n",
            _ => ""
        }));
    }
    private async Task SendAsync()
    {
        SynchronizeModbusConnections();
        if (!CanUseSendArea)
            throw new InvalidOperationException("通信端口已被 Modbus 占用，不能使用发送区。");
        var bytes = BuildPayload();
        if (bytes.Length == 0)
            throw new ArgumentException("发送内容不能为空。");
        await SendToAsync(CreateSendRequest(bytes), _shutdown.Token);
    }
    private sealed record SendRequest(ICommunicationSession Session, string? Peer, string Host, int Port, byte[] Bytes, string SourceName);
    private SendRequest CreateSendRequest(byte[] bytes)
    {
        var connection = SendEndpoint ?? throw new InvalidOperationException("请选择 ONLINE 的发送端。");
        if (!SendEndpoints.Contains(connection) || connection.IsOccupied || connection.IsModbusManaged || connection.Session?.State != ConnectionState.Connected)
            throw new InvalidOperationException("发送端已关闭或被占用，请重新选择。");
        string? peer = null;
        var host = connection.Host;
        var port = connection.Port;
        if (connection.IsUdp)
        {
            if (!Uri.TryCreate("udp://" + ReceiverAddress.Trim(), UriKind.Absolute, out var target)
                || string.IsNullOrEmpty(target.Host) || target.Port is < 1 or > 65535 || target.AbsolutePath != "/" || !string.IsNullOrEmpty(target.UserInfo) || !string.IsNullOrEmpty(target.Query) || !string.IsNullOrEmpty(target.Fragment))
                throw new ArgumentException("请输入接收端地址和端口，例如 127.0.0.1:5001。");
            host = target.DnsSafeHost;
            port = target.Port;
        }
        else if (connection.IsServer)
        {
            var peers = ((IStreamSession)connection.Session).Peers;
            var matches = peers.Where(p => p == ReceiverAddress.Trim() || p.Split(" · ")[0] == ReceiverAddress.Trim()).ToArray();
            if (matches.Length != 1)
                throw new ArgumentException("接收端必须是当前已接入的唯一客户端，请从列表选择。");
            peer = matches[0];
        }
        return new(connection.Session, peer, host, port, bytes, connection.DisplayName);
    }
    private static async Task SendToAsync(SendRequest request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        switch (request.Session)
        {
            case IStreamSession stream:
                await stream.WriteAsync(request.Bytes, request.Peer, timeout.Token);
                break;
            case IDatagramSession datagram:
                await datagram.SendDatagramAsync(request.Bytes, request.Host, request.Port, timeout.Token);
                break;
            default:
                throw new InvalidOperationException("当前端点不支持原始发送。");
        }
    }
    private void TogglePeriodic()
    {
        if (IsPeriodic)
        {
            StopPeriodic();
            return;
        }
        try
        {
            if (IntervalMs < 50 || IntervalMs > 3600000)
                throw new ArgumentException("周期范围为 50–3600000 ms。");
            var bytes = BuildPayload();
            if (bytes.Length == 0)
                throw new ArgumentException("发送内容不能为空。");
            var connection = SendEndpoint ?? throw new InvalidOperationException("请选择 ONLINE 的发送端。");
            var request = CreateSendRequest(bytes);
            _periodic = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _periodicEndpointId = connection.Id;
            _periodicTask = RunPeriodicAsync(request, TimeSpan.FromMilliseconds(IntervalMs), _periodic);
            Log("INFO", connection.DisplayName, $"周期发送已启动：{IntervalMs} ms；固定当前会话、目标和内容。");
            Notify(nameof(IsPeriodic));
            Notify(nameof(PeriodicLabel));
            RefreshCommands();
        }
        catch (Exception ex)
        {
            ReportSend(ex);
        }

    }
    private async Task RunPeriodicAsync(SendRequest request, TimeSpan interval, CancellationTokenSource source)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(source.Token))
                await SendToAsync(request, source.Token);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {

        }

        catch (Exception ex)
        {
            Log("ERROR", request.SourceName, ex.Message);
        }

        finally
        {
            if (ReferenceEquals(_periodic, source))
            {
                _periodic = null;
                _periodicEndpointId = null;
            }
            source.Dispose();
            Notify(nameof(IsPeriodic));
            Notify(nameof(PeriodicLabel));
            RefreshCommands();
        }
    }
    private void StopPeriodic()
    {
        _periodic?.Cancel();
    }
    private async Task ExportAsync()
    {
        var dialog = new SaveFileDialog { Filter = "CSV traffic (*.csv)|*.csv", FileName = $"traffic-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog() != true)
            return;
        var rows = TrafficView.Cast<TrafficRow>().ToArray();
        await Task.Run(async () =>
        {
            await using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true));
            await writer.WriteLineAsync("Timestamp,Connection,Direction,Kind,Endpoint,Bytes,Hex");
            foreach (var row in rows)
                await writer.WriteLineAsync(string.Join(',', Csv(row.Record.Timestamp.ToString("O")), Csv(row.Connection), row.Direction,
                    row.Kind, Csv(row.Endpoint), row.Length, Csv(PayloadCodec.ToHex(row.Record.Data))));
        });
        Log("INFO", "Workspace", $"已导出 {rows.Length} 条可见流量记录");
    }
    private static string Csv(string value) => "\"" + (value.Length > 0 && "=+-@".Contains(value[0]) ? "'" : "") + value.Replace("\"", "\"\"") + "\"";
    private void Drain()
    {
        SynchronizeModbusConnections();
        for (var i = 0; i < 150 && _modbusTraffic.Reader.TryRead(out var item); i++)
        {
            if (Paused)
            {
                _pausedCount++;
                continue;
            }
            var connection = Connections.FirstOrDefault(candidate => item.Owner.ConflictsWith(candidate.Profile));
            Traffic.Add(new(++_sequence, connection?.Id ?? item.Owner.Profile.Id, connection?.DisplayName ?? item.Owner.Profile.Name, item.Record, HexView, item.Owner.Profile.Kind, ResolvePeerName(item.Record.Endpoint, item.Owner.Profile.Kind), connection?.Endpoint ?? new ConnectionViewModel(item.Owner.Profile, modbusManaged: true).Endpoint));
            if (Traffic.Count > 1000)
                Traffic.RemoveAt(0);
        }
        for (var i = 0; i < 100 && _events.Reader.TryRead(out var item); i++)
        {
            if (!ReferenceEquals(item.Connection.Session, item.Session))
                continue;
            if (item.Change is { } change)
            {
                item.Connection.State = change.State;
                item.Connection.Detail = change.Detail;
                Log(change.State == ConnectionState.Faulted ? "ERROR" : "INFO", item.Connection.DisplayName, change.Detail);
            }
            else if (item.Message is { } message)
                Log("INFO", item.Connection.DisplayName, message);
        }
        // ListCollectionView cannot safely process ObservableCollection notifications
        // inside DeferRefresh once a control/current item observes the view.
        // Batch by dispatcher tick, but publish each bounded collection mutation normally.
        for (var i = 0; i < 150 && _traffic.Reader.TryRead(out var item); i++)
        {
            if (Paused)
            {
                _pausedCount++;
                continue;
            }
            Traffic.Add(new(++_sequence, item.Connection.Id, item.Connection.DisplayName, item.Record, HexView, item.Connection.Kind, ResolvePeerName(item.Record.Endpoint, item.Connection.Kind), item.Connection.Endpoint));
            if (Traffic.Count > 1000)
                Traffic.RemoveAt(0);
        }
        UpdateTrafficFilters();
        foreach (var connection in Connections)
        {
            if (connection.Session is not null)
                connection.State = connection.Session.State;
            connection.Refresh();
        }
        Notify(nameof(Summary));
        Notify(nameof(BufferStatus));
        Notify(nameof(TotalTx));
        Notify(nameof(TotalRx));
        RefreshCommands();
    }
    public void RefreshCommands()
    {
        UpdateSendEndpoints();
        Notify(nameof(CanUseSendArea));
        ConnectCommand?.Refresh();
        DisconnectCommand?.Refresh();
        RemoveCommand?.Refresh();
        SendCommand?.Refresh();
        PeriodicCommand?.Refresh();
        SaveCommand?.Refresh();
    }
    private void Report(Exception ex) => Log("ERROR", Selected?.DisplayName ?? "Workspace", ex is OperationCanceledException ? "操作已取消或超时。" : ex.Message);
    private void ReportSend(Exception ex) => Log("ERROR", SendEndpoint?.DisplayName ?? "Workspace", ex is OperationCanceledException ? "操作已取消或超时。" : ex.Message);
    private void Log(string level, string connection, string message)
    {
        Logs.Insert(0, new(DateTimeOffset.Now, level, connection, message));
        if (Logs.Count > 300)
            Logs.RemoveAt(Logs.Count - 1);
        Status = message;
    }
    public async Task ShutdownAsync()
    {
        _closing = true;
        _timer.Stop();
        _shutdown.Cancel();
        StopPeriodic();
        RefreshCommands();
        await _periodicTask;
        await Task.WhenAll(Connections.Where(c => c.Session is not null).Select(c => Task.Run(async () => await c.Session!.DisposeAsync())));
        if (_initialized && !_loadFailed)
            await SaveAsync();
    }
}
