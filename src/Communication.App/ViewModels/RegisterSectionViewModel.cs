using System.Collections.ObjectModel;
using Communication.Core;

namespace Communication.App.ViewModels;

public sealed record RegisterSectionConfiguration(string Name, ModbusTable Table, ushort Start, ushort Count)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 64)
            throw new ArgumentException("标签名称应为 1–64 个字符。");
        var limit = Table is ModbusTable.Coils or ModbusTable.DiscreteInputs ? 2000 : 125;
        if (!Enum.IsDefined(Table) || Count < 1 || Count > limit || Start + Count > 65536)
            throw new ArgumentException($"数量范围为 1–{limit}，末尾地址不能超过 65535。");
    }
}

public sealed class RegisterSectionViewModel : ObservableObject
{
    private RegisterSectionConfiguration _configuration;
    public RegisterSectionConfiguration Configuration => _configuration;
    public string Name => Configuration.Name;
    public string TypeLabel => Configuration.Table switch
    {
        ModbusTable.Coils => "线圈（Coils）",
        ModbusTable.DiscreteInputs => "离散输入（Discrete Inputs）",
        ModbusTable.HoldingRegisters => "保持寄存器（Holding Registers）",
        ModbusTable.InputRegisters => "输入寄存器（Input Registers）",
        _ => Configuration.Table.ToString()
    };
    public string Description => $"{TypeLabel} · 起始地址 {Configuration.Start} · {Configuration.Count} 项（零基偏移）";
    public ObservableCollection<RegisterRow> MasterRows { get; } = [];
    public ObservableCollection<RegisterRow> SlaveRows { get; } = [];
    public RegisterRow? MasterSelected
    {
        get; set;
    }
    public RegisterRow? SlaveSelected
    {
        get; set;
    }
    public bool MasterLoaded
    {
        get; set;
    }
    public bool SlaveLoaded
    {
        get; set;
    }
    public int Revision
    {
        get; private set;
    }
    public RegisterSectionViewModel(RegisterSectionConfiguration configuration)
    {
        configuration.Validate();
        _configuration = configuration;
    }
    public void Configure(RegisterSectionConfiguration configuration)
    {
        configuration.Validate();
        var rangeChanged = configuration.Table != Configuration.Table || configuration.Start != Configuration.Start || configuration.Count != Configuration.Count;
        _configuration = configuration;
        if (rangeChanged)
        {
            Revision++;
            MasterRows.Clear();
            SlaveRows.Clear();
            MasterLoaded = false;
            SlaveLoaded = false;
            MasterSelected = null;
            SlaveSelected = null;
        }
        Notify(nameof(Name));
        Notify(nameof(TypeLabel));
        Notify(nameof(Description));
        Notify(nameof(Configuration));
    }
}
