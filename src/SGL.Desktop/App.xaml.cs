using System.Windows;
using SGL.Core;
namespace SGL.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] == "--apply-update")
        {
            try
            {
                Updater.Apply(e.Args);
                Shutdown(0);
            }
            catch { Shutdown(1); }
            return;
        }
        DispatcherUnhandledException += (s, args) => { MessageBox.Show(args.Exception.Message, "SGL", MessageBoxButton.OK, MessageBoxImage.Warning); args.Handled = true; };
        try
        {
            new MainWindow(e.Args).Show();
        }
        catch (Exception error) { MessageBox.Show(error.ToString(), "SGL could not start"); Shutdown(1); }
    }
}
