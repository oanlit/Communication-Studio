using System.Windows;
using System.Windows.Threading;
using Communication.App.ViewModels;
using Communication.App.Presentation;

namespace Communication.App.Views;

public partial class DataConverterWindow : Window
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    public DataConverterWindow(ShellViewModel shell)
    {
        InitializeComponent();
        WindowWorkArea.Attach(this);
        DataContext = shell.DataConverter;
        shell.DataConverter.CopyCompleted += ShowCopiedToast;
        _toastTimer.Tick += (_, _) =>
{
    _toastTimer.Stop();
    CopiedToast.Visibility = Visibility.Collapsed;
}
;
        Closed += (_, _) =>
{
    _toastTimer.Stop();
    shell.DataConverter.CopyCompleted -= ShowCopiedToast;
}
;
    }
    private void ShowCopiedToast()
    {
        _toastTimer.Stop();
        CopiedToast.Visibility = Visibility.Visible;
        _toastTimer.Start();
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void MinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
}
