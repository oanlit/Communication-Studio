using System.Buffers.Binary;

namespace Communication.Core;

public enum ModbusRole
{
    Master, Slave
}
public enum ModbusTransport
{
    Tcp, Rtu
}

public sealed class ModbusTrafficRecord
{
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.Now;
    public ModbusRole Role
    {
        get;
    }
    public TrafficDirection Direction
    {
        get;
    }
    public string Endpoint
    {
        get;
    }
    public int Length
    {
        get;
    }
    public string Hex
    {
        get;
    }
    public bool IsComplete
    {
        get;
    }
    public ushort? Transaction
    {
        get;
    }
    public byte? Unit
    {
        get;
    }
    public byte? Function
    {
        get;
    }
    public ModbusTransport Transport
    {
        get;
    }
    public bool? ValidCrc
    {
        get;
    }
    public ModbusTrafficRecord(ModbusRole role, TrafficDirection direction, string endpoint, ReadOnlySpan<byte> data, bool complete = true, ModbusTransport transport = ModbusTransport.Tcp)
    {
        Role = role;
        Direction = direction;
        Endpoint = endpoint;
        Length = data.Length;
        Hex = PayloadCodec.ToHex(data);
        IsComplete = complete;
        Transport = transport;
        Transaction = transport == ModbusTransport.Tcp && data.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(data) : null;
        Unit = data.Length > (transport == ModbusTransport.Tcp ? 6 : 0) ? data[transport == ModbusTransport.Tcp ? 6 : 0] : null;
        Function = data.Length > (transport == ModbusTransport.Tcp ? 7 : 1) ? data[transport == ModbusTransport.Tcp ? 7 : 1] : null;
        ValidCrc = transport == ModbusTransport.Rtu ? ModbusRtuCodec.HasValidCrc(data) : null;
    }
}
