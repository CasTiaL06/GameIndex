using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;

namespace GameIndex.Core.Integrations;

public sealed record SteamManifest(string AppId, string Name, string InstallDir, int StateFlags, long SizeOnDisk, long? LastPlayed, IReadOnlyList<string> DlcAppIds);

public sealed class SteamIntegration : PlatformIntegrationBase
{
    public override string Id => PlatformIds.Steam;
    public override string DisplayName => "Steam";
    public override PlatformCapabilities Capabilities => PlatformCapabilities.Discovery | PlatformCapabilities.Launch | PlatformCapabilities.InstalledDlc |
        PlatformCapabilities.Achievements | PlatformCapabilities.Playtime | PlatformCapabilities.LastPlayed | PlatformCapabilities.Artwork;
    public override string Limitations =>
        "No API key needed: playtime and achievements come from your public Steam Community profile (Steam > Profile > Edit > Privacy: Game details = Public). " +
        "An optional Steam Web API key gives exact minutes. If the profile is private, GameIndex falls back to Steam's local files, which can lag behind. " +
        "DLC: DLC depots in the app manifest plus DLC installed as their own Steam app; DLC merged into the base game's files cannot be told apart.";

    private const long SteamId64Base = 76561197960265728;
    private static readonly ConcurrentDictionary<string, string> DlcNameCache = new();
    private (DateTime At, Dictionary<long, (long Minutes, long LastPlayed)> Data)? _owned;

    // ---------- locating Steam ----------
    public static string? FindSteamPath()
    {
        var candidates = new[]
        {
            Reg.Str(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath"),
            Reg.Str(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"),
            Reg.Str(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath")
        };
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c) && Directory.Exists(c)) return Path.GetFullPath(c);
        return null;
    }

    public static IReadOnlyList<string> GetLibraryDirs(string steamPath)
    {
        var dirs = new List<string> { Path.Combine(steamPath, "steamapps") };
        try
        {
            var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                var root = VdfParser.Parse(File.ReadAllText(vdf))["libraryfolders"];
                if (root is not null)
                    foreach (var f in root.Children)
                    {
                        var p = f.Value ?? f.GetString("path"); // old format: "1" "D:\\Library"
                        if (string.IsNullOrWhiteSpace(p)) continue;
                        var apps = Path.Combine(p, "steamapps");
                        if (Directory.Exists(apps) && !dirs.Contains(apps, StringComparer.OrdinalIgnoreCase)) dirs.Add(apps);
                    }
            }
        }
        catch (Exception ex) { Log.Warn("Steam libraryfolders.vdf unreadable: " + ex.Message); }
        return dirs;
    }

    public static SteamManifest? ParseManifest(string acfText)
    {
        var s = VdfParser.Parse(acfText)["AppState"];
        var id = s?.GetString("appid");
        if (s is null || id is null) return null;
        var dlc = s["InstalledDepots"]?.Children.Select(d => d.GetString("dlcappid")).Where(x => !string.IsNullOrEmpty(x)).Distinct().Cast<string>().ToList() ?? [];
        return new SteamManifest(id, s.GetString("name") ?? ("App " + id), s.GetString("installdir") ?? "",
            (int)(s.GetLong("StateFlags") ?? 0), s.GetLong("SizeOnDisk") ?? 0, s.GetLong("LastPlayed"), dlc);
    }

    private static bool IsTool(string name, string id) =>
        id == "228980" || name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Steam Controller", StringComparison.OrdinalIgnoreCase);

    public override Task<DiscoveryResult> DiscoverAsync(CancellationToken ct) => Run(() =>
    {
        var steam = FindSteamPath();
        if (steam is null) return DiscoveryResult.NotPresent("Steam is not installed.");
        var games = new List<DiscoveredGame>();
        foreach (var lib in GetLibraryDirs(steam))
        {
            if (!Directory.Exists(lib)) continue;
            foreach (var acf in Directory.EnumerateFiles(lib, "appmanifest_*.acf"))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var m = ParseManifest(File.ReadAllText(acf));
                    if (m is null || IsTool(m.Name, m.AppId) || (m.StateFlags & 4) != 4 || string.IsNullOrEmpty(m.InstallDir)) continue; // 4 = fully installed
                    var dir = Path.Combine(lib, "common", m.InstallDir);
                    if (!Directory.Exists(dir)) continue; // verify it is really installed
                    games.Add(new DiscoveredGame(m.AppId, m.Name, dir, LaunchUri: $"steam://rungameid/{m.AppId}"));
                }
                catch (Exception ex) { Log.Warn($"Steam manifest {Path.GetFileName(acf)} skipped: {ex.Message}"); }
            }
        }
        return new DiscoveryResult(true, games);
    });

    private static SteamManifest? FindManifest(string appId)
    {
        var steam = FindSteamPath();
        if (steam is null) return null;
        foreach (var lib in GetLibraryDirs(steam))
        {
            var f = Path.Combine(lib, $"appmanifest_{appId}.acf");
            if (File.Exists(f)) { try { return ParseManifest(File.ReadAllText(f)); } catch { } }
        }
        return null;
    }

    // ---------- DLC ----------
    public override async Task<Result<IReadOnlyList<DlcInfo>>> GetDlcAsync(Game g, IntegrationContext x, CancellationToken ct)
    {
        var m = FindManifest(g.ExternalId);
        if (m is null) return Result<IReadOnlyList<DlcInfo>>.Fail("Steam app manifest not found.");
        var list = new List<DlcInfo>();
        foreach (var id in m.DlcAppIds)
        {
            ct.ThrowIfCancellationRequested();
            string name;
            if (x.KnownDlcNames.TryGetValue(id, out var known) && !known.StartsWith("DLC ")) name = known;
            else if (!DlcNameCache.TryGetValue(id, out name!))
            {
                name = await FetchStoreName(id, x.Http, ct) ?? $"DLC {id}";
                if (!name.StartsWith("DLC ")) DlcNameCache[id] = name;
            }
            list.Add(new DlcInfo(id, name, true)); // present as an installed depot in the manifest
        }
        // Many DLC are installed as their own Steam app (own appmanifest) instead of a depot of the base game.
        foreach (var id in await FetchStoreDlcIds(g.ExternalId, x.Http, ct))
        {
            if (list.Any(d => d.ExternalId == id)) continue;
            var own = FindManifest(id);
            if (own is not null && (own.StateFlags & 4) == 4) list.Add(new DlcInfo(id, own.Name, true));
        }
        return new Result<IReadOnlyList<DlcInfo>>(list);
    }

    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> StoreDlcCache = new();

    /// <summary>All DLC appids of a game according to the public Steam store API (no key needed).</summary>
    private static async Task<IReadOnlyList<string>> FetchStoreDlcIds(string appId, HttpClient http, CancellationToken ct)
    {
        if (StoreDlcCache.TryGetValue(appId, out var cached)) return cached;
        try
        {
            var json = await http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic", ct);
            using var doc = JsonDocument.Parse(json);
            var ids = new List<string>();
            if (doc.RootElement.TryGetProperty(appId, out var e) && e.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object &&
                d.TryGetProperty("dlc", out var dlc) && dlc.ValueKind == JsonValueKind.Array)
                foreach (var v in dlc.EnumerateArray()) ids.Add(v.ToString());
            StoreDlcCache[appId] = ids;
            return ids;
        }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    private static async Task<string?> FetchStoreName(string appId, HttpClient http, CancellationToken ct)
    {
        try
        {
            var json = await http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic", ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(appId, out var e) && e.TryGetProperty("success", out var ok) && ok.GetBoolean() &&
                e.TryGetProperty("data", out var d) && d.TryGetProperty("name", out var n)) return n.GetString();
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return null;
    }

    // ---------- user ids / playtime ----------
    private static string? MostRecentSteamId64(string steamPath)
    {
        try
        {
            var f = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(f)) return null;
            var users = VdfParser.Parse(File.ReadAllText(f))["users"];
            return users?.Children.FirstOrDefault(u => u.GetString("MostRecent") == "1")?.Key ?? users?.Children.FirstOrDefault()?.Key;
        }
        catch { return null; }
    }

    private sealed class PrivateProfileException : Exception { public PrivateProfileException() : base("Steam profile is private") { } }
    private static DateTime _privateUntil = DateTime.MinValue;          // profile found private: do not hammer the community endpoints for a while
    private (DateTime At, string SteamId, Dictionary<long, double> Hours)? _community;

    private static string? SteamIdFor(IntegrationContext x, string steamPath) => !string.IsNullOrWhiteSpace(x.SteamId64) ? x.SteamId64 : MostRecentSteamId64(steamPath);

    /// <summary>Hours on record per appid from the public profile XML (no API key). Throws PrivateProfileException for private profiles.</summary>
    private async Task<Dictionary<long, double>> GetCommunityHoursAsync(HttpClient http, string steamId, CancellationToken ct)
    {
        if (_community is { } c && c.SteamId == steamId && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(5)) return c.Hours;
        var xml = await http.GetStringAsync($"https://steamcommunity.com/profiles/{steamId}/games?tab=all&xml=1", ct);
        var map = ParseCommunityGames(xml);
        _community = (DateTime.UtcNow, steamId, map);
        return map;
    }

    public static Dictionary<long, double> ParseCommunityGames(string xml)
    {
        var doc = XDocument.Parse(xml);
        var err = doc.Root?.Element("error")?.Value;
        if (err is not null) { if (err.Contains("private", StringComparison.OrdinalIgnoreCase)) throw new PrivateProfileException(); throw new InvalidOperationException(err); }
        var map = new Dictionary<long, double>();
        foreach (var g in doc.Descendants("game"))
        {
            if (!long.TryParse((string?)g.Element("appID"), out var id)) continue;
            var h = ((string?)g.Element("hoursOnRecord"))?.Replace(",", "");
            map[id] = double.TryParse(h, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) ? hours : 0;
        }
        return map;
    }

    public override async Task<Result<ImportedStats>> ImportStatsAsync(Game g, IntegrationContext x, CancellationToken ct)
    {
        var steam = FindSteamPath();
        if (steam is null) return Result<ImportedStats>.Fail("Steam is not installed.");
        var steamId = SteamIdFor(x, steam);
        long? server = null; DateTime? last = null;

        // 1) Steam Web API: exact minutes, only when the user supplied a key
        if (!string.IsNullOrEmpty(x.SteamApiKey) && !string.IsNullOrEmpty(steamId) && long.TryParse(g.ExternalId, out var appId))
        {
            try
            {
                var owned = await GetOwnedAsync(x, steamId, ct);
                if (owned.TryGetValue(appId, out var o)) { server = o.Minutes * 60; if (o.LastPlayed > 0) last = Format.FromUnix(o.LastPlayed); }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn("Steam GetOwnedGames failed: " + ex.GetType().Name); }
        }

        // 2) Public community profile (no key): the same server-side number Steam itself shows
        if (server is null && !string.IsNullOrEmpty(steamId) && long.TryParse(g.ExternalId, out var aid) && DateTime.UtcNow >= _privateUntil)
        {
            try
            {
                var hours = await GetCommunityHoursAsync(x.Http, steamId, ct);
                if (hours.TryGetValue(aid, out var h)) server = (long)Math.Round(h * 3600);
            }
            catch (OperationCanceledException) { throw; }
            catch (PrivateProfileException) { _privateUntil = DateTime.UtcNow.AddMinutes(30); }
            catch (Exception ex) { Log.Warn("Steam community playtime failed: " + ex.Message); }
        }

        // 3) Steam's local config (can lag behind the server)
        long? local = null; DateTime? localLast = null;
        try
        {
            if (!string.IsNullOrEmpty(steamId) && long.TryParse(steamId, out var id64))
            {
                var cfg = Path.Combine(steam, "userdata", (id64 - SteamId64Base).ToString(), "config", "localconfig.vdf");
                if (File.Exists(cfg))
                {
                    var app = VdfParser.Parse(File.ReadAllText(cfg))["UserLocalConfigStore"]?["Software"]?["Valve"]?["Steam"]?["apps"]?[g.ExternalId];
                    local = app?.GetLong("Playtime") * 60;
                    if (app?.GetLong("LastPlayed") is > 0 and var lp) localLast = Format.FromUnix(lp);
                }
            }
        }
        catch (Exception ex) { Log.Warn("Steam localconfig read failed: " + ex.Message); }

        long? best = server is null ? local : local is null ? server : Math.Max(server.Value, local.Value);   // never show less than any source says
        var lastBest = last ?? localLast;
        if (best is null && lastBest is null)
            return Result<ImportedStats>.Fail("No Steam playtime found. Set your Steam profile's Game details to Public (no API key needed).");
        return new Result<ImportedStats>(new ImportedStats(best, lastBest));
    }

    private async Task<Dictionary<long, (long Minutes, long LastPlayed)>> GetOwnedAsync(IntegrationContext x, string steamId, CancellationToken ct)
    {
        if (_owned is { } c && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(5)) return c.Data;
        var json = await x.Http.GetStringAsync($"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={x.SteamApiKey}&steamid={steamId}&include_played_free_games=1&format=json", ct);
        using var doc = JsonDocument.Parse(json);
        var map = new Dictionary<long, (long, long)>();
        if (doc.RootElement.TryGetProperty("response", out var r) && r.TryGetProperty("games", out var games))
            foreach (var e in games.EnumerateArray())
                map[e.GetProperty("appid").GetInt64()] = (e.TryGetProperty("playtime_forever", out var p) ? p.GetInt64() : 0,
                    e.TryGetProperty("rtime_last_played", out var l) ? l.GetInt64() : 0);
        _owned = (DateTime.UtcNow, map);
        return map;
    }

    // ---------- achievements ----------
    public override async Task<Result<AchievementData>> GetAchievementsAsync(Game g, IntegrationContext x, CancellationToken ct)
    {
        var steam = FindSteamPath();
        var steamId = steam is null ? x.SteamId64 : SteamIdFor(x, steam);
        if (string.IsNullOrEmpty(steamId)) return Result<AchievementData>.Fail("SteamID64 unknown. Sign in to Steam once, or enter it in Settings.");

        Result<AchievementData>? apiResult = null;
        if (!string.IsNullOrEmpty(x.SteamApiKey))
        {
            apiResult = await ApiAchievementsAsync(g, x, steamId, ct);
            if (apiResult.Ok) return apiResult;
        }
        if (DateTime.UtcNow < _privateUntil) return Result<AchievementData>.Fail("Your Steam profile is private. Set Game details to Public (no API key needed).");
        var community = await CommunityAchievementsAsync(g, x, steamId, ct);
        return community.Ok ? community : (apiResult ?? community);
    }

    public static Result<AchievementData> ParseCommunityAchievements(string xml)
    {
        var doc = XDocument.Parse(xml);
        var err = doc.Root?.Element("error")?.Value;
        if (err is not null) return Result<AchievementData>.Fail("Steam: " + err);
        var privacy = doc.Root?.Element("privacyState")?.Value;
        if (privacy is not null && !privacy.Equals("public", StringComparison.OrdinalIgnoreCase))
            return Result<AchievementData>.Fail("Your Steam profile's stats are not public. Set Game details to Public (no API key needed).");
        var items = doc.Descendants("achievement").Select(a =>
        {
            var api = (string?)a.Element("apiname");
            var name = (string?)a.Element("name") ?? api ?? "?";
            var unlocked = (string?)a.Attribute("closed") == "1";
            DateTime? when = long.TryParse((string?)a.Element("unlockTimestamp"), out var ts) && ts > 0 ? Format.FromUnix(ts) : null;
            return new AchievementRecord(api ?? name, name, (string?)a.Element("description"), (string?)a.Element("iconClosed"), (string?)a.Element("iconOpen"), unlocked, unlocked ? when : null);
        }).ToList();
        return items.Count == 0 ? Result<AchievementData>.Fail("This game has no Steam achievements (or its stats are not public).") : new Result<AchievementData>(new AchievementData(items));
    }

    private async Task<Result<AchievementData>> CommunityAchievementsAsync(Game g, IntegrationContext x, string steamId, CancellationToken ct)
    {
        try
        {
            var xml = await x.Http.GetStringAsync($"https://steamcommunity.com/profiles/{steamId}/stats/{g.ExternalId}/?xml=1", ct);
            var r = ParseCommunityAchievements(xml);
            if (!r.Ok && r.Reason?.Contains("not public", StringComparison.OrdinalIgnoreCase) == true) _privateUntil = DateTime.UtcNow.AddMinutes(30);
            return r;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn("Steam community achievements failed: " + ex.GetType().Name); return Result<AchievementData>.Fail("Could not read Steam achievements from your public profile."); }
    }

    private async Task<Result<AchievementData>> ApiAchievementsAsync(Game g, IntegrationContext x, string steamId, CancellationToken ct)
    {
        try
        {
            // 1) schema: names, descriptions, icons, total count
            var schemaJson = await x.Http.GetStringAsync($"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?key={x.SteamApiKey}&appid={g.ExternalId}&l=english", ct);
            using var schema = JsonDocument.Parse(schemaJson);
            var defs = new List<(string Api, string Name, string? Desc, string? Icon, string? Gray)>();
            if (schema.RootElement.TryGetProperty("game", out var game) && game.TryGetProperty("availableGameStats", out var stats) && stats.TryGetProperty("achievements", out var arr))
                foreach (var a in arr.EnumerateArray())
                    defs.Add((a.GetProperty("name").GetString()!, a.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
                        a.TryGetProperty("description", out var d) ? d.GetString() : null,
                        a.TryGetProperty("icon", out var i) ? i.GetString() : null, a.TryGetProperty("icongray", out var ig) ? ig.GetString() : null));
            if (defs.Count == 0) return Result<AchievementData>.Fail("This game has no Steam achievements.");

            // 2) the player's unlocked state
            var resp = await x.Http.GetAsync($"https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v1/?key={x.SteamApiKey}&steamid={steamId}&appid={g.ExternalId}&l=english", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return Result<AchievementData>.Fail("Steam refused access: set your Steam profile's Game details to Public.");
            using var player = JsonDocument.Parse(body);
            if (!player.RootElement.TryGetProperty("playerstats", out var ps)) return Result<AchievementData>.Fail("Steam returned no achievement data.");
            if (ps.TryGetProperty("success", out var succ) && !succ.GetBoolean())
                return Result<AchievementData>.Fail(ps.TryGetProperty("error", out var err) ? "Steam: " + err.GetString() : "Steam returned no achievement data.");
            var state = new Dictionary<string, (bool, long)>();
            if (ps.TryGetProperty("achievements", out var pa))
                foreach (var a in pa.EnumerateArray())
                    state[a.GetProperty("apiname").GetString()!] = (a.GetProperty("achieved").GetInt32() == 1, a.TryGetProperty("unlocktime", out var ut) ? ut.GetInt64() : 0);

            var items = defs.Select(d =>
            {
                var (ach, t) = state.TryGetValue(d.Api, out var s) ? s : (false, 0L);
                return new AchievementRecord(d.Api, string.IsNullOrEmpty(d.Name) ? d.Api : d.Name, d.Desc, d.Icon, d.Gray, ach, ach && t > 0 ? Format.FromUnix(t) : null);
            }).ToList();
            return new Result<AchievementData>(new AchievementData(items));
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex) { return Result<AchievementData>.Fail($"Steam request failed ({(int?)ex.StatusCode ?? 0}). Check the API key and profile privacy."); }
        catch (Exception ex) { Log.Warn("Steam achievements failed: " + ex.GetType().Name); return Result<AchievementData>.Fail("Could not read Steam achievements."); }
    }
}
