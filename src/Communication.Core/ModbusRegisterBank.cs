namespace Communication.Core;

public enum ModbusTable
{
    Coils = 1, DiscreteInputs = 2, HoldingRegisters = 3, InputRegisters = 4
}

public sealed class ModbusRegisterBank
{
    private readonly object _gate = new();
    private readonly ushort[][] _tables = [new ushort[65536], new ushort[65536], new ushort[65536], new ushort[65536]];
    public ushort[] Read(ModbusTable table, ushort start, int count)
    {
        Validate(table, start, count);
        lock (_gate)
            return _tables[(int)table - 1].AsSpan(start, count).ToArray();
    }
    // Simulator edits may update input tables; protocol writes cannot.
    public void Set(ModbusTable table, ushort start, ReadOnlySpan<ushort> values)
    {
        Validate(table, start, values.Length);
        if (table is ModbusTable.Coils or ModbusTable.DiscreteInputs)
            foreach (var value in values)
                if (value > 1)
                    throw new ArgumentException("位表仅接受 0 或 1。");
        lock (_gate)
            values.CopyTo(_tables[(int)table - 1].AsSpan(start));
    }
    private static void Validate(ModbusTable table, ushort start, int count)
    {
        if (!Enum.IsDefined(table) || count < 1 || start + count > 65536)
            throw new ArgumentOutOfRangeException(nameof(count), "地址范围无效。");
    }
}
