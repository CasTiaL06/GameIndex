using GameIndex.Core;
using GameIndex.Core.Database;
using GameIndex.Core.Discovery;
using GameIndex.Core.Integrations;
using GameIndex.Core.Launching;
using GameIndex.Core.Services;
using GameIndex.Core.Settings;
using GameIndex.Core.Tracking;

namespace GameIndex;

/// <summary>Composition root: builds and owns all long-lived services.</summary>
public sealed class AppHost : IDisposable
{
    public static AppHost Instance { get; private set; } = null!;

    public AppPaths Paths { get; private init; } = null!;
    public SettingsService Settings { get; private init; } = null!;
    public Db Db { get; private init; } = null!;
    public HttpClient Http { get; private init; } = null!;
    public IReadOnlyList<IPlatformIntegration> Integrations { get; private init; } = null!;
    public ArtworkService Artwork { get; private init; } = null!;
    public DiscoveryService Discovery { get; private init; } = null!;
    public GameInfoService Info { get; private init; } = null!;
    public PlaytimeTracker Tracker { get; private init; } = null!;
    public GameLauncher Launcher { get; private init; } = new();

    public static AppHost Create()
    {
        var paths = new AppPaths();
        Log.Init(paths.LogsDir);
        Log.Info($"GameIndex starting. Root={paths.Root} Portable={paths.IsPortable}");
        if (paths.Warning is not null) Log.Warn(paths.Warning);

        var settings = new SettingsService(paths.SettingsFile);
        GameIndex.Services.Loc.SetLanguage(settings.Current.Language);
        var db = new Db(paths.DatabaseFile, paths.BackupsDir, paths.Root);
        db.Initialize();
        db.AutoBackup(settings.Current.BackupDirectory);

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GameIndex/1.0");

        var integrations = new List<IPlatformIntegration>
        {
            new SteamIntegration(), new EpicIntegration(), new GogIntegration(), new UbisoftIntegration(), new EaIntegration(),
            new XboxIntegration(), new BattleNetIntegration(), new RockstarIntegration(), new RiotIntegration(),
            new StandaloneScanner(() => settings.Current.ScanDirectories)
        };
        var artwork = new ArtworkService(paths, db, http);
        Instance = new AppHost
        {
            Paths = paths, Settings = settings, Db = db, Http = http, Integrations = integrations, Artwork = artwork,
            Discovery = new DiscoveryService(db, integrations, settings),
            Info = new GameInfoService(db, settings, integrations, artwork, http),
            Tracker = new PlaytimeTracker(db)
        };
        return Instance;
    }

    public void Dispose() { Tracker.Dispose(); Discovery.Dispose(); Http.Dispose(); }
}
