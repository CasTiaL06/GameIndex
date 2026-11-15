using GameIndex.Core;
using GameIndex.Services;
using Microsoft.UI.Xaml;

namespace GameIndex;

public partial class App : Application
{
    private MainWindow? _window;
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
        Taskbar.SetAppId();                       // taskbar button, pinning and jump list all belong to "GameIndex.Launcher"
        var host = AppHost.Create();

        // One instance per GameIndex folder (two trackers on one database would double count sessions)
        var name = "GameIndex." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(host.Paths.Root.ToLowerInvariant())))[..16];
        var pipe = name + ".pipe";
        var command = InstanceBridge.FromArgs(Environment.GetCommandLineArgs());
        _single = new Mutex(true, name, out var first);
        if (!first)
        {
            InstanceBridge.TrySend(pipe, command);   // e.g. a jump-list click: hand it to the running instance, then exit
            Environment.Exit(0);
            return;
        }

        var minimized = Environment.GetCommandLineArgs().Contains("--minimized") && host.Settings.Current.MinimizeToTray;
        var background = minimized || command.StartsWith("launch:");   // launching a game from the jump list should not pop the window up
        _window = new MainWindow(host, startHidden: background && host.Settings.Current.MinimizeToTray, initialCommand: command.StartsWith("launch:") || command == "scan" ? command : null);
        if (!(background && host.Settings.Current.MinimizeToTray)) _window.Activate();
        InstanceBridge.StartServer(pipe, msg => _window.HandleCommand(msg));
    }
}
