using GameIndex.Core;
using Microsoft.UI.Xaml;

namespace GameIndex;

public partial class App : Application
{
    private Window? _window;
    private static Mutex? _single;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => { Log.Error("Unhandled UI exception", e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Fatal exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Unobserved task exception", e.Exception); e.SetObserved(); };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var host = AppHost.Create();

        // One instance per GameIndex folder (two trackers on one database would double count sessions)
        var name = "GameIndex." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(host.Paths.Root.ToLowerInvariant())))[..16];
        _single = new Mutex(true, name, out var first);
        if (!first) { Environment.Exit(0); return; }

        var minimized = Environment.GetCommandLineArgs().Contains("--minimized") && host.Settings.Current.MinimizeToTray;
        _window = new MainWindow(host, startHidden: minimized);
        if (!minimized) _window.Activate();
    }
}
