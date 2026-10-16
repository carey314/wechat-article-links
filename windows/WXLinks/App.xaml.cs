using System.Windows;

namespace WXLinks;

public partial class App : Application
{
    void OnStartup(object sender, StartupEventArgs e)
    {
        var w = new MainWindow();
        MainWindow = w;
        w.Show();
    }
}
