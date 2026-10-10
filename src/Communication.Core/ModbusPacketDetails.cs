using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Communication.Core;

public sealed record ModbusSemanticField(string Meaning, string Decimal, string Binary)
{
    private const string NotePattern = @"[（(]([^）)]*)[）)]";
    public string Field { get; } = Regex.Replace(Meaning, NotePattern, "").Trim();
    public string? Description { get; } = GetDescription(Meaning);
    private static string? GetDescription(string meaning)
    {
        var notes = string.Join("\n", Regex.Matches(meaning, NotePattern).Select(match => match.Groups[1].Value));
        return notes.Length == 0 ? null : notes;
    }
}

public sealed record ModbusPacketDetails(string Hex, string Binary, string Summary, IReadOnlyList<ModbusSemanticField> Fields)
{
    public static ModbusPacketDetails Decode(ModbusTrafficRecord record)
    {
        var bytes = PayloadCodec.Parse(record.Hex, true);
        var fields = new List<ModbusSemanticField>();
        var warnings = new List<string>();
        var offset = 0;
        var crcPresent = record.Transport == ModbusTransport.Rtu && record.IsComplete && bytes.Length >= 4;
        var end = bytes.Length - (crcPresent ? 2 : 0);
        bool request = record.Role == ModbusRole.Master ? record.Direction == TrafficDirection.Tx : record.Direction == TrafficDirection.Rx;
        if (!record.IsComplete)
            warnings.Add("不完整报文，仅解释已收到的字节");
        if (record.Transport == ModbusTransport.Rtu && record.ValidCrc == false)
            warnings.Add("CRC 校验失败 / CRC 不完整");

        int? Take(string meaning, int length, bool littleEndian = false)
        {
            var available = Math.Min(length, Math.Max(0, end - offset));
            if (available == 0)
            {
                warnings.Add($"缺少{meaning}");
                return null;
            }
            var data = bytes.AsSpan(offset, available).ToArray();
            offset += available;
            var full = available == length;
            if (!full)
            {
                meaning += $"（截断：{available}/{length} 字节）";
                warnings.Add("字段截断");
            }
            var binary = string.Join(" ", data.Select(value => Convert.ToString(value, 2).PadLeft(8, '0')));
            int? number = available <= 2 ? available == 1 ? data[0] : littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(data) : BinaryPrimitives.ReadUInt16BigEndian(data) : null;
            fields.Add(new(meaning, number?.ToString(CultureInfo.InvariantCulture) ?? string.Join(" ", data.Select(value => value.ToString(CultureInfo.InvariantCulture))), binary));
            return full ? number : null;
        }
        void Remainder(string meaning)
        {
            if (offset < end)
                Take(meaning, end - offset);
        }
        void Values(int size, int? declaredCount, bool bits, string label, int? bitCount = null)
        {
            var actual = Math.Min(declaredCount ?? 0, end - offset);
            if (declaredCount is null)
            {
                Remainder("未解析数据");
                return;
            }
            if (actual != declaredCount)
                warnings.Add("数据长度与字节数不一致");
            var limit = offset + actual;
            var index = 0;
            while (offset < limit)
            {
                var available = Math.Min(size, limit - offset);
                var lastBit = bitCount is { } quantity ? Math.Min(index * 8 + 7, quantity - 1) : index * 8 + 7;
                var meaning = bits ? $"{label}位组 [{index * 8}–{lastBit}]（bit0 优先{(bitCount is not null && lastBit < index * 8 + 7 ? "，高位填充" : "")}）" : $"{label} [{index}]（响应 / 数据序号）";
                var savedEnd = end;
                end = limit;
                Take(meaning, size);
                end = savedEnd;
                if (available < size)
                    warnings.Add("寄存器数据缺少一个字节");
                index++;
            }
        }
        if (record.Transport == ModbusTransport.Tcp)
        {
            Take("事务标识", 2);
            var protocol = Take("协议标识（Modbus=0）", 2);
            var length = Take("MBAP 长度（Unit + PDU）", 2);
            if (protocol is not null and not 0)
                warnings.Add("协议标识非 0");
            if (length is { } count && count + 6 != bytes.Length)
                warnings.Add("MBAP 长度与报文长度不一致");
        }
        Take(record.Transport == ModbusTransport.Rtu ? "从站地址（0=广播）" : "Unit ID", 1);
        var function = offset < end ? bytes[offset] : (byte?)null;
        var name = FunctionName((byte)((function ?? 0) & 0x7F));
        Take($"功能码（{(function >= 0x80 ? "异常：" : "")}{name}）", 1);
        if (function is >= 0x80)
        {
            var exception = offset < end ? bytes[offset] : (byte?)null;
            Take($"异常码（{ExceptionName(exception)}）", 1);
        }
        else if (function is >= 1 and <= 4 && request)
        {
            Take("起始地址（零基偏移）", 2);
            Take(function <= 2 ? "读取位数量" : "读取寄存器数量", 2);
        }
        else if (function is >= 1 and <= 4)
        {
            var count = Take("数据字节数", 1);
            Values(function <= 2 ? 1 : 2, count, function <= 2, function switch
            {
                1 => "线圈",
                2 => "离散输入",
                3 => "保持寄存器",
                _ => "输入寄存器"
            });
        }
        else if (function is 5 or 6)
        {
            Take("写入地址（零基偏移）", 2);
            Take(function == 5 ? "线圈写入值（65280=1，0=0）" : "寄存器写入值", 2);
        }
        else if (function is 15 or 16)
        {
            Take("起始地址（零基偏移）", 2);
            var count = Take(function == 15 ? "写入线圈数量" : "写入寄存器数量", 2);
            if (request)
            {
                var size = Take("数据字节数", 1);
                if (count is { } quantity && size is { } byteCount && byteCount != (function == 15 ? (quantity + 7) / 8 : quantity * 2))
                    warnings.Add("写入数量与数据字节数不一致");
                Values(function == 15 ? 1 : 2, size, function == 15, function == 15 ? "线圈" : "保持寄存器", function == 15 ? count : null);
            }
        }
        Remainder(function is null || name == "未知功能" ? "未解析数据" : "额外数据");
        if (crcPresent)
        {
            end = bytes.Length;
            Take("CRC16（低字节在前）", 2, true);
        }
        var summary = $"{(record.Transport == ModbusTransport.Tcp ? "Modbus TCP" : "Modbus RTU")} · {(request ? "请求" : "响应")} · {name}";
        if (warnings.Count > 0)
            summary += " · " + string.Join("；", warnings.Distinct());
        return new(record.Hex, string.Join(", ", fields.Select(field => field.Binary)), summary, fields.ToArray());
    }
    private static string FunctionName(byte function) => function switch
    {
        1 => "读线圈",
        2 => "读离散输入",
        3 => "读保持寄存器",
        4 => "读输入寄存器",
        5 => "写单线圈",
        6 => "写单寄存器",
        15 => "写多线圈",
        16 => "写多寄存器",
        _ => "未知功能"
    };
    private static string ExceptionName(byte? code) => code switch
    {
        1 => "非法功能",
        2 => "非法地址",
        3 => "非法数据值",
        4 => "设备故障",
        5 => "确认",
        6 => "设备忙",
        10 => "网关路径不可用",
        11 => "目标设备未响应",
        _ => "未知 / 不完整"
    };
}
