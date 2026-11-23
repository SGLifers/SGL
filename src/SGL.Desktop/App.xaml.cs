using System.Windows;
using SGL.Core;
namespace SGL.Desktop;

public partial class App : Application
{
    SharedLinkActivation? activation;
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
            int dataIndex = Array.IndexOf(e.Args, "--data"), linkIndex = Array.IndexOf(e.Args, "--shared-link");
            string data = dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? e.Args[dataIndex + 1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SGL");
            string link = linkIndex >= 0 && linkIndex + 1 < e.Args.Length ? e.Args[linkIndex + 1] : "";
            if (linkIndex >= 0 && (linkIndex != 0 || e.Args.Length != 2 || link.Length > 4096))
                throw new InvalidDataException("Invalid SGL link invocation.");
            activation = new SharedLinkActivation(data);
            if (!activation.IsFirst)
            {
                await activation.Forward(link);
                Shutdown();
                return;
            }
            var window = new MainWindow(e.Args);
            window.Show();
            _ = activation.Listen(message => Dispatcher.BeginInvoke(() => { window.Show(); if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; window.Activate(); if (message.Length > 0) window.AcceptSharedLink(message); }));
        }
        catch (Exception error) { MessageBox.Show(error.ToString(), "SGL could not start"); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        activation?.Dispose();
        base.OnExit(e);
    }
}
