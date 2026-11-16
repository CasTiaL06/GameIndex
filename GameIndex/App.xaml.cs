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

        // Started with Windows (--minimized) or by a jump-list "launch game" click: stay in the tray / desktop icon instead of popping the window up.
        var startHidden = Environment.GetCommandLineArgs().Contains("--minimized") || command.StartsWith("launch:");
        _window = new MainWindow(host, startHidden, initialCommand: command.StartsWith("launch:") || command == "scan" ? command : null);
        _window.Activate();                    // XAML only loads once the window is activated; a hidden start hides itself in Loaded
        InstanceBridge.StartServer(pipe, msg => _window.HandleCommand(msg));
    }
}
