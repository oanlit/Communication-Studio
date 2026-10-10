using System.Collections.ObjectModel;
using Communication.Core;

namespace Communication.App.ViewModels;

public sealed class ConnectionViewModel : ObservableObject
{
    private ConnectionProfile _profile;
    private ConnectionState _state;
    private string _detail = "等待连接";
    public ConnectionViewModel(ConnectionProfile profile, bool modbusManaged = false)
    {
        _profile = profile;
        IsModbusManaged = modbusManaged;
    }
    public bool IsModbusManaged
    {
        get;
    }
    private string? _occupiedBy;
    public bool IsOccupied => _occupiedBy is not null;
    public void SetOccupiedBy(string? owner)
    {
        if (!Set(ref _occupiedBy, owner, nameof(IsOccupied)))
            return;
        Notify(nameof(StateLabel));
        Notify(nameof(Detail));
        Notify(nameof(CanRemove));
    }
    public void UpdateManagedProfile(ConnectionProfile profile)
    {
        if (!IsModbusManaged || _profile == profile)
            return;
        _profile = profile;
        Notify(null);
    }
    private ICommunicationSession? _session;
    private ModbusTrafficCounters? _modbusCounters;
    public ICommunicationSession? Session
    {
        get => _session; set
        {
            if (ReferenceEquals(_session, value))
                return;
            _session = value;
            _modbusCounters = null;
            Refresh();
        }
    }
    public SessionStatistics Statistics => _modbusCounters?.Snapshot ?? Session?.Statistics ?? new(0, 0, 0, 0);
    public object StatisticsSource => (object?)_modbusCounters ?? Session ?? (object)this;
    public void ObserveModbusCounters(ModbusTrafficCounters counters)
    {
        _modbusCounters = counters;
    }
    public Guid Id => _profile.Id;
    public ConnectionProfile Profile => _profile;
    public string Name
    {
        get => string.IsNullOrWhiteSpace(_profile.Name) ? "" : _profile.Name; set
        {
            _profile = _profile with
            {
                Name = string.IsNullOrWhiteSpace(value) ? "" : value
            };
            Notify();
            Notify(nameof(DisplayName));
        }
    }
    public string DefaultName => $"{Kind switch { TransportKind.Serial => "Serial", TransportKind.Udp => "UDP", TransportKind.TcpServer => "TCP Server", _ => "TCP Client" }} · {Endpoint}";
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? DefaultName : Name;
    private void NotifyIdentity()
    {
        Notify(nameof(Endpoint));
        Notify(nameof(DefaultName));
        Notify(nameof(DisplayName));
    }
    public TransportKind Kind
    {
        get => _profile.Kind; set
        {
            _profile = _profile with
            {
                Kind = value
            };
            Notify();
            Notify(nameof(IsSerial));
            Notify(nameof(IsRemote));
            Notify(nameof(IsLocal));
            Notify(nameof(IsServer));
            Notify(nameof(IsUdp));
            Notify(nameof(IsTcpClient));
            Notify(nameof(LocalEndpoint));
            NotifyIdentity();
        }
    }
    public string Host
    {
        get => _profile.Host; set
        {
            _profile = _profile with
            {
                Host = value
            };
            Notify();
            NotifyIdentity();
        }
    }
    public int Port
    {
        get => _profile.Port; set
        {
            _profile = _profile with
            {
                Port = value
            };
            Notify();
            NotifyIdentity();
        }
    }
    public string LocalAddress
    {
        get => _profile.LocalAddress; set
        {
            _profile = _profile with
            {
                LocalAddress = value
            };
            Notify();
            NotifyIdentity();
            Notify(nameof(LocalEndpoint));
        }
    }
    public int LocalPort
    {
        get => _profile.LocalPort; set
        {
            _profile = _profile with
            {
                LocalPort = value
            };
            Notify();
            NotifyIdentity();
            Notify(nameof(LocalEndpoint));
        }
    }
    public string SerialPort
    {
        get => _profile.SerialPort; set
        {
            _profile = _profile with
            {
                SerialPort = value
            };
            Notify();
            NotifyIdentity();
        }
    }
    public int BaudRate
    {
        get => _profile.BaudRate; set
        {
            _profile = _profile with
            {
                BaudRate = value
            };
            Notify();
        }
    }
    public int DataBits
    {
        get => _profile.DataBits; set
        {
            _profile = _profile with
            {
                DataBits = value
            };
            Notify();
        }
    }
    public string Parity
    {
        get => _profile.Parity; set
        {
            _profile = _profile with
            {
                Parity = value
            };
            Notify();
        }
    }
    public string StopBits
    {
        get => _profile.StopBits; set
        {
            _profile = _profile with
            {
                StopBits = value
            };
            Notify();
        }
    }
    public string Handshake
    {
        get => _profile.Handshake; set
        {
            _profile = _profile with
            {
                Handshake = value
            };
            Notify();
        }
    }
    public bool IsSerial => Kind == TransportKind.Serial;
    public bool IsServer => Kind == TransportKind.TcpServer;
    public bool IsUdp => Kind == TransportKind.Udp;
    public bool IsTcpClient => Kind == TransportKind.TcpClient;
    public string LocalEndpoint => $"{LocalAddress}:{LocalPort}";
    public bool HasPeers => Peers.Count > 0;
    public string PeerHint => HasPeers ? $"{Peers.Count} 个在线客户端；发送仅发送给所选目标。" : "启动监听并等待客户端接入后，即可选择发送目标。";
    public bool IsRemote => Kind is TransportKind.TcpClient or TransportKind.Udp;
    public bool IsLocal => Kind is TransportKind.TcpServer or TransportKind.Udp;
    public string Endpoint => Kind switch { TransportKind.Serial => SerialPort, TransportKind.TcpServer or TransportKind.Udp => LocalEndpoint, _ => $"{Host}:{Port}" };
    public bool CanEdit => !IsModbusManaged && State is ConnectionState.Disconnected or ConnectionState.Faulted;
    public bool CanRemove => !IsModbusManaged && !IsOccupied && State is ConnectionState.Disconnected or ConnectionState.Connected or ConnectionState.Faulted;
    public ConnectionState State
    {
        get => _state; set
        {
            Set(ref _state, value);
            Notify(nameof(CanEdit));
            Notify(nameof(CanRemove));
            Notify(nameof(StateLabel));
        }
    }
    public string StateLabel => IsOccupied ? "已占用" : State switch { ConnectionState.Connected => "ONLINE", ConnectionState.Connecting => "CONNECTING", ConnectionState.Disconnecting => "CLOSING", ConnectionState.Faulted => "FAULT", _ => "OFFLINE" };
    public string Detail
    {
        get => IsOccupied ? $"{_occupiedBy}使用中，请在 Modbus 管理中操作。" : _detail; set => Set(ref _detail, value);
    }
    public ObservableCollection<string> Peers { get; } = [];
    private string? _selectedPeer;
    public string? SelectedPeer
    {
        get => _selectedPeer; set => Set(ref _selectedPeer, value);
    }
    public string Tx => $"{Statistics.TxBytes:N0} B";
    public string Rx => $"{Statistics.RxBytes:N0} B";
    public string Operations => $"{Statistics.TxOperations:N0} TX / {Statistics.RxOperations:N0} RX";
    public void Refresh()
    {
        Notify(nameof(Tx));
        Notify(nameof(Rx));
        Notify(nameof(Operations));
        var peers = (Session as IStreamSession)?.Peers ?? [];
        if (Peers.SequenceEqual(peers))
            return;
        var previous = SelectedPeer;
        Peers.Clear();
        foreach (var peer in peers)
            Peers.Add(peer);
        Notify(nameof(HasPeers));
        Notify(nameof(PeerHint));
        SelectedPeer = previous is not null && Peers.Contains(previous) ? previous : Peers.FirstOrDefault();
    }
    public void Validate()
    {
        if (IsRemote && (string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65535))
            throw new ArgumentException("远端主机不能为空，端口范围为 1–65535。");
        if (IsLocal && (!System.Net.IPAddress.TryParse(LocalAddress, out _) || LocalPort is < 1 or > 65535))
            throw new ArgumentException("请输入有效的本地 IP 和 1–65535 范围内的端口。");
        if (IsSerial && (string.IsNullOrWhiteSpace(SerialPort) || BaudRate <= 0 || DataBits is < 5 or > 8))
            throw new ArgumentException("请检查串口名称、波特率和数据位。");
    }
}
