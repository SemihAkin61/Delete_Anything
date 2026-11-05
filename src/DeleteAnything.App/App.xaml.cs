using System.Windows;

namespace DeleteAnything.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var win = new MainWindow(e.Args);
        MainWindow = win;
        win.Show();
    }
}
