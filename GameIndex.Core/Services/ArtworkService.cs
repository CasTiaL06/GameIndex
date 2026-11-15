namespace GameIndex.Core.Services;

/// <summary>Finds and caches artwork locally (Assets/Covers, Assets/Backgrounds, Assets/Icons). Custom artwork is never replaced by automatic artwork.</summary>
public sealed class ArtworkService
{
    private readonly AppPaths _paths;
    private readonly Db _db;
    private readonly HttpClient _http;

    /// <summary>Set by the UI layer: extracts the shell icon of an EXE into a PNG and returns its path.</summary>
    public Func<Game, string, CancellationToken, Task<string?>>? ExeIconProvider { get; set; }

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
            if (!Has(g.CoverPath) && !Has(g.IconPath) && ExeIconProvider is not null && IconSourceFor(g) is { } src)
            {
                var icon = await ExeIconProvider(g, src, ct);
                if (icon is not null) _db.SetArtwork(g.Id, "icon", icon, false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn($"Artwork for '{g.Title}' failed: {ex.Message}"); }
    }

    /// <summary>The EXE whose icon represents the game: one inside the install folder (e.g. League of Legends.exe), never a launcher/client outside it.</summary>
    public static string? IconSourceFor(Game g)
    {
        try
        {
            if (!string.IsNullOrEmpty(g.InstallDir) && Directory.Exists(g.InstallDir))
            {
                if (!string.IsNullOrEmpty(g.ExePath) && File.Exists(g.ExePath) && PathUtil.IsUnder(g.ExePath, g.InstallDir)) return g.ExePath;
                var found = ExeFinder.FindMainExe(g.InstallDir, 3);
                if (found is not null) return found;
            }
        }
        catch { }
        return !string.IsNullOrEmpty(g.ExePath) && File.Exists(g.ExePath) ? g.ExePath : null;
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
