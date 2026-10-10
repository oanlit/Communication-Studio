namespace Communication.App.ViewModels;

public sealed class ShellViewModel : ObservableObject
{
    public WorkspaceViewModel Workspace
    {
        get;
    }
    public DataConverterViewModel DataConverter { get; } = new();
    public ChecksumViewModel Checksum { get; } = new();
    public ModbusViewModel Modbus
    {
        get;
    }
    private string _page = "端点管理";
    public string Page
    {
        get => _page; private set
        {
            Set(ref _page, value);
            Notify(nameof(SelectedPageIndex));
            Notify(nameof(IsConnections));
            Notify(nameof(IsModbus));
            Notify(nameof(Status));
        }
    }
    public int SelectedPageIndex
    {
        get => IsConnections ? 0 : 1; set => Page = value == 1 ? "Modbus 管理" : "端点管理";
    }
    public string Status => IsConnections ? Workspace.Status : Modbus.Status;
    public bool IsConnections => Page == "端点管理";
    public event Action? ChecksumRequested;
    public event Action? DataConverterRequested;
    public bool IsModbus => Page == "Modbus 管理";
    public RelayCommand ConnectionsCommand
    {
        get;
    }
    public RelayCommand ChecksumCommand
    {
        get;
    }
    public RelayCommand DataConverterCommand
    {
        get;
    }
    public RelayCommand ModbusCommand
    {
        get;
    }
    public RelayCommand NewConnectionCommand
    {
        get;
    }
    public ShellViewModel(WorkspaceViewModel? workspace = null, ModbusViewModel? modbus = null)
    {
        Workspace = workspace ?? new();
        Modbus = modbus ?? new();
        Workspace.SetModbusSource(() => Modbus.OwnedConnections);
        Modbus.OwnedTraffic += Workspace.ReceiveModbusTraffic;
        ConnectionsCommand = new(() => Page = "端点管理");
        ChecksumCommand = new(() => ChecksumRequested?.Invoke());
        ModbusCommand = new(() => Page = "Modbus 管理");
        DataConverterCommand = new(() => DataConverterRequested?.Invoke());
        NewConnectionCommand = new(() =>
{
    Workspace.AddCommand.Execute(null);
    Page = "端点管理";
}
);
        Workspace.PropertyChanged += (_, e) =>
{
    if (e.PropertyName == nameof(Workspace.Status))
        Notify(nameof(Status));
}
;
        Checksum.PropertyChanged += (_, e) =>
{
    if (e.PropertyName == nameof(Checksum.Status))
        Notify(nameof(Status));
}
;
        Modbus.PropertyChanged += (_, e) =>
{
    if (e.PropertyName == nameof(Modbus.Status))
        Notify(nameof(Status));
    if (e.PropertyName == nameof(Modbus.OwnedConnections))
        Workspace.SynchronizeModbusConnections();
}
;
    }
    public async Task ShutdownAsync()
    {
        try
        {
            await Modbus.ShutdownAsync();
        }
        finally
        {
            Modbus.OwnedTraffic -= Workspace.ReceiveModbusTraffic;
            await Workspace.ShutdownAsync();
        }

    }
}
