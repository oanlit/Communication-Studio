using System.IO.Ports;

namespace Communication.Transports;

public sealed record ModbusSerialOptions(string PortName, int BaudRate = 9600, Parity Parity = Parity.Even, StopBits StopBits = StopBits.One, int ResponseTimeoutMs = 1000)
{
    public int FrameGapMs => Math.Max(20, (int)Math.Ceiling(3500d * (1 + 8 + (Parity == Parity.None ? 0 : 1) + (StopBits == StopBits.Two ? 2 : 1)) / BaudRate));
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PortName) || BaudRate is < 300 or > 4000000 || !Enum.IsDefined(Parity) || StopBits is not (StopBits.One or StopBits.Two) || ResponseTimeoutMs is < 100 or > 60000)
            throw new ArgumentException("请检查串口名称、波特率 (300–4000000)、停止位与响应超时 (100–60000 ms)。");
    }
}

// Timeout-bounded blocking operations are run exclusively on workers; never use a
// driver's uncancellable BaseStream.ReadAsync on the WPF dispatcher.
public interface IModbusRtuPort : IDisposable
{
    int Read(byte[] buffer, int offset, int count, int timeoutMs);
    void Write(byte[] data);
    void DiscardInput();
}

internal sealed class SerialRtuPort : IModbusRtuPort
{
    private readonly SerialPort _port;
    public SerialRtuPort(ModbusSerialOptions options)
    {
        options.Validate();
        _port = new(options.PortName, options.BaudRate, options.Parity, 8, options.StopBits)
        {
            WriteTimeout = 1000,
            Handshake = Handshake.None
        };
        try
        {
            _port.Open();
        }
        catch
        {
            _port.Dispose();
            throw;
        }

    }
    public int Read(byte[] buffer, int offset, int count, int timeoutMs)
    {
        _port.ReadTimeout = Math.Max(1, timeoutMs);
        try
        {
            return _port.Read(buffer, offset, count);
        }
        catch (TimeoutException)
        {
            return 0;
        }

    }
    public void Write(byte[] data) => _port.Write(data, 0, data.Length);
    public void DiscardInput() => _port.DiscardInBuffer();
    public void Dispose() => _port.Dispose();
}
