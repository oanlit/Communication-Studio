using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace Communication.App.Presentation;

/// <summary>Refresh local NIC addresses when an editable IP picker opens, without blocking the UI.</summary>
public static class LocalNetworkAddresses
{
    private sealed class PickerState
    {
        public IpAddressPreset[]? Presets;
        public int Revision;
    }
    private static readonly ConditionalWeakTable<ComboBox, PickerState> States = new();
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(LocalNetworkAddresses), new PropertyMetadata(false, EnabledChanged));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    private static void EnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ComboBox picker)
            return;
        if ((bool)e.NewValue)
        {
            picker.DropDownOpened += Opened;
            picker.DropDownClosed += Closed;
            picker.Unloaded += Unloaded;
        }
        else
        {
            picker.DropDownOpened -= Opened;
            picker.DropDownClosed -= Closed;
            picker.Unloaded -= Unloaded;
            States.Remove(picker);
        }
    }
    private static void Closed(object? sender, EventArgs e)
    {
        if (sender is ComboBox picker)
            States.GetOrCreateValue(picker).Revision++;
    }
    private static void Unloaded(object sender, RoutedEventArgs e) => Closed(sender, e);
    private static async void Opened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox picker)
            return;
        var state = States.GetOrCreateValue(picker);
        state.Presets ??= picker.Items.OfType<IpAddressPreset>().ToArray();
        var revision = ++state.Revision;
        try
        {
            var local = await IpAddressPresets.DiscoverLocalAsync();
            if (!picker.IsDropDownOpen || state.Revision != revision || !GetEnabled(picker))
                return;
            var text = picker.Text;
            var selected = picker.SelectedItem;
            var options = state.Presets.Concat(local).Distinct().ToArray();
            picker.SetCurrentValue(ItemsControl.ItemsSourceProperty, options);
            picker.SetCurrentValue(ComboBox.SelectedItemProperty, options.FirstOrDefault(option => Equals(option, selected)));
            picker.SetCurrentValue(ComboBox.TextProperty, text);
        }
        catch (NetworkInformationException)
        { /* Keep presets and manual input available if adapter discovery fails. */

        }

    }
}
