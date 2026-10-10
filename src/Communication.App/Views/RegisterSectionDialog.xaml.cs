using System.Globalization;
using System.Windows;
using Communication.App.ViewModels;
using Communication.Core;
using Communication.App.Presentation;

namespace Communication.App.Views;

public partial class RegisterSectionDialog : Window
{
    public RegisterSectionConfiguration? Result
    {
        get; private set;
    }
    public RegisterSectionDialog(RegisterSectionConfiguration? configuration = null)
    {
        InitializeComponent();
        TypeField.ItemsSource = Enum.GetValues<ModbusTable>();
        var defaults = configuration ?? new(Translation.Text("寄存器分区"), ModbusTable.HoldingRegisters, 0, 16);
        NameField.Text = defaults.Name;
        TypeField.SelectedItem = defaults.Table;
        StartField.Text = defaults.Start.ToString(CultureInfo.InvariantCulture);
        CountField.Text = defaults.Count.ToString(CultureInfo.InvariantCulture);
        Translation.Bind(DialogHeading, System.Windows.Controls.TextBlock.TextProperty, configuration is null ? "新建寄存器标签" : "编辑寄存器标签");
        Loaded += (_, _) =>
{
    NameField.Focus();
    NameField.SelectAll();
}
;
    }
    private void ConfirmClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = new RegisterSectionConfiguration(NameField.Text.Trim(), (ModbusTable)TypeField.SelectedItem,
                ushort.Parse(StartField.Text, CultureInfo.InvariantCulture), ushort.Parse(CountField.Text, CultureInfo.InvariantCulture));
            result.Validate();
            Result = result;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            Translation.Bind(ErrorText, System.Windows.Controls.TextBlock.TextProperty, ex is FormatException or OverflowException ? "起始地址和数量必须是有效的整数。" : ex.Message);
        }

    }
}
