using System.Collections.ObjectModel;
using Communication.Core;

namespace Communication.App.ViewModels;

public sealed record ChecksumResult(string Algorithm, string Hex, string WireBytes, string Description);
public sealed class ChecksumViewModel : ObservableObject
{
    private string _input = "01 03 00 00 00 02";
    private bool _hexInput = true;
    private string _status = "";
    private byte[] _payload = [];
    private ChecksumResult? _selected;
    public string Input
    {
        get => _input; set
        {
            Set(ref _input, value);
            Calculate();
        }
    }
    public bool HexInput
    {
        get => _hexInput; set
        {
            Set(ref _hexInput, value);
            Calculate();
        }
    }
    public string Status
    {
        get => _status; private set => Set(ref _status, value);
    }
    public ObservableCollection<ChecksumResult> Results { get; } = [];
    public ChecksumResult? Selected
    {
        get => _selected; set
        {
            Set(ref _selected, value);
            Notify(nameof(FramedHex));
        }
    }
    public string FramedHex => Selected is null ? "" : (PayloadCodec.ToHex(_payload) + " " + Selected.WireBytes).Trim();
    public ChecksumViewModel() => Calculate();
    private void Calculate()
    {
        var algorithm = Selected?.Algorithm;
        Results.Clear();
        Selected = null;
        try
        {
            if (Input.Length > 131072)
                throw new ArgumentException("输入过长，请限制在 128 Ki 字符内。");
            _payload = PayloadCodec.Parse(Input, HexInput);
            var crc = Checksums.ModbusCrc16(_payload);
            var ccitt = Checksums.CcittFalse(_payload);
            var crc32 = Checksums.Crc32(_payload);
            Results.Add(new("CRC-16 / Modbus", crc.ToString("X4"), $"{crc & 0xFF:X2} {crc >> 8:X2}", "poly A001 · init FFFF · 低字节先发送"));
            Results.Add(new("CRC-16 / CCITT-FALSE", ccitt.ToString("X4"), $"{ccitt >> 8:X2} {ccitt & 0xFF:X2}", "poly 1021 · init FFFF · 高字节先发送"));
            Results.Add(new("CRC-32 / ISO-HDLC", crc32.ToString("X8"), $"{crc32 >> 24:X2} {(crc32 >> 16) & 0xFF:X2} {(crc32 >> 8) & 0xFF:X2} {crc32 & 0xFF:X2}", "reflected · init/xor FFFFFFFF · 高字节先发送"));
            foreach (var (name, value, description) in new[] { ("LRC / Modbus ASCII", Checksums.Lrc(_payload), "二补数 SUM8；此处输入原始字节"), ("SUM8", Checksums.Sum8(_payload), "字节求和，保留低 8 位"), ("XOR8", Checksums.Xor8(_payload), "逐字节异或") })
                Results.Add(new(name, value.ToString("X2"), value.ToString("X2"), description));
            Selected = Results.FirstOrDefault(r => r.Algorithm == algorithm) ?? Results[0];
            Status = $"{_payload.Length:N0} bytes · 选择算法查看追加校验码后的 HEX";
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            _payload = [];
            Status = ex.Message;
        }

        Notify(nameof(FramedHex));
    }
}
