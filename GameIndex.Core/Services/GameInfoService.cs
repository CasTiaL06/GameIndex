using GameIndex.Core.Achievements;
using GameIndex.Core.Dlc;
using GameIndex.Core.Storage;

namespace GameIndex.Core.Services;

[Flags] public enum RefreshKind { None = 0, Size = 1, Dlc = 2, Achievements = 4, Stats = 8, Artwork = 16, All = 31 }

/// <summary>Coordinates all per-game information: size, DLC, achievements, imported playtime/last played, artwork. Runs entirely in the background.</summary>
public sealed class GameInfoService
{
    private readonly Db _db;
    private readonly SettingsService _settings;
    private readonly IReadOnlyList<IPlatformIntegration> _integrations;
    private readonly ArtworkService _artwork;
    private readonly AchievementService _ach;
    private readonly DlcService _dlc;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _sizeGate = new(2, 2);
    private readonly SemaphoreSlim _staleGate = new(1, 1);

    public event Action<long>? GameUpdated;

    public GameInfoService(Db db, SettingsService settings, IReadOnlyList<IPlatformIntegration> integrations, ArtworkService artwork, HttpClient http)
    {
        _db = db; _settings = settings; _integrations = integrations; _artwork = artwork; _http = http;
        _ach = new AchievementService(db, integrations);
        _dlc = new DlcService(db, integrations);
    }

    private IntegrationContext Ctx() => new(_settings.GetSteamApiKey(), _settings.Current.SteamId64, _http, new Dictionary<string, string>());

    /// <summary>Always runs on a thread-pool thread (file system walks, DB and HTTP must not touch the UI thread).</summary>
    public Task<string> RefreshGameAsync(long id, RefreshKind kinds, CancellationToken ct = default) => Task.Run(() => RefreshGameCoreAsync(id, kinds, ct), ct);

    private async Task<string> RefreshGameCoreAsync(long id, RefreshKind kinds, CancellationToken ct)
    {
        var g = _db.GetGame(id);
        if (g is null) return "Game not found.";
        var notes = new List<string>();

        if (kinds.HasFlag(RefreshKind.Artwork)) await _artwork.EnsureAsync(g, ct);

        if (kinds.HasFlag(RefreshKind.Size) && g.IsInstalled)
        {
            var dir = g.InstallDir ?? (g.ExePath is null ? null : Path.GetDirectoryName(g.ExePath));
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                await _sizeGate.WaitAsync(ct);
                try { var bytes = await Task.Run(() => StorageAnalyzer.GetDirectorySize(dir, ct), ct); _db.SetSize(id, bytes); }
                finally { _sizeGate.Release(); }
            }
        }

        var integ = _integrations.FirstOrDefault(i => i.Id == g.PlatformId);
        if (kinds.HasFlag(RefreshKind.Stats) && integ is not null && !g.IsManual && (integ.Capabilities & (PlatformCapabilities.Playtime | PlatformCapabilities.LastPlayed)) != 0)
        {
            var r = await integ.ImportStatsAsync(g, Ctx(), ct);
            if (r.Ok) _db.SetImportedStats(id, r.Value!.PlaytimeSeconds, r.Value.LastPlayedUtc is null ? null : Format.ToUnix(r.Value.LastPlayedUtc.Value));
            else notes.Add("Playtime import: " + r.Reason);
        }
        if (kinds.HasFlag(RefreshKind.Dlc)) { var r = await _dlc.RefreshAsync(g, Ctx(), ct); if (!r.Ok) notes.Add("DLC: " + r.Reason); }
        if (kinds.HasFlag(RefreshKind.Achievements)) { var r = await _ach.RefreshAsync(g, Ctx(), ct); if (!r.Ok) notes.Add("Achievements: " + r.Reason); }

        GameUpdated?.Invoke(id);
        return string.Join(" ", notes);
    }

    /// <summary>Fills in whatever is missing or stale. Called after every library scan.</summary>
    public Task RefreshStaleAsync(CancellationToken ct = default) => Task.Run(() => RefreshStaleCoreAsync(ct), ct);

    private async Task RefreshStaleCoreAsync(CancellationToken ct)
    {
        if (!await _staleGate.WaitAsync(0, ct)) return;
        try
        {
            var auto = _settings.Current.AutoMetadataRefresh;
            var hasKey = !string.IsNullOrEmpty(_settings.GetSteamApiKey());
            foreach (var g in _db.GetGames().Where(g => g.IsInstalled))
            {
                ct.ThrowIfCancellationRequested();
                var kinds = RefreshKind.None;
                if (g.SizeBytes is null || (auto && DateTime.UtcNow - g.SizeUpdatedUtc > TimeSpan.FromDays(7))) kinds |= RefreshKind.Size;
                // artwork is retried at most once a week per game (manual "Refresh Information" always retries)
                var artTried = long.TryParse(_db.GetSetting($"art_tried_{g.Id}"), out var at) && Format.NowUnix() - at < 7 * 86400;
                if (!artTried && ((g.CoverPath is null && g.IconPath is null) || (g.BackgroundPath is null && g.PlatformId == PlatformIds.Steam))) kinds |= RefreshKind.Artwork;
                if (auto && !g.IsManual)
                {
                    if (DateTime.UtcNow - g.StatsUpdatedUtc > TimeSpan.FromHours(1) || g.StatsUpdatedUtc is null) kinds |= RefreshKind.Stats;
                    if (g.DlcUpdatedUtc is null || DateTime.UtcNow - g.DlcUpdatedUtc > TimeSpan.FromDays(1)) kinds |= RefreshKind.Dlc;
                    if (g.PlatformId == PlatformIds.Steam && hasKey && (g.AchUpdatedUtc is null || DateTime.UtcNow - g.AchUpdatedUtc > TimeSpan.FromDays(1))) kinds |= RefreshKind.Achievements;
                }
                if (kinds == RefreshKind.None) continue;
                try { await RefreshGameCoreAsync(g.Id, kinds, ct); await Task.Delay(120, ct); } // gentle on web APIs
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Warn($"Refresh of '{g.Title}' failed: {ex.Message}"); }
            }
        }
        finally { _staleGate.Release(); }
    }
}
