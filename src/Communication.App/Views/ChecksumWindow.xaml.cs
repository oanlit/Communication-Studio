using System.Windows;
using Communication.App.ViewModels;
using Communication.App.Presentation;

namespace Communication.App.Views;

public partial class ChecksumWindow : Window
{
    public ChecksumWindow(ShellViewModel shell)
    {
        InitializeComponent();
        WindowWorkArea.Attach(this);
        DataContext = shell;
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void MinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
}
