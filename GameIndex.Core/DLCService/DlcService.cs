namespace GameIndex.Core.Dlc;

/// <summary>Estimates installed DLC from DLC folders inside the game directory (used when a platform cannot tell). Always labeled "estimate".</summary>
public static class FolderDlcScanner
{
    private static readonly string[] Names = ["DLC", "DLCs", "Addons", "AddOns", "Expansions", "Expansion", "Content\\DLC"];

    public static IReadOnlyList<DlcInfo> Scan(string? installDir)
    {
        var found = new Dictionary<string, DlcInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir)) return [];
            var roots = new List<string> { installDir };
            roots.AddRange(Directory.EnumerateDirectories(installDir).Take(40));      // one level deeper: <game>\<GameName>\DLC
            foreach (var root in roots)
                foreach (var n in Names)
                {
                    var dir = Path.Combine(root, n);
                    if (!Directory.Exists(dir)) continue;
                    foreach (var sub in Directory.EnumerateDirectories(dir).Take(50))
                    {
                        if (!Directory.EnumerateFileSystemEntries(sub).Any()) continue;   // empty folder = not installed
                        var name = new DirectoryInfo(sub).Name;
                        found[sub] = new DlcInfo(name, name, true);
                    }
                }
        }
        catch { }
        return found.Values.ToList();
    }
}

/// <summary>Detects installed DLC through platform metadata; platforms that cannot tell fall back to a clearly labeled folder estimate. Uninstalled DLC is never counted.</summary>
public sealed class DlcService
{
    private readonly Db _db;
    private readonly IReadOnlyList<IPlatformIntegration> _integrations;
    public DlcService(Db db, IReadOnlyList<IPlatformIntegration> integrations) { _db = db; _integrations = integrations; }

    public async Task<Result<IReadOnlyList<DlcInfo>>> RefreshAsync(Game g, IntegrationContext ctx, CancellationToken ct)
    {
        var integ = _integrations.FirstOrDefault(i => i.Id == g.PlatformId);
        Result<IReadOnlyList<DlcInfo>> r;
        if (integ is null || g.IsManual || (integ.Capabilities & PlatformCapabilities.InstalledDlc) == 0)
            r = Result<IReadOnlyList<DlcInfo>>.Fail(g.IsManual ? "Manual games have no DLC source." : $"{g.PlatformName} does not expose installed DLC.");
        else
        {
            var known = _db.GetDlcs(g.Id).ToDictionary(d => d.ExternalId, d => d.Name);
            r = await integ.GetDlcAsync(g, ctx with { KnownDlcNames = known }, ct);
        }

        if (r.Ok && r.Value!.Count > 0) { _db.SetDlcs(g.Id, r.Value, "platform"); return r; }

        var estimate = FolderDlcScanner.Scan(g.InstallDir);
        if (estimate.Count > 0)
        {
            _db.SetDlcs(g.Id, estimate, "folders");
            return new Result<IReadOnlyList<DlcInfo>>(estimate);
        }
        _db.SetDlcs(g.Id, r.Ok ? r.Value : null, "platform");     // platform answered "none" (0) or could not answer (unavailable)
        return r;
    }
}
