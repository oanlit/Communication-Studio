using System.Windows;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Communication.App.Presentation;
using Communication.Core;
using DataFormat = Communication.Core.DataFormat;

namespace Communication.App.ViewModels;

public sealed record ConversionFormat(DataFormat Format, string Name)
{
    // The shared ComboBox template presents the selected object directly.
    public override string ToString() => Name;
}

public sealed class DataConverterViewModel : ObservableObject
{
    public IReadOnlyList<ConversionFormat> Formats
    {
        get;
    } = Enum.GetValues<DataFormat>().Select(f => new ConversionFormat(f, f switch
    {
        DataFormat.Hex => "HEX",
        DataFormat.Bin => "BIN",
        DataFormat.Dec => "DEC",
        DataFormat.Oct => "OCT",
        DataFormat.Half => "Half / Float16",
        DataFormat.Single => "Single / Float32",
        DataFormat.Double => "Double / Float64",
        DataFormat.Char => "Char / UTF-16",
        DataFormat.Utf8 => "UTF-8",
        DataFormat.Utf16Le => "UTF-16 LE",
        DataFormat.Utf16Be => "UTF-16 BE",
        DataFormat.Utf32 => "UTF-32 LE",
        DataFormat.Ascii => "ASCII",
        DataFormat.Latin1 => "ISO-8859-1",
        DataFormat.Gbk => "GBK",
        DataFormat.Gb18030 => "GB18030",
        _ => f.ToString()
    })).ToArray();
    private string _input = "01 03 00 00 00 02", _output = "", _errorKey = "";
    private object[] _errorArguments = [];
    private ConversionFormat _inputFormat, _outputFormat;
    private bool _inputBigEndian, _outputBigEndian, _busy;
    private int _byteCount, _outputByteCount, _revision;
    public string Input
    {
        get => _input; set
        {
            if (Set(ref _input, value))
            {
                CopyInputCommand.Refresh();
                Convert();
            }
        }
    }
    public string Output
    {
        get => _output; private set
        {
            Set(ref _output, value);
            CopyCommand.Refresh();
            SwapCommand.Refresh();
        }
    }
    public string Error => _errorKey.Length == 0 ? "" : Localize(_errorKey, _errorArguments);
    public bool HasError => _errorKey.Length > 0;
    public string ByteCountText => Localize("UiConverterByteCount", ByteCount);
    public string OutputByteCountText => Localize("UiConverterByteCount", OutputByteCount);
    public int OutputByteCount
    {
        get => _outputByteCount; private set
        {
            if (Set(ref _outputByteCount, value))
                Notify(nameof(OutputByteCountText));
        }
    }
    private static string Localize(string key, params object[] arguments) => string.Format(CultureInfo.CurrentCulture, Application.Current?.TryFindResource(key)?.ToString() ?? key, arguments);
    private void SetError(string key, params object[] arguments)
    {
        _errorKey = key;
        _errorArguments = arguments;
        Notify(nameof(Error));
        Notify(nameof(HasError));
        CopyCommand.Refresh();
        SwapCommand.Refresh();
    }
    private void AppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        Notify(nameof(Error));
        Notify(nameof(ByteCountText));
        Notify(nameof(OutputByteCountText));
    }
    public int ByteCount
    {
        get => _byteCount; private set
        {
            if (Set(ref _byteCount, value))
                Notify(nameof(ByteCountText));
        }
    }
    public bool Busy
    {
        get => _busy; private set
        {
            Set(ref _busy, value);
            CopyCommand.Refresh();
            SwapCommand.Refresh();
        }
    }
    public ConversionFormat InputFormat
    {
        get => _inputFormat; set
        {
            if (value is not null && Set(ref _inputFormat, value))
            {
                Notify(nameof(InputUsesByteOrder));
                Convert();
            }
        }
    }
    public ConversionFormat OutputFormat
    {
        get => _outputFormat; set
        {
            if (value is not null && Set(ref _outputFormat, value))
            {
                Notify(nameof(OutputUsesByteOrder));
                Convert();
            }
        }
    }
    public bool InputBigEndian
    {
        get => _inputBigEndian; set
        {
            if (Set(ref _inputBigEndian, value))
                Convert();
        }
    }
    public bool OutputBigEndian
    {
        get => _outputBigEndian; set
        {
            if (Set(ref _outputBigEndian, value))
                Convert();
        }
    }
    public bool InputUsesByteOrder => DataConversion.UsesByteOrder(InputFormat.Format);
    public bool OutputUsesByteOrder => DataConversion.UsesByteOrder(OutputFormat.Format);
    public RelayCommand ClearCommand
    {
        get;
    }
    public RelayCommand CopyCommand
    {
        get;
    }
    public RelayCommand CopyInputCommand
    {
        get;
    }
    public event Action? CopyCompleted;
    public RelayCommand SwapCommand
    {
        get;
    }

    public DataConverterViewModel()
    {
        _inputFormat = Formats.First(f => f.Format == DataFormat.Hex);
        _outputFormat = Formats.First(f => f.Format == DataFormat.Bin);
        ClearCommand = new(() => Input = "");
        CopyInputCommand = new(() => CopyText(Input), () => Input.Length > 0);
        CopyCommand = new(() => CopyText(Output), () => !Busy && !HasError && Output.Length > 0);
        SwapCommand = new(() =>
        {
            (_inputFormat, _outputFormat) = (_outputFormat, _inputFormat);
            (_inputBigEndian, _outputBigEndian) = (_outputBigEndian, _inputBigEndian);
            _input = Output;
            Notify(nameof(Input));
            CopyInputCommand.Refresh();
            Notify(nameof(InputFormat));
            Notify(nameof(OutputFormat));
            Notify(nameof(InputBigEndian));
            Notify(nameof(OutputBigEndian));
            Notify(nameof(InputUsesByteOrder));
            Notify(nameof(OutputUsesByteOrder));
            Convert();
        }, () => !Busy && !HasError && Output.Length > 0 && InputFormat.Format != DataFormat.Char && OutputFormat.Format != DataFormat.Char);
        PropertyChangedEventManager.AddHandler(Appearance.Current, AppearanceChanged, string.Empty);
        Convert();
    }

    private void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            CopyCompleted?.Invoke();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            SetError("UiConverterClipboardError");
        }

    }

    private async void Convert()
    {
        var revision = ++_revision;
        var input = Input;
        var from = InputFormat.Format;
        var to = OutputFormat.Format;
        var fromBig = InputBigEndian;
        var toBig = OutputBigEndian;
        Busy = true;
        SetError("");
        Output = "";
        ByteCount = 0;
        OutputByteCount = 0;
        // Debounce typing, then work off the dispatcher. Stale results cannot replace newer input.
        await Task.Delay(120);
        if (revision != _revision)
            return;
        try
        {
            var result = await Task.Run(() =>
            {
                byte[]? bytes = null;
                try
                {
                    bytes = DataConversion.Decode(input, from, fromBig);
                    return new ConversionResult(DataConversion.Encode(bytes, to, toBig), bytes.Length, "", []);
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
                {
                    return DescribeError(ex, bytes is null ? from : to, bytes is null, input.Length, bytes?.Length ?? 0);
                }
            });
            if (revision != _revision)
                return;
            ByteCount = result.Count;
            OutputByteCount = result.ErrorKey.Length == 0 ? result.Count : 0;
            Output = result.Text;
            SetError(result.ErrorKey, result.Arguments);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            if (revision == _revision)
                SetError("UiConverterInvalidInput", from);
        }
        finally
        {
            if (revision == _revision)
                Busy = false;
        }

    }
    private sealed record ConversionResult(string Text, int Count, string ErrorKey, object[] Arguments);
    private static ConversionResult DescribeError(Exception error, DataFormat format, bool inputStage, int inputLength, int count)
    {
        ConversionResult Issue(string key, params object[] args) => new("", count, key, args);
        if (inputLength > 131072)
            return Issue("UiConverterInputTooLong");
        if (error is EncoderFallbackException)
            return Issue("UiConverterUnsupportedCharacter", format);
        if (error is DecoderFallbackException)
            return Issue("UiConverterInvalidEncoding", format);
        if (!inputStage && DataConversion.IsScalar(format) && count % DataConversion.Size(format) != 0)
            return Issue("UiConverterInvalidLength", format, DataConversion.Size(format), count);
        if (format == DataFormat.Oct)
            return Issue("UiConverterInvalidOct");
        if (format is DataFormat.Hex or DataFormat.Bin)
            return Issue("UiConverterInvalidRadix", format, format == DataFormat.Hex ? "0–9, A–F" : format == DataFormat.Bin ? "0, 1" : "0–7", format == DataFormat.Hex ? 2 : format == DataFormat.Bin ? 8 : 3);
        if (format == DataFormat.Base64)
            return Issue("UiConverterInvalidBase64");
        if (format == DataFormat.Boolean)
            return Issue(inputStage ? "UiConverterInvalidBooleanInput" : "UiConverterInvalidBooleanBytes");
        if (format == DataFormat.Char)
            return Issue("UiConverterInvalidChar");
        if (format == DataFormat.Decimal && !inputStage)
            return Issue("UiConverterInvalidDecimalBytes");
        if (error is OverflowException)
            return Issue("UiConverterOverflow", format);
        return Issue("UiConverterInvalidInput", format);
    }

}
