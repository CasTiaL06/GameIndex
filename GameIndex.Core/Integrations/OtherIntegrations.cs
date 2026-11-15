using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameIndex.Core.Integrations;

internal static class UninstallScan
{
    public static IEnumerable<(string Key, string? Name, string? Location, string? Publisher)> Entries()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            const string p = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            foreach (var name in Reg.SubKeys(RegistryHive.LocalMachine, view, p))
            {
                using var k = Reg.Open(RegistryHive.LocalMachine, view, p + "\\" + name);
                if (k is null) continue;
                yield return (name, k.GetValue("DisplayName")?.ToString(), k.GetValue("InstallLocation")?.ToString()?.Trim('"'), k.GetValue("Publisher")?.ToString());
            }
        }
    }
}

public sealed class UbisoftIntegration : PlatformIntegrationBase
{
    public override string Id => PlatformIds.Ubisoft;
    public override string DisplayName => "Ubisoft Connect";
    public override PlatformCapabilities Capabilities => PlatformCapabilities.Discovery | PlatformCapabilities.Launch;
    public override string Limitations => "Detects installs via registry and launches through uplay://. Ubisoft Connect has no public API for playtime, achievements or DLC, so those are Not Available.";

    public override Task<DiscoveryResult> DiscoverAsync(CancellationToken ct) => Run(() =>
    {
        var games = new Dictionary<string, DiscoveredGame>();
        foreach (var e in UninstallScan.Entries())
        {
            if (!e.Key.StartsWith("Uplay Install ", StringComparison.OrdinalIgnoreCase)) continue;
            var id = e.Key["Uplay Install ".Length..].Trim();
            if (string.IsNullOrEmpty(e.Location) || !Directory.Exists(e.Location)) continue;
            games[id] = new DiscoveredGame(id, e.Name ?? new DirectoryInfo(e.Location).Name, e.Location, LaunchUri: $"uplay://launch/{id}/0");
        }
        foreach (var id in Reg.SubKeys(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Ubisoft\Launcher\Installs"))
        {
            if (games.ContainsKey(id)) continue;
            var dir = Reg.Str(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Ubisoft\Launcher\Installs\" + id, "InstallDir");
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                games[id] = new DiscoveredGame(id, new DirectoryInfo(dir.TrimEnd('\\', '/')).Name, dir, LaunchUri: $"uplay://launch/{id}/0");
        }
        var installed = Reg.SubKeys(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Ubisoft\Launcher").Length > 0 || games.Count > 0;
        return installed ? new DiscoveryResult(true, games.Values.ToList()) : DiscoveryResult.NotPresent("Ubisoft Connect not found.");
    });
}

public sealed class EaIntegration : PlatformIntegrationBase
{
    public override string Id => PlatformIds.Ea;
    public override string DisplayName => "EA App";
    public override PlatformCapabilities Capabilities => PlatformCapabilities.Discovery | PlatformCapabilities.Launch;
    public override string Limitations => "Detects games registered under HKLM\\SOFTWARE\\EA Games. Some EA App installs do not register there; add those via a scan directory or manually. No public playtime/achievement/DLC API.";

    public override Task<DiscoveryResult> DiscoverAsync(CancellationToken ct) => Run(() =>
    {
        const string root = @"SOFTWARE\EA Games";
        var names = Reg.SubKeys(RegistryHive.LocalMachine, RegistryView.Registry32, root);
        var games = new List<DiscoveredGame>();
        foreach (var n in names)
        {
            var dir = Reg.Str(RegistryHive.LocalMachine, RegistryView.Registry32, root + "\\" + n, "Install Dir")
                      ?? Reg.Str(RegistryHive.LocalMachine, RegistryView.Registry32, root + "\\" + n, "InstallDir");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            var title = Reg.Str(RegistryHive.LocalMachine, RegistryView.Registry32, root + "\\" + n, "DisplayName") ?? n;
            games.Add(new DiscoveredGame(n, title, dir));
        }
        var present = names.Length > 0 || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EA Desktop"));
        return present ? new DiscoveryResult(true, games) : DiscoveryResult.NotPresent("EA App not found.");
    });
}

public sealed class BattleNetIntegration : PlatformIntegrationBase
{
    public override string Id => PlatformIds.BattleNet;
    public override string DisplayName => "Battle.net";
    public override PlatformCapabilities Capabilities => PlatformCapabilities.Discovery | PlatformCapabilities.Launch;
    public override string Limitations => "Detects Blizzard games from their uninstall registry entries and launches the game executable. Battle.net's product database is an undocumented binary format and is not read; no playtime/achievement/DLC API.";

    public override Task<DiscoveryResult> DiscoverAsync(CancellationToken ct) => Run(() =>
    {
        var present = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Battle.net"));
        var games = new Dictionary<string, DiscoveredGame>();
        foreach (var e in UninstallScan.Entries())
        {
            if (!string.Equals(e.Publisher, "Blizzard Entertainment", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(e.Name) || e.Name.Contains("Battle.net", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(e.Location) || !Directory.Exists(e.Location)) continue;
            games[e.Key] = new DiscoveredGame(e.Key, e.Name, e.Location);
        }
        return present || games.Count > 0 ? new DiscoveryResult(true, games.Values.ToList()) : DiscoveryResult.NotPresent("Battle.net not found.");
    });
}

public sealed class RockstarIntegration : PlatformIntegrationBase
{
    public override string Id => PlatformIds.Rockstar;
    public override string DisplayName => "Rockstar Games";
    public override PlatformCapabilities Capabilities => PlatformCapabilities.Discovery | PlatformCapabilities.Launch;
    public override string Limitations => "Detects installs from HKLM\\SOFTWARE\\Rockstar Games and launches the game executable. No playtime/achievement/DLC API. Games already found via Steam/Epic are not duplicated.";

    public override Task<DiscoveryResult> DiscoverAsync(CancellationToken ct) => Run(() =>
    {
        const string root = @"SOFTWARE\Rockstar Games";
        var names = Reg.SubKeys(RegistryHive.LocalMachine, RegistryView.Registry32, root);
        var games = new List<DiscoveredGame>();
        foreach (var n in names)
        {
            if (n.Contains("Launcher", StringComparison.OrdinalIgnoreCase) || n.Contains("Social Club", StringComparison.OrdinalIgnoreCase)) continue;
            var dir = Reg.Str(RegistryHive.LocalMachine, RegistryView.Registry32, root + "\\" + n, "InstallFolder");
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) games.Add(new DiscoveredGame(n, n, dir));
        }
        return names.Length > 0 ? new DiscoveryResult(true, games) : DiscoveryResult.NotPresent("Rockstar Games Launcher not found.");
    });
}

public sealed class RiotIntegration : PlatformIntegrationBase
{
    public override string Id => PlatformIds.Riot;
    public override string DisplayName => "Riot Client";
    public override PlatformCapabilities Capabilities => PlatformCapabilities.Discovery | PlatformCapabilities.Launch;
    public override string Limitations => "Reads Riot's product_settings metadata and launches via RiotClientServices.exe. Riot exposes no playtime/achievement/DLC data to third-party apps.";

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["valorant"] = "VALORANT", ["league_of_legends"] = "League of Legends", ["bacon"] = "Legends of Runeterra", ["lor"] = "Legends of Runeterra"
    };
    private static readonly Regex PathLine = new(@"product_install_full_path:\s*""?([^""\r\n]+)""?", RegexOptions.Compiled);

    public static string? ParseInstallPath(string yaml) { var m = PathLine.Match(yaml); return m.Success ? m.Groups[1].Value.Trim() : null; }

    public override Task<DiscoveryResult> DiscoverAsync(CancellationToken ct) => Run(() =>
    {
        var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var meta = Path.Combine(pd, "Riot Games", "Metadata");
        var installs = Path.Combine(pd, "Riot Games", "RiotClientInstalls.json");
        if (!Directory.Exists(meta)) return DiscoveryResult.NotPresent("Riot Client not found.");
        string? client = null;
        try
        {
            if (File.Exists(installs))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(installs));
                foreach (var key in new[] { "rc_default", "rc_live" })
                    if (doc.RootElement.TryGetProperty(key, out var v) && v.GetString() is { } p && File.Exists(p)) { client = p; break; }
            }
        }
        catch { }
        var games = new List<DiscoveredGame>();
        foreach (var d in Directory.EnumerateDirectories(meta))
        {
            var folder = Path.GetFileName(d);                        // e.g. valorant.live
            var product = folder.Split('.')[0];
            var patchline = folder.Contains('.') ? folder[(folder.IndexOf('.') + 1)..] : "live";
            if (product.StartsWith("riot_client", StringComparison.OrdinalIgnoreCase)) continue;
            var yaml = Directory.EnumerateFiles(d, "*.product_settings.yaml").FirstOrDefault();
            if (yaml is null) continue;
            var dir = ParseInstallPath(File.ReadAllText(yaml));
            if (string.IsNullOrEmpty(dir)) continue;
            dir = Path.GetFullPath(dir);
            if (!Directory.Exists(dir)) continue;
            var title = Names.TryGetValue(product, out var n) ? n : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(product.Replace('_', ' '));
            games.Add(new DiscoveredGame(folder, title, dir, client, null, client is null ? null : $"--launch-product={product} --launch-patchline={patchline}"));
        }
        return new DiscoveryResult(true, games);
    });
}
