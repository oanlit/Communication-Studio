using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO.Ports;
using System.Threading.Channels;
using System.Windows.Threading;
using System.Windows.Data;
using Communication.Core;
using Communication.Transports;

namespace Communication.App.ViewModels;

public sealed class RegisterRow(ushort address, ushort value, bool isBit = false) : ObservableObject
{
    private ushort _value = value;
    private string _editValue = value.ToString(CultureInfo.InvariantCulture);
    private string _updated = "—";
    public ushort Address { get; } = address;
    public bool IsBit { get; } = isBit;
    public string[] BitOptions { get; } = ["0", "1"];
    public ushort Value => _value;
    public string Hex => $"0x{Value:X4}";
    public short Signed => unchecked((short)Value);
    public string Binary => Convert.ToString(Value, 2).PadLeft(16, '0');
    public string Updated => _updated;
    private bool _editValidated;
    private string _editError = "";
    public string EditError
    {
        get => _editError; private set
        {
            Set(ref _editError, value);
            Notify(nameof(HasEditError));
        }
    }
    public bool HasEditError => EditError.Length > 0;
    public string EditValue
    {
        get => _editValue; set
        {
            Set(ref _editValue, value);
            if (_editValidated)
                ValidateEdit();
            Notify(nameof(IsDirty));
        }
    }
    public bool ValidateEdit()
    {
        _editValidated = true;
        try
        {
            ParseEdit(IsBit);
            EditError = "";
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            EditError = IsBit ? $"地址 {Address}：请选择 0 或 1。" : $"地址 {Address}：写入值必须为 0–65535 的整数，或 0x0000–0xFFFF 的十六进制值。";
            return false;
        }
    }
    public bool IsDirty
    {
        get
        {
            try
            {
                return ParseEdit(IsBit) != Value;
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                return true;
            }

        }
    }
    public void RevertEdit() => EditValue = Value.ToString(CultureInfo.InvariantCulture);
    public ushort ParseEdit(bool bits)
    {
        var text = EditValue.Trim();
        var parsed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ushort.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : ushort.Parse(text, CultureInfo.InvariantCulture);
        if (bits && parsed > 1)
            throw new ArgumentException($"地址 {Address} 仅接受 0 或 1。");
        return parsed;
    }
    public void Update(ushort value, bool discardEdits = false)
    {
        var dirty = IsDirty;
        _value = value;
        _updated = DateTime.Now.ToString("HH:mm:ss.fff");
        if (!dirty || discardEdits)
            EditValue = value.ToString(CultureInfo.InvariantCulture);
        Notify(nameof(Value));
        Notify(nameof(Hex));
        Notify(nameof(Signed));
        Notify(nameof(Binary));
        Notify(nameof(Updated));
        Notify(nameof(IsDirty));
    }
}

public sealed class ModbusViewModel : ObservableObject
{
    private readonly ModbusRegisterBank _bank = new();
    private readonly ModbusTcpServer _server;
    private readonly ModbusMasterClient _client = new();
    private readonly ModbusRtuMaster _rtuMaster;
    private readonly ModbusRtuServer _rtuServer;
    private readonly ModbusMasterClient _rtuClient;
    private bool _connectionBusy;
    private ModbusTransport _transport;
    public Array Transports { get; } = Enum.GetValues<ModbusTransport>();
    public ModbusTransport Transport
    {
        get => _transport; set
        {
            if (!CanChangeProtocol)
            {
                Notify();
                return;
            }
            if (!Set(ref _transport, value))
                return;
            foreach (var section in Sections)
            {
                section.MasterRows.Clear();
                section.MasterLoaded = false;
                section.MasterSelected = null;
            }
            Notify(nameof(IsRtu));
            Notify(nameof(IsTcp));
            NotifySection();
            if (IsRtu)
                RefreshPortsCommand.Execute(null);
        }
    }
    public bool IsRtu => Transport == ModbusTransport.Rtu;
    public bool IsTcp => !IsRtu;
    public bool CanChangeProtocol => !_closing && !_connectionBusy && !IsPolling && _requests.CurrentCount == 1 && !IsSlaveRunning && !_rtuMaster.IsConnected;
    public bool CanConfigureSerial => !_closing && !_connectionBusy && !(SlaveMode ? _rtuServer.IsRunning : IsMasterConnected);
    public bool CanSwitchMode => !_closing;
    private sealed class SerialSettings
    {
        public string PortName = "COM1", Baud = "9600", Parity = "Even", Stops = "One", Timeout = "1000", Error = "";
    }
    private readonly SerialSettings _masterSerial = new(), _slaveSerial = new();
    private SerialSettings CurrentSerial => SlaveMode ? _slaveSerial : _masterSerial;
    private string? _masterOpenPort, _slaveOpenPort;
    private readonly Guid _masterConnectionId = Guid.NewGuid(), _rtuSlaveConnectionId = Guid.NewGuid(), _tcpSlaveConnectionId = Guid.NewGuid();
    private ModbusOwnedConnection? _masterOwned, _rtuSlaveOwned, _tcpSlaveOwned;
    public event Action<ModbusOwnedConnection, TrafficRecord>? OwnedTraffic;
    private void PublishOwnedTraffic(ModbusOwnedConnection? owner, TrafficRecord record)
    {
        if (owner is null)
            return;
        owner.Counters.Record(record);
        OwnedTraffic?.Invoke(owner, record);
    }
    private IReadOnlyList<ModbusOwnedConnection> _publishedOwned = [];
    public IReadOnlyList<ModbusOwnedConnection> OwnedConnections
    {
        get
        {
            var result = new List<ModbusOwnedConnection>();
            if (IsMasterConnected && _masterOwned is not null)
                result.Add(_masterOwned);
            if (_rtuServer.IsRunning && _rtuSlaveOwned is not null)
                result.Add(_rtuSlaveOwned);
            if (_server.IsRunning && _tcpSlaveOwned is not null)
                result.Add(_tcpSlaveOwned);
            return result;
        }
    }
    private static ModbusOwnedConnection SerialOwnership(Guid id, ModbusSerialOptions options, string role) => new(new()
    {
        Id = id,
        Name = $"Modbus RTU {role} · {options.PortName}",
        Kind = TransportKind.Serial,
        SerialPort = options.PortName,
        BaudRate = options.BaudRate,
        DataBits = 8,
        Parity = options.Parity.ToString(),
        StopBits = options.StopBits.ToString(),
        Handshake = "None"
    }, $"Modbus {role}");
    public string SerialPortName
    {
        get => CurrentSerial.PortName; set
        {
            CurrentSerial.PortName = value;
            SerialEdited(nameof(SerialPortName));
        }
    }
    public string BaudRate
    {
        get => CurrentSerial.Baud; set
        {
            CurrentSerial.Baud = value;
            SerialEdited(nameof(BaudRate));
        }
    }
    public string SerialParity
    {
        get => CurrentSerial.Parity; set
        {
            CurrentSerial.Parity = value;
            SerialEdited(nameof(SerialParity));
        }
    }
    public string SerialStopBits
    {
        get => CurrentSerial.Stops; set
        {
            CurrentSerial.Stops = value;
            SerialEdited(nameof(SerialStopBits));
        }
    }
    public string ResponseTimeout
    {
        get => CurrentSerial.Timeout; set
        {
            CurrentSerial.Timeout = value;
            SerialEdited(nameof(ResponseTimeout));
        }
    }
    private void SerialEdited(string property)
    {
        CurrentSerial.Error = "";
        Notify(property);
        RefreshCommands();
    }
    public bool SerialPortConflict => IsRtu && (SlaveMode ? IsMasterConnected : _rtuServer.IsRunning) &&
        string.Equals(SerialPortName.Trim(), SlaveMode ? _masterOpenPort : _slaveOpenPort, StringComparison.OrdinalIgnoreCase);
    public string SerialWarning => SerialPortConflict ? "已占有" : CurrentSerial.Error;
    public string[] ParityOptions { get; } = ["None", "Even", "Odd"];
    public string[] StopBitsOptions { get; } = ["One", "Two"];
    public ObservableCollection<string> SerialPorts { get; } = [];
    public AsyncCommand RefreshPortsCommand
    {
        get;
    }
    public AsyncCommand ConnectRtuCommand
    {
        get;
    }
    public AsyncCommand DisconnectRtuCommand
    {
        get;
    }
    public event Action<string>? SerialOpenFailed;
    public bool IsMasterConnected => _rtuMaster.IsConnected;
    public string MasterConnectionStatus => IsMasterConnected ? $"{_masterOpenPort} 已打开 · RTU 主机就绪" : "串口已关闭";
    private bool CanMasterOperate => !IsRtu || IsMasterConnected;
    private ModbusMasterClient ActiveClient => IsRtu ? _rtuClient : _client;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly Channel<ModbusLogRow> _activity = Channel.CreateBounded<ModbusLogRow>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _poll;
    private Task _pollTask = Task.CompletedTask;
    private bool _closing;
    private bool _slaveMode;
    private string _status = "就绪 · Modbus TCP / RTU · 地址采用零基偏移";
    public bool SlaveMode
    {
        get => _slaveMode; set
        {
            if (_closing || !Set(ref _slaveMode, value))
                return;
            StopPolling();
            Notify(nameof(MasterMode));
            Notify(nameof(Rows));
            Notify(nameof(Selected));
            Notify(nameof(SerialPortName));
            Notify(nameof(BaudRate));
            Notify(nameof(SerialParity));
            Notify(nameof(SerialStopBits));
            Notify(nameof(ResponseTimeout));
            if (value && SelectedSection is not null)
                LoadSlave();
            RefreshCommands();
        }
    }
    public bool MasterMode => !SlaveMode;
    public string Host { get; set; } = "127.0.0.1";
    public string Port { get; set; } = "1502";
    public string Unit { get; set; } = "1";
    public string LocalAddress { get; set; } = "127.0.0.1";
    public string LocalPort { get; set; } = "1502";
    public string SlaveUnit { get; set; } = "1";
    public string Start
    {
        get => (SelectedSection?.Configuration.Start ?? 0).ToString(CultureInfo.InvariantCulture); set => ConfigureCurrent(c => c with { Start = ushort.Parse(value, CultureInfo.InvariantCulture) });
    }
    public string Count
    {
        get => (SelectedSection?.Configuration.Count ?? 16).ToString(CultureInfo.InvariantCulture); set => ConfigureCurrent(c => c with { Count = ushort.Parse(value, CultureInfo.InvariantCulture) });
    }
    public string PollInterval { get; set; } = "1000";
    public ModbusTable Table
    {
        get => SelectedSection?.Configuration.Table ?? ModbusTable.HoldingRegisters; set => ConfigureCurrent(c => c with { Table = value });
    }
    public Array Tables { get; } = Enum.GetValues<ModbusTable>();
    public ObservableCollection<RegisterSectionViewModel> Sections { get; } = [];
    private readonly ObservableCollection<RegisterRow> _emptyRows = [];
    private RegisterSectionViewModel? _selectedSection;
    public RegisterSectionViewModel? SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (!Set(ref _selectedSection, value))
                return;
            StopPolling();
            NotifySection();
            if (SlaveMode && value is not null)
                LoadSlave();
        }
    }
    public ObservableCollection<RegisterRow> Rows => SelectedSection is null ? _emptyRows : SlaveMode ? SelectedSection.SlaveRows : SelectedSection.MasterRows;
    public RegisterRow? Selected
    {
        get => SlaveMode ? SelectedSection?.SlaveSelected : SelectedSection?.MasterSelected;
        set
        {
            if (SelectedSection is { } section)
            {
                if (SlaveMode)
                    section.SlaveSelected = value;
                else
                    section.MasterSelected = value;
            }
            Notify();
            RefreshCommands();
        }
    }
    public RelayCommand MasterModeCommand
    {
        get;
    }
    public RelayCommand SlaveModeCommand
    {
        get;
    }
    public ObservableCollection<ModbusLogRow> Logs { get; } = [];
    public RelayCommand ClearLogsCommand
    {
        get;
    }
    private DateTimeOffset _logClearBefore = DateTimeOffset.MinValue;
    private void ClearLogs()
    {
        _logClearBefore = DateTimeOffset.Now;
        SelectedLog = null;
        Logs.Clear();
        foreach (var filter in new[] { LevelFilter, TypeFilter, RoleFilter, EndpointFilter })
            filter.SelectAll(true);
    }
    public ICollectionView LogsView
    {
        get;
    }
    private ModbusLogRow? _selectedLog;
    public ModbusLogRow? SelectedLog
    {
        get => _selectedLog; set
        {
            if (Set(ref _selectedLog, value))
            {
                Notify(nameof(SelectedPacket));
                Notify(nameof(HasSelectedPacket));
            }
        }
    }
    public ModbusPacketDetails? SelectedPacket => SelectedLog?.Packet;
    public bool HasSelectedPacket => SelectedPacket is not null;
    public MultiSelectFilter LevelFilter
    {
        get;
    }
    public MultiSelectFilter TypeFilter
    {
        get;
    }
    public MultiSelectFilter RoleFilter
    {
        get;
    }
    public MultiSelectFilter EndpointFilter
    {
        get;
    }
    private bool _timeAscending;
    public bool TimeAscending => _timeAscending;
    public void ToggleLogTimeSort()
    {
        _timeAscending = !_timeAscending;
        Notify(nameof(TimeAscending));
        using (LogsView.DeferRefresh())
        {
            LogsView.SortDescriptions.Clear();
            LogsView.SortDescriptions.Add(new(nameof(ModbusLogRow.Timestamp), _timeAscending ? ListSortDirection.Ascending : ListSortDirection.Descending));
        }
    }
    private void UpdateLogFilterOptions()
    {
        LevelFilter.UpdateOptions(Logs.Select(row => row.Level));
        TypeFilter.UpdateOptions(Logs.Select(row => row.Type));
        RoleFilter.UpdateOptions(Logs.Select(row => row.Role));
        EndpointFilter.UpdateOptions(Logs.Select(row => row.Endpoint));
    }
    public string Status
    {
        get => _status; private set => Set(ref _status, value);
    }
    public bool IsSlaveRunning => _server.IsRunning || _rtuServer.IsRunning;
    public string SlaveStatus => IsSlaveRunning ? "监听中 · 等待 / 处理主机请求" : "已停止";
    public bool CanConfigureSlave => !IsSlaveRunning && !_connectionBusy;
    public bool IsPolling => _poll is not null;
    public bool CanConfigureMaster => !IsPolling && !_connectionBusy && _requests.CurrentCount == 1;
    public string PollLabel => IsPolling ? "停止轮询" : "开始轮询";
    public AsyncCommand ReadCommand
    {
        get;
    }
    public AsyncCommand WriteSelectedCommand
    {
        get;
    }
    public AsyncCommand WriteTableCommand
    {
        get;
    }
    public AsyncCommand StartSlaveCommand
    {
        get;
    }
    public AsyncCommand StopSlaveCommand
    {
        get;
    }
    public RelayCommand LoadSlaveCommand
    {
        get;
    }
    public RelayCommand ApplySlaveCommand
    {
        get;
    }
    public RelayCommand RevertEditsCommand
    {
        get;
    }
    public string EditValidationSummary => Rows.FirstOrDefault(r => r.HasEditError)?.EditError ?? "";
    public RelayCommand PollCommand
    {
        get;
    }
    public ModbusViewModel(Func<ModbusSerialOptions, IModbusRtuPort>? rtuPortFactory = null)
    {
        _rtuMaster = new(rtuPortFactory);
        LogsView = new ListCollectionView(Logs);
        LevelFilter = new("级别", () => LogsView.Refresh());
        TypeFilter = new("类型", () => LogsView.Refresh());
        RoleFilter = new("角色", () => LogsView.Refresh());
        EndpointFilter = new("对端", () => LogsView.Refresh());
        LogsView.Filter = item => item is ModbusLogRow row && LevelFilter.Matches(row.Level) && TypeFilter.Matches(row.Type) && RoleFilter.Matches(row.Role) && EndpointFilter.Matches(row.Endpoint);
        LogsView.SortDescriptions.Add(new(nameof(ModbusLogRow.Timestamp), ListSortDirection.Descending));
        Logs.CollectionChanged += (_, _) => UpdateLogFilterOptions();
        ClearLogsCommand = new(ClearLogs, () => !_closing);
        Sections.Add(new(new("保持寄存器", ModbusTable.HoldingRegisters, 0, 16)));
        _selectedSection = Sections[0];
        MasterModeCommand = new(() => SlaveMode = false, () => CanSwitchMode);
        SlaveModeCommand = new(() => SlaveMode = true, () => CanSwitchMode);
        _server = new(_bank);
        _server.Activity += message => _activity.Writer.TryWrite(new(DateTimeOffset.Now, "INFO", "从机", "—", "", message, Type: "从机事件"));
        _server.Traffic += record => _activity.Writer.TryWrite(ModbusLogRow.FromTraffic(record));
        _server.RawTraffic += record => PublishOwnedTraffic(_tcpSlaveOwned, record);
        _client.Traffic += record => _activity.Writer.TryWrite(ModbusLogRow.FromTraffic(record));
        _rtuServer = new(_bank, rtuPortFactory);
        _rtuClient = new(_rtuMaster);
        _rtuServer.Activity += message => _activity.Writer.TryWrite(new(DateTimeOffset.Now, "INFO", "从机", "—", _slaveOpenPort ?? "", message, Type: "从机事件"));
        _rtuServer.Traffic += record => _activity.Writer.TryWrite(ModbusLogRow.FromTraffic(record));
        _rtuClient.Traffic += record => _activity.Writer.TryWrite(ModbusLogRow.FromTraffic(record));
        _rtuServer.RawTraffic += record => PublishOwnedTraffic(_rtuSlaveOwned, record);
        _rtuMaster.RawTraffic += record => PublishOwnedTraffic(_masterOwned, record);
        _rtuMaster.Activity += message => _activity.Writer.TryWrite(new(DateTimeOffset.Now, "ERROR", "主机", "—", _masterOpenPort ?? "", message, Type: "异常事件"));
        RefreshPortsCommand = new(RefreshPortsAsync, Error, () => !_closing);
        ConnectRtuCommand = new(ConnectRtuAsync, Error, () => !_closing && !_connectionBusy && MasterMode && IsRtu && !IsMasterConnected && !SerialPortConflict);
        DisconnectRtuCommand = new(DisconnectRtuAsync, Error, () => IsMasterConnected);
        ReadCommand = new(ReadAsync, Error, () => !_closing && MasterMode && CanMasterOperate && !IsPolling && SelectedSection is not null);
        WriteSelectedCommand = new(WriteSelectedAsync, Error, () => CanWrite && Selected is not null);
        WriteTableCommand = new(WriteTableAsync, Error, () => CanWrite);
        StartSlaveCommand = new(StartSlaveAsync, Error, () => !_closing && !_connectionBusy && SlaveMode && !IsSlaveRunning && !SerialPortConflict);
        StopSlaveCommand = new(StopSlaveAsync, Error, () => IsSlaveRunning);
        LoadSlaveCommand = new(() => Guard(LoadSlave), () => !_closing && SlaveMode && SelectedSection is not null);
        ApplySlaveCommand = new(() => Guard(ApplySlave), () => !_closing && SlaveMode && Rows.Count > 0 && Rows.All(r => !r.HasEditError));
        RevertEditsCommand = new(() =>
{
    foreach (var row in Rows)
        row.RevertEdit();
    Log("当前表格未应用的修改已撤销", type: "数据表事件");
}
, () => !_closing && Rows.Any(r => r.IsDirty));
        PollCommand = new(TogglePolling, () => IsPolling || (!_closing && MasterMode && CanMasterOperate && SelectedSection is not null));
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => Drain(), Dispatcher.CurrentDispatcher);
    }
    public RegisterSectionViewModel AddSection(RegisterSectionConfiguration configuration)
    {
        if (Sections.Count >= 32)
            throw new ArgumentException("最多创建 32 个寄存器标签。");
        var section = new RegisterSectionViewModel(configuration);
        Sections.Add(section);
        SelectedSection = section;
        return section;
    }
    public void EditSection(RegisterSectionViewModel section, RegisterSectionConfiguration configuration)
    {
        if (!Sections.Contains(section))
            return;
        StopPolling();
        section.Configure(configuration);
        if (ReferenceEquals(section, SelectedSection))
        {
            NotifySection();
            if (SlaveMode)
                LoadSlave();
        }
    }
    public void RemoveSection(RegisterSectionViewModel section)
    {
        if (!Sections.Contains(section))
            return;
        StopPolling();
        var index = Sections.IndexOf(section);
        Sections.Remove(section);
        if (ReferenceEquals(section, SelectedSection))
            SelectedSection = Sections.Count == 0 ? null : Sections[Math.Min(index, Sections.Count - 1)];
    }
    private void ConfigureCurrent(Func<RegisterSectionConfiguration, RegisterSectionConfiguration> configure)
    {
        if (SelectedSection is { } section)
            EditSection(section, configure(section.Configuration));
    }
    private void NotifySection()
    {
        Notify(nameof(Rows));
        Notify(nameof(Selected));
        Notify(nameof(Table));
        Notify(nameof(Start));
        Notify(nameof(Count));
        RefreshCommands();
    }
    private bool CanWrite => !_closing && MasterMode && CanMasterOperate && !IsPolling && SelectedSection?.MasterLoaded == true && Rows.Count > 0 && Rows.All(r => !r.HasEditError) && Table is ModbusTable.Coils or ModbusTable.HoldingRegisters;
    private (ushort Start, ushort Count) Range(bool local)
    {
        var start = ushort.Parse(Start, CultureInfo.InvariantCulture);
        var count = ushort.Parse(Count, CultureInfo.InvariantCulture);
        var limit = local ? 2000 : Table is ModbusTable.Coils or ModbusTable.DiscreteInputs ? 2000 : 125;
        if (count < 1 || count > limit || start + count > 65536)
            throw new ArgumentException($"数量范围为 1–{limit}，地址范围不能溢出。");
        return (start, count);
    }
    private (string Host, int Port, byte Unit, ModbusTable Table, ushort Start, ushort Count) Request()
    {
        var range = Range(false);
        var port = IsRtu ? 0 : int.Parse(Port, CultureInfo.InvariantCulture);
        var unit = byte.Parse(Unit, CultureInfo.InvariantCulture);
        if ((!IsRtu && (string.IsNullOrWhiteSpace(Host) || port is < 1 or > 65535)) || unit is < 1 or > 247)
            throw new ArgumentException("请检查端点和从站地址 (1–247)。");
        return (Host, port, unit, Table, range.Start, range.Count);
    }
    private async Task ReadAsync() => await ReadRequestAsync(Request(), _shutdown.Token);
    private async Task ReadRequestAsync((string Host, int Port, byte Unit, ModbusTable Table, ushort Start, ushort Count) request, CancellationToken token, RegisterSectionViewModel? target = null)
    {
        var section = target ?? SelectedSection;
        var revision = section?.Revision;
        await _requests.WaitAsync(token);
        RefreshCommands();
        try
        {
            var values = await ActiveClient.ReadAsync(request.Host, request.Port, request.Unit, request.Table, request.Start, request.Count, token);
            if (section is null || !Sections.Contains(section) || section.Revision != revision)
                return;
            UpdateRows(request.Table, false, request.Start, values, section);
            Log($"主机读取 {section.Name} · {request.Start}–{request.Start + request.Count - 1} · 成功", type: "读取事件");
        }
        finally
        {
            _requests.Release();
            RefreshCommands();
        }

    }
    private void UpdateRows(ModbusTable table, bool slave, ushort start, ushort[] values, RegisterSectionViewModel? target = null)
    {
        var section = target ?? SelectedSection;
        if (section is null)
            return;
        var rows = slave ? section.SlaveRows : section.MasterRows;
        if (rows.Count != values.Length || rows.FirstOrDefault()?.Address != start)
        {
            rows.Clear();
            for (var i = 0; i < values.Length; i++)
            {
                var row = new RegisterRow((ushort)(start + i), values[i], table is ModbusTable.Coils or ModbusTable.DiscreteInputs);
                row.PropertyChanged += (_, e) =>
{
    if (e.PropertyName is nameof(RegisterRow.IsDirty) or nameof(RegisterRow.HasEditError))
        RefreshCommands();
}
;
                rows.Add(row);
            }
            if (slave)
                section.SlaveSelected = rows.FirstOrDefault();
            else
                section.MasterSelected = rows.FirstOrDefault();
        }
        if (slave)
            section.SlaveLoaded = true;
        else
            section.MasterLoaded = true;
        for (var i = 0; i < values.Length; i++)
            rows[i].Update(values[i]);
        Notify(nameof(Selected));
        RefreshCommands();
    }
    private void ValidateLoadedTable()
    {
        var range = Range(SlaveMode);
        if (SelectedSection is null || !(SlaveMode ? SelectedSection.SlaveLoaded : SelectedSection.MasterLoaded) || Rows.Count != range.Count || Rows.FirstOrDefault()?.Address != range.Start)
            throw new InvalidOperationException("数据表或范围已改变，请先读取 / 加载表格。");
    }
    private async Task WriteSelectedAsync()
    {
        ValidateLoadedTable();
        var request = Request();
        var row = Selected!;
        var value = row.ParseEdit(Table == ModbusTable.Coils);
        await _requests.WaitAsync(_shutdown.Token);
        RefreshCommands();
        try
        {
            await ActiveClient.WriteSingleAsync(request.Host, request.Port, request.Unit, request.Table, row.Address, value, _shutdown.Token);
            row.Update(value, true);
            Log($"主机写入地址 {row.Address} = {value} · 确认成功", type: "写入事件");
        }
        finally
        {
            _requests.Release();
            RefreshCommands();
        }

    }
    private async Task WriteTableAsync()
    {
        ValidateLoadedTable();
        var request = Request();
        var rows = Rows.ToArray();
        var values = rows.Select(r => r.ParseEdit(Table == ModbusTable.Coils)).ToArray();
        await _requests.WaitAsync(_shutdown.Token);
        RefreshCommands();
        try
        {
            await ActiveClient.WriteMultipleAsync(request.Host, request.Port, request.Unit, request.Table, request.Start, values, _shutdown.Token);
            for (var i = 0; i < values.Length; i++)
                rows[i].Update(values[i], true);
            Log($"主机批量写入 {values.Length} 项 · 确认成功", type: "写入事件");
        }
        finally
        {
            _requests.Release();
            RefreshCommands();
        }

    }
    private ModbusSerialOptions SerialOptions() => new(SerialPortName.Trim(), int.Parse(BaudRate, CultureInfo.InvariantCulture), Enum.Parse<Parity>(SerialParity), Enum.Parse<StopBits>(SerialStopBits), int.Parse(ResponseTimeout, CultureInfo.InvariantCulture));
    private async Task RefreshPortsAsync()
    {
        var ports = await Task.Run(() => SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray(), _shutdown.Token);
        var settings = CurrentSerial;
        var entered = settings.PortName;
        SerialPorts.Clear();
        foreach (var port in ports)
            SerialPorts.Add(port);
        settings.PortName = entered;
        Notify(nameof(SerialPortName));
    }
    private async Task ConnectRtuAsync()
    {
        var settings = _masterSerial;
        _connectionBusy = true;
        RefreshCommands();
        try
        {
            var options = SerialOptions();
            _masterOwned = SerialOwnership(_masterConnectionId, options, "主机");
            await _rtuMaster.ConnectAsync(options, _shutdown.Token);
            _masterOpenPort = options.PortName;
            settings.Error = "";
            Log($"RTU 主机串口已打开：{options.PortName} · {options.BaudRate} · 8/{options.Parity}/{options.StopBits}", type: "连接事件");
        }
        catch (Exception ex)
        {
            settings.Error = ex is UnauthorizedAccessException ? "已占有" : "";
            if (ex is not OperationCanceledException)
                SerialOpenFailed?.Invoke($"无法打开串口 {settings.PortName}。\n\n{ex.Message}");
            throw;
        }

        finally
        {
            _connectionBusy = false;
            RefreshCommands();
        }

    }
    private async Task DisconnectRtuAsync()
    {
        _connectionBusy = true;
        RefreshCommands();
        try
        {
            StopPolling();
            await _rtuMaster.DisconnectAsync();
            Log("RTU 主机串口已关闭", type: "连接事件");
        }
        finally
        {
            _connectionBusy = false;
            RefreshCommands();
        }

    }
    private async Task StartSlaveAsync()
    {
        var settings = _slaveSerial;
        _connectionBusy = true;
        RefreshCommands();
        try
        {
            var unit = byte.Parse(SlaveUnit, CultureInfo.InvariantCulture);
            if (IsRtu)
            {
                var options = SerialOptions();
                _rtuSlaveOwned = SerialOwnership(_rtuSlaveConnectionId, options, "从机");
                await _rtuServer.StartAsync(options, unit, _shutdown.Token);
                _slaveOpenPort = options.PortName;
                settings.Error = "";
                Log($"RTU 从机监听 {options.PortName} · Unit {unit}", type: "监听事件");
            }
            else
            {
                var address = LocalAddress;
                var port = int.Parse(LocalPort, CultureInfo.InvariantCulture);
                _tcpSlaveOwned = new(new()
                {
                    Id = _tcpSlaveConnectionId,
                    Name = $"Modbus TCP 从机 · {address}:{port}",
                    Kind = TransportKind.TcpServer,
                    LocalAddress = address,
                    LocalPort = port
                }, "Modbus 从机");
                await _server.StartAsync(address, port, unit, _shutdown.Token);
                Log($"从机监听 {address}:{port} · Unit {unit}", type: "监听事件");
            }
        }
        catch (Exception ex)
        {
            if (IsRtu)
            {
                settings.Error = ex is UnauthorizedAccessException ? "已占有" : "";
                if (ex is not OperationCanceledException)
                    SerialOpenFailed?.Invoke($"无法打开串口 {settings.PortName}。\n\n{ex.Message}");
            }
            throw;
        }

        finally
        {
            _connectionBusy = false;
            RefreshCommands();
        }

    }
    private async Task StopSlaveAsync()
    {
        _connectionBusy = true;
        RefreshCommands();
        try
        {
            await _server.StopAsync();
            await _rtuServer.StopAsync();
            Log("从机已停止", type: "监听事件");
        }
        finally
        {
            _connectionBusy = false;
            RefreshCommands();
        }

    }
    private void LoadSlave()
    {
        var range = Range(true);
        UpdateRows(Table, true, range.Start, _bank.Read(Table, range.Start, range.Count));
        Log("从机表格已加载；编辑写入值后点击「应用写入值」", type: "数据表事件");
    }
    private void ApplySlave()
    {
        ValidateLoadedTable();
        var values = Rows.Select(r => r.ParseEdit(Table is ModbusTable.Coils or ModbusTable.DiscreteInputs)).ToArray();
        _bank.Set(Table, Rows[0].Address, values);
        for (var i = 0; i < values.Length; i++)
            Rows[i].Update(values[i], true);
        Log($"从机本地应用 {values.Length} 项", type: "数据表事件");
    }
    private void TogglePolling()
    {
        if (IsPolling)
        {
            StopPolling();
            return;
        }
        Guard(() =>
        {
            var request = Request();
            var interval = int.Parse(PollInterval, CultureInfo.InvariantCulture);
            if (interval is < 100 or > 3600000)
                throw new ArgumentException("轮询周期范围为 100–3600000 ms。");
            _poll = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            Log("轮询已启动", type: "轮询事件");
            _pollTask = PollAsync(request, interval, _poll, SelectedSection!);
            RefreshCommands();
        });
    }
    private async Task PollAsync((string Host, int Port, byte Unit, ModbusTable Table, ushort Start, ushort Count) request, int interval, CancellationTokenSource source, RegisterSectionViewModel section)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));
            do
            {
                await ReadRequestAsync(request, source.Token, section);
            } while (await timer.WaitForNextTickAsync(source.Token));
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {

        }

        catch (Exception ex)
        {
            Error(ex);
        }

        finally
        {
            if (ReferenceEquals(_poll, source))
                _poll = null;
            source.Dispose();
            if (!_closing)
                Log("轮询已停止", type: "轮询事件");
            RefreshCommands();
        }

    }
    private void StopPolling() => _poll?.Cancel();
    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Error(ex);
        }

    }
    private void Error(Exception ex) => Log(ex is OperationCanceledException ? "请求已取消或超时" : ex.Message, "ERROR", ex is OperationCanceledException ? "取消事件" : "异常事件");
    private void Log(string message, string level = "INFO", string type = "操作事件")
    {
        Status = message;
        AppendLog(new(DateTimeOffset.Now, level, SlaveMode ? "从机" : "主机", "—", "", message, Type: type));
    }
    private void AppendLog(ModbusLogRow row)
    {
        if (row.Timestamp <= _logClearBefore)
            return;
        var index = 0;
        while (index < Logs.Count && Logs[index].Timestamp >= row.Timestamp)
            index++;
        Logs.Insert(index, row);
        if (Logs.Count > 300)
            Logs.RemoveAt(Logs.Count - 1);
    }
    private void Drain()
    {
        for (var i = 0; i < 100 && _activity.Reader.TryRead(out var entry); i++)
            AppendLog(entry);
        foreach (var section in Sections.Where(s => s.SlaveLoaded && s.SlaveRows.Count > 0))
        {
            var values = _bank.Read(section.Configuration.Table, section.Configuration.Start, section.SlaveRows.Count);
            for (var i = 0; i < values.Length; i++)
                if (section.SlaveRows[i].Value != values[i])
                    section.SlaveRows[i].Update(values[i]);
        }
        RefreshCommands();
    }
    private void RefreshCommands()
    {
        var owned = OwnedConnections;
        if (!_publishedOwned.SequenceEqual(owned))
        {
            _publishedOwned = owned;
            Notify(nameof(OwnedConnections));
        }
        Notify(nameof(SerialPortConflict));
        Notify(nameof(SerialWarning));
        Notify(nameof(CanChangeProtocol));
        Notify(nameof(CanConfigureSerial));
        Notify(nameof(IsMasterConnected));
        Notify(nameof(MasterConnectionStatus));
        Notify(nameof(CanSwitchMode));
        MasterModeCommand?.Refresh();
        SlaveModeCommand?.Refresh();
        Notify(nameof(EditValidationSummary));
        Notify(nameof(IsSlaveRunning));
        Notify(nameof(SlaveStatus));
        Notify(nameof(CanConfigureSlave));
        Notify(nameof(IsPolling));
        Notify(nameof(CanConfigureMaster));
        Notify(nameof(PollLabel));
        ReadCommand?.Refresh();
        WriteSelectedCommand?.Refresh();
        WriteTableCommand?.Refresh();
        StartSlaveCommand?.Refresh();
        StopSlaveCommand?.Refresh();
        LoadSlaveCommand?.Refresh();
        ApplySlaveCommand?.Refresh();
        PollCommand?.Refresh();
        RevertEditsCommand?.Refresh();
        ConnectRtuCommand?.Refresh();
        DisconnectRtuCommand?.Refresh();
    }
    public async Task ShutdownAsync()
    {
        _closing = true;
        _timer.Stop();
        _shutdown.Cancel();
        StopPolling();
        await _rtuMaster.DisconnectAsync();
        await _pollTask;
        await _server.StopAsync();
        await _rtuServer.StopAsync();
        await _requests.WaitAsync();
        _requests.Release();
        RefreshCommands();
    }
}
