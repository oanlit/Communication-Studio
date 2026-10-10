using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;

namespace Communication.App.Presentation;

public static class Translation
{
    private static readonly Dictionary<string, string> Catalog = Load();
    private static readonly Regex Phrases = new(string.Join("|", Catalog.Keys.OrderByDescending(key => key.Length).Select(Regex.Escape)), RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Dictionary<string, string> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Communication.App.Localization.catalog.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    public static string Text(string source)
    {
        if (string.IsNullOrEmpty(source))
            return source;
        if (Appearance.Current.IsChinese)
            return source switch
            {
                "HoldingRegisters" => "保持寄存器",
                "InputRegisters" => "输入寄存器",
                "DiscreteInputs" => "离散输入",
                "Coils" => "线圈",
                _ => source
            };
        if (Catalog.TryGetValue(source, out var translated))
            return translated;
        // Generated diagnostics have variable numbers/endpoints. Replace known phrases once,
        // preserving those values and never passing user-editable fields through this service.
        return Phrases.Replace(source, match => Catalog[match.Value]);
    }

    public static void Bind(DependencyObject target, DependencyProperty property, string source)
    {
        var binding = new MultiBinding { Converter = LocalizedDisplayConverter.Instance, Mode = BindingMode.OneWay };
        binding.Bindings.Add(new Binding { Source = source });
        binding.Bindings.Add(new Binding(nameof(Appearance.Revision)) { Source = Appearance.Current });
        BindingOperations.SetBinding(target, property, binding);
    }
}
