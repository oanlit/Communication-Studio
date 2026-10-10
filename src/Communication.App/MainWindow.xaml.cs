using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Communication.App.ViewModels;
using Communication.App.Presentation;
using Communication.App.Views;

namespace Communication.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private bool _closed;
    private bool _closing;
    private ChecksumWindow? _checksumWindow;
    private DataConverterWindow? _dataConverterWindow;
    private readonly HashSet<ValidationError> _validationErrors = [];
    public MainWindow() : this(new ShellViewModel())
    {

    }

    public MainWindow(ShellViewModel shell)
    {
        _shell = shell;
        InitializeComponent();
        DataContext = _shell;
        WindowWorkArea.Attach(this);
        _shell.ChecksumRequested += OpenChecksum;
        _shell.DataConverterRequested += OpenDataConverter;
        Closed += (_, _) =>
{
    _shell.ChecksumRequested -= OpenChecksum;
    _shell.DataConverterRequested -= OpenDataConverter;
}
;
        AddHandler(Validation.ErrorEvent, new EventHandler<ValidationErrorEventArgs>((_, e) =>
        {
            if (e.Action == ValidationErrorEventAction.Added)
                _validationErrors.Add(e.Error);
            else
                _validationErrors.Remove(e.Error);
            _shell.Workspace.HasInputErrors = _validationErrors.Count > 0;
            _shell.Workspace.Status = _shell.Workspace.HasInputErrors ? "请修正标红的数字字段。" : "输入已修正。";
            _shell.Workspace.RefreshCommands();
        }));
        Loaded += async (_, _) => await _shell.Workspace.InitializeAsync();
        Closing += OnClosing;
    }
    private void OpenChecksum()
    {
        if (_closing)
            return;
        if (_checksumWindow is null)
        {
            _checksumWindow = new ChecksumWindow(_shell);
            _checksumWindow.Closed += (_, _) => _checksumWindow = null;
            _checksumWindow.Show();
        }
        else
        {
            if (_checksumWindow.WindowState == WindowState.Minimized)
                _checksumWindow.WindowState = WindowState.Normal;
            _checksumWindow.Activate();
        }
    }
    private void OpenDataConverter()
    {
        if (_closing)
            return;
        if (_dataConverterWindow is null)
        {
            _dataConverterWindow = new DataConverterWindow(_shell);
            _dataConverterWindow.Closed += (_, _) => _dataConverterWindow = null;
            _dataConverterWindow.Show();
        }
        else
        {
            if (_dataConverterWindow.WindowState == WindowState.Minimized)
                _dataConverterWindow.WindowState = WindowState.Normal;
            _dataConverterWindow.Activate();
        }
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void AboutClick(object sender, RoutedEventArgs e) => MessageBox.Show(this, Translation.Text("Communication Studio 0.2\n端点管理 · 校验码计算 · Modbus TCP / RTU 主从机\nC# / .NET 10 / WPF"), Translation.Text("关于"), MessageBoxButton.OK, MessageBoxImage.Information);
    private void DarkThemeClick(object sender, RoutedEventArgs e) => SetAppearance("Dark", Appearance.Current.Language);
    private void LightThemeClick(object sender, RoutedEventArgs e) => SetAppearance("Light", Appearance.Current.Language);
    private void ChineseClick(object sender, RoutedEventArgs e) => SetAppearance(Appearance.Current.Theme, "zh-CN");
    private void EnglishClick(object sender, RoutedEventArgs e) => SetAppearance(Appearance.Current.Theme, "en-US");
    private void SetAppearance(string theme, string language)
    {
        try
        {
            Appearance.Current.Apply(theme, language);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, Appearance.Current.IsChinese ? $"切换已生效，但无法保存设置。\n{ex.Message}" : $"The change was applied, but preferences could not be saved.\n{ex.Message}", "Communication Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed)
            return;
        e.Cancel = true;
        if (_closing)
            return;
        _closing = true;
        IsEnabled = false;
        _checksumWindow?.Close();
        _dataConverterWindow?.Close();
        try
        {
            await _shell.ShutdownAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Translation.Text(ex.Message), Translation.Text("工作区关闭提醒"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        finally
        {
            _closed = true;
            Close();
        }

    }
}
