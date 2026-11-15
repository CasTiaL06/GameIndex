using System.Text.Json;

namespace GameIndex.Core.Integrations;

public sealed record EpicItem(string AppName, string DisplayName, string InstallLocation, string? LaunchExecutable, string? MainGameAppName,
    string? CatalogNamespace, string? CatalogItemId, bool Incomplete, bool IsEngine)
{
    public bool IsAddon => !string.IsNullOrEmpty(MainGameAppName) && !string.Equals(MainGameAppName, AppName, StringComparison.OrdinalIgnoreCase);
}

public sealed class EpicIntegration : PlatformIntegrationBase
{
    public override string Id => PlatformIds.Epic;
    public override string DisplayName => "Epic Games";
    public override PlatformCapabilities Capabilities => PlatformCapabilities.Discovery | PlatformCapabilities.Launch | PlatformCapabilities.InstalledDlc;
    public override string Limitations => "Reads the launcher's install manifests. Epic exposes no local playtime or achievement data, and no cover art, so those show as Not Available / use the generated fallback.";

    public static string ManifestDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public static EpicItem? ParseItem(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var app = S("AppName");
        if (string.IsNullOrEmpty(app)) return null;
        var engine = r.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array &&
                     cats.EnumerateArray().Any(c => string.Equals(c.GetString(), "engines", StringComparison.OrdinalIgnoreCase));
        return new EpicItem(app, S("DisplayName") ?? app, S("InstallLocation") ?? "", S("LaunchExecutable"), S("MainGameAppName"),
            S("CatalogNamespace"), S("CatalogItemId"), r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True, engine);
    }

    private static List<EpicItem> ReadAll()
    {
        var items = new List<EpicItem>();
        if (!Directory.Exists(ManifestDir)) return items;
        foreach (var f in Directory.EnumerateFiles(ManifestDir, "*.item"))
            try { var i = ParseItem(File.ReadAllText(f)); if (i is not null) items.Add(i); }
            catch (Exception ex) { Log.Warn($"Epic manifest {Path.GetFileName(f)} skipped: {ex.Message}"); }
        return items;
    }

    public override Task<DiscoveryResult> DiscoverAsync(CancellationToken ct) => Run(() =>
    {
        if (!Directory.Exists(ManifestDir)) return DiscoveryResult.NotPresent("Epic Games Launcher manifests not found.");
        var games = new List<DiscoveredGame>();
        foreach (var i in ReadAll())
        {
            if (i.Incomplete || i.IsAddon || i.IsEngine || string.IsNullOrEmpty(i.InstallLocation) || !Directory.Exists(i.InstallLocation)) continue;
            var exe = string.IsNullOrEmpty(i.LaunchExecutable) ? null : Path.Combine(i.InstallLocation, i.LaunchExecutable);
            var uri = i.CatalogNamespace is not null && i.CatalogItemId is not null
                ? $"com.epicgames.launcher://apps/{i.CatalogNamespace}%3A{i.CatalogItemId}%3A{i.AppName}?action=launch&silent=true"
                : $"com.epicgames.launcher://apps/{i.AppName}?action=launch&silent=true";
            games.Add(new DiscoveredGame(i.AppName, i.DisplayName, i.InstallLocation, exe is not null && File.Exists(exe) ? exe : null, uri));
        }
        return new DiscoveryResult(true, games);
    });

    public override Task<Result<IReadOnlyList<DlcInfo>>> GetDlcAsync(Game g, IntegrationContext x, CancellationToken ct) => Task.Run(() =>
    {
        if (!Directory.Exists(ManifestDir)) return Result<IReadOnlyList<DlcInfo>>.Fail("Epic manifests not found.");
        IReadOnlyList<DlcInfo> list = ReadAll()
            .Where(i => i.IsAddon && string.Equals(i.MainGameAppName, g.ExternalId, StringComparison.OrdinalIgnoreCase) && !i.Incomplete)
            .Select(i => new DlcInfo(i.AppName, i.DisplayName, true)).ToList();
        return new Result<IReadOnlyList<DlcInfo>>(list);
    }, ct);
}
