using System.Windows;
using SGL.Core;
namespace SGL.Desktop;

public partial class App : Application
{
    SingleInstanceActivation? activation;
    protected override async void OnStartup(StartupEventArgs e)
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
            if (Array.IndexOf(e.Args, "--shared-link") >= 0)
            {
                MessageBox.Show("Map and gamemode links are no longer supported. Browse Community in SGL instead.", "SGL");
                Shutdown();
                return;
            }
            int dataIndex = Array.IndexOf(e.Args, "--data");
            string data = dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? e.Args[dataIndex + 1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SGL");
            activation = new SingleInstanceActivation(data);
            if (!activation.IsFirst)
            {
                await activation.Forward();
                Shutdown();
                return;
            }
            var window = new MainWindow(e.Args);
            window.Show();
            _ = activation.Listen(() => Dispatcher.BeginInvoke(() => { window.Show(); if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; window.Activate(); }));
        }
        catch (Exception error) { MessageBox.Show(error.ToString(), "SGL could not start"); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        activation?.Dispose();
        base.OnExit(e);
    }
}
