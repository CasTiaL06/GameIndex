namespace GameIndex.Core.Discovery;

public sealed class DiscoveryService : IDisposable
{
    private readonly Db _db;
    private readonly IReadOnlyList<IPlatformIntegration> _integrations;
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public event Action? LibraryChanged;
    public bool IsScanning { get; private set; }

    public DiscoveryService(Db db, IReadOnlyList<IPlatformIntegration> integrations, SettingsService settings)
    { _db = db; _integrations = integrations; _settings = settings; }

    public void StartBackground()
    {
        _loop = Task.Run(async () =>
        {
            try
            {
                await ScanAsync(_cts.Token); // initial scan
                while (!_cts.IsCancellationRequested)
                {
                    var minutes = Math.Max(1, _settings.Current.ScanIntervalMinutes);
                    await Task.Delay(TimeSpan.FromMinutes(minutes), _cts.Token);
                    if (_settings.Current.AutoDetectGames) await ScanAsync(_cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error("Background discovery loop failed", ex); }
        });
    }

    /// <summary>Scans every enabled platform. Safe to call repeatedly (a running scan is not duplicated).</summary>
    public async Task ScanAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        IsScanning = true;
        var changed = false;
        try
        {
            foreach (var integ in _integrations)
            {
                ct.ThrowIfCancellationRequested();
                if (_settings.Current.DisabledPlatforms.Contains(integ.Id)) { _db.SetIntegrationStatus(integ.Id, "Disabled", null, 0); continue; }
                try
                {
                    var res = await integ.DiscoverAsync(ct);
                    changed |= Sync(integ.Id, res);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Error($"Discovery failed for {integ.Id}", ex);
                    _db.SetIntegrationStatus(integ.Id, "Error", ex.Message, 0);
                }
            }
            changed |= VerifyManualGames();
        }
        finally { IsScanning = false; _gate.Release(); }
        if (changed) LibraryChanged?.Invoke();
    }

    private bool Sync(string platformId, DiscoveryResult res)
    {
        if (!res.PlatformPresent)
        {
            _db.SetIntegrationStatus(platformId, "Not detected", res.Message, 0);
            return false;
        }
        var existing = _db.GetGames();
        var byKey = existing.Where(g => g.PlatformId == platformId).ToDictionary(g => g.ExternalId);
        var others = existing.Where(g => g.PlatformId != platformId).ToList();
        var present = new HashSet<string>();
        var changed = false;
        var count = 0;

        foreach (var d in res.Games)
        {
            if (string.IsNullOrEmpty(d.InstallDir) ? false : !Directory.Exists(d.InstallDir)) continue;   // verify it is really installed
            var isKnown = byKey.TryGetValue(d.ExternalId, out var prior);
            if (!isKnown && IsClaimedElsewhere(d, others)) continue;                                      // duplicate prevention
            present.Add(d.ExternalId);
            count++;

            var toSave = d;
            if (d.ExePath is null && (!isKnown || prior!.ExePath is null) && d.InstallDir is not null)
                toSave = d with { ExePath = ExeFinder.FindMainExe(d.InstallDir) };                       // best guess only for games that have none

            var (_, isNew) = _db.UpsertDiscovered(platformId, toSave);
            if (isNew || (isKnown && (!prior!.IsInstalled || prior.InstallDir != d.InstallDir || prior.Title != d.Title && !prior.UserEdited))) changed = true;
        }
        var missing = _db.MarkMissing(platformId, present);
        if (missing > 0) changed = true;
        _db.SetIntegrationStatus(platformId, "Detected", res.Message, count);
        return changed;
    }

    private static bool IsClaimedElsewhere(DiscoveredGame d, List<Game> others)
    {
        if (string.IsNullOrEmpty(d.InstallDir)) return false;
        foreach (var o in others)
        {
            try
            {
                if (!string.IsNullOrEmpty(o.InstallDir) && PathUtil.Norm(o.InstallDir).Equals(PathUtil.Norm(d.InstallDir), StringComparison.OrdinalIgnoreCase)) return true;
                if (!string.IsNullOrEmpty(o.ExePath) && PathUtil.IsUnder(o.ExePath, d.InstallDir)) return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>Manual games are never removed; their installed flag just follows whether the EXE still exists.</summary>
    private bool VerifyManualGames()
    {
        var changed = false;
        foreach (var g in _db.GetGames().Where(g => g.IsManual))
        {
            var exists = !string.IsNullOrEmpty(g.ExePath) && File.Exists(g.ExePath);
            if (exists != g.IsInstalled) { _db.SetInstalled(g.Id, exists); changed = true; }
        }
        return changed;
    }

    public void Dispose() { _cts.Cancel(); try { _loop?.Wait(1500); } catch { } _cts.Dispose(); }
}
