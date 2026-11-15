namespace GameIndex.Core.Services;

/// <summary>Finds and caches artwork locally (Assets/Covers, Assets/Backgrounds, Assets/Icons). Custom artwork is never replaced by automatic artwork.</summary>
public sealed class ArtworkService
{
    private readonly AppPaths _paths;
    private readonly Db _db;
    private readonly HttpClient _http;

    /// <summary>Set by the UI layer: extracts the shell icon of an EXE into a PNG and returns its path.</summary>
    public Func<Game, IReadOnlyList<string>, CancellationToken, Task<string?>>? ExeIconProvider { get; set; }

    public ArtworkService(AppPaths paths, Db db, HttpClient http) { _paths = paths; _db = db; _http = http; }

    public async Task EnsureAsync(Game g, CancellationToken ct)
    {
        try
        {
            if (g.PlatformId == PlatformIds.Steam && !g.IsManual)
            {
                if (!Has(g.CoverPath)) await SteamAsync(g, "cover", "library_600x900.jpg", _paths.CoversDir, ct);
                if (!Has(g.BackgroundPath)) await SteamAsync(g, "background", "library_hero.jpg", _paths.BackgroundsDir, ct);
                if (!Has(g.LogoPath)) await SteamAsync(g, "logo", "logo.png", _paths.IconsDir, ct);
            }
            if (!Has(g.CoverPath) && !Has(g.IconPath)) await TryIconAsync(g, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn($"Artwork for '{g.Title}' failed: {ex.Message}"); }
        // remember that we tried, so scans do not repeat expensive searches every 15 minutes (manual refresh ignores this)
        try { _db.SetSetting($"art_tried_{g.Id}", Format.NowUnix().ToString()); } catch { }
    }

    /// <summary>
    /// Icon search, best first: (1) logo/icon files shipped with the game (Xbox package logos, GOG/other .ico files),
    /// (2) embedded icons of the game's executables – several candidates are tried, never a generic icon.
    /// If nothing is found the UI draws a coloured initials card.
    /// </summary>
    private async Task<bool> TryIconAsync(Game g, CancellationToken ct)
    {
        var file = FindShippedImage(g);
        if (file is not null)
        {
            var dest = Path.Combine(_paths.IconsDir, $"file_{g.Id}{Path.GetExtension(file)}");
            File.Copy(file, dest, true);
            _db.SetArtwork(g.Id, "icon", dest, false);
            return true;
        }
        if (ExeIconProvider is not null)
        {
            var candidates = IconCandidatesFor(g);
            if (candidates.Count > 0)
            {
                var icon = await ExeIconProvider(g, candidates, ct);
                if (icon is not null) { _db.SetArtwork(g.Id, "icon", icon, false); return true; }
            }
        }
        return false;
    }

    private static string? FindShippedImage(Game g)
    {
        try
        {
            var dir = g.InstallDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            var places = new List<string> { dir, Path.Combine(dir, "Assets") };
            foreach (var d in places.Where(Directory.Exists))
                foreach (var pattern in new[] { "Square310x310Logo*.png", "Square150x150Logo*.png", "StoreLogo*.png", "Logo*.png", "SmallLogo*.png" })
                {
                    var hit = Directory.EnumerateFiles(d, pattern).FirstOrDefault();
                    if (hit is not null) return hit;
                }
            return Directory.EnumerateFiles(dir, "*.ico").Where(f => !Path.GetFileName(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Executables whose icon can represent the game. Epic/Riot report launcher stubs, so several candidates from the game folder are tried.</summary>
    public static IReadOnlyList<string> IconCandidatesFor(Game g)
    {
        var list = new List<string>();
        try
        {
            var dir = g.InstallDir;
            if (!string.IsNullOrEmpty(g.ExePath) && File.Exists(g.ExePath) && (string.IsNullOrEmpty(dir) || PathUtil.IsUnder(g.ExePath, dir))) list.Add(g.ExePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                foreach (var c in ExeFinder.FindCandidates(dir)) if (!list.Contains(c, StringComparer.OrdinalIgnoreCase)) list.Add(c);
            if (list.Count == 0 && !string.IsNullOrEmpty(g.ExePath) && File.Exists(g.ExePath)) list.Add(g.ExePath);
        }
        catch { }
        return list;
    }

    private static bool Has(string? p) => !string.IsNullOrEmpty(p) && File.Exists(p);

    private async Task SteamAsync(Game g, string kind, string fileName, string targetDir, CancellationToken ct)
    {
        var dest = Path.Combine(targetDir, $"steam_{g.ExternalId}_{fileName}");
        if (File.Exists(dest)) { _db.SetArtwork(g.Id, kind, dest, false); return; }

        var local = FindLocalSteamArt(g.ExternalId, fileName);
        if (local is not null) { File.Copy(local, dest, true); _db.SetArtwork(g.Id, kind, dest, false); return; }

        var url = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{g.ExternalId}/{fileName}";
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return;
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length < 512) return;
        await File.WriteAllBytesAsync(dest, bytes, ct);
        _db.SetArtwork(g.Id, kind, dest, false);
    }

    private static string? FindLocalSteamArt(string appId, string fileName)
    {
        try
        {
            var steam = SteamIntegration.FindSteamPath();
            if (steam is null) return null;
            var cache = Path.Combine(steam, "appcache", "librarycache");
            var legacy = Path.Combine(cache, $"{appId}_{fileName}");
            if (File.Exists(legacy)) return legacy;
            var dir = Path.Combine(cache, appId); // newer layout: librarycache/<appid>/<hash>/<file>
            if (Directory.Exists(dir)) return Directory.EnumerateFiles(dir, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { }
        return null;
    }

    /// <summary>Copies a user-picked image into the portable Assets folder and registers it as custom artwork.</summary>
    public string ImportCustom(long gameId, string kind, string sourceFile)
    {
        var dir = kind == "background" ? _paths.BackgroundsDir : _paths.CoversDir;
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, $"custom_{gameId}_{kind}_{Guid.NewGuid():N}".Substring(0, 40) + Path.GetExtension(sourceFile));
        File.Copy(sourceFile, dest, true);
        _db.SetArtwork(gameId, kind, dest, true);
        return dest;
    }

    public void ClearAutomatic()
    {
        foreach (var p in _db.ClearAutomaticArtwork()) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
        try { foreach (var f in Directory.EnumerateFiles(_paths.CacheDir)) File.Delete(f); } catch { }
    }
}
