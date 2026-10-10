using System.Windows;

namespace Communication.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Presentation.Appearance.Current.Initialize();
        base.OnStartup(e);
    }
}
