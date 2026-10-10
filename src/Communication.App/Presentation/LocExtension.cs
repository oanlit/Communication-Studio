using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace Communication.App.Presentation;

/// <summary>Localizes display values, never the underlying navigation, filter or wire data.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public BindingBase Source { get; set; } = new Binding();
    public string? Key
    {
        get; set;
    }
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new MultiBinding { Mode = BindingMode.OneWay, Converter = LocalizedDisplayConverter.Instance, ConverterParameter = Key };
        binding.Bindings.Add(Key is null ? Source : new Binding { Source = Key });
        binding.Bindings.Add(new Binding(nameof(Appearance.Revision)) { Source = Appearance.Current });
        return binding.ProvideValue(serviceProvider);
    }
}

public sealed class LocalizedDisplayConverter : IMultiValueConverter
{
    public static LocalizedDisplayConverter Instance { get; } = new();
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        parameter is string key ? Application.Current.TryFindResource(key) ?? key :
        values.Length == 0 || values[0] == DependencyProperty.UnsetValue ? "" : Translation.Text(values[0]?.ToString() ?? "");
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
