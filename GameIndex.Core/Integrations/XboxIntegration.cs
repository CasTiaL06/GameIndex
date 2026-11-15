using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace GameIndex.Core.Integrations;

public sealed class XboxIntegration : PlatformIntegrationBase
{
    public override string Id => PlatformIds.Xbox;
    public override string DisplayName => "Xbox / Microsoft Store";
    public override PlatformCapabilities Capabilities => PlatformCapabilities.Discovery | PlatformCapabilities.Launch;
    public override string Limitations => "Scans the XboxGames folder on every fixed drive (Xbox app default install location) and launches via the package's AppsFolder entry. Xbox achievements/playtime need Xbox Live sign-in (not implemented); DLC is not exposed.";

    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    /// <summary>Computes a package family name from identity name + publisher (the 13 character publisher id hash).</summary>
    public static string PackageFamilyName(string name, string publisher)
    {
        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        ulong v = 0;
        for (var i = 0; i < 8; i++) v = (v << 8) | hash[i];
        var sb = new StringBuilder();
        for (var i = 0; i < 13; i++)
        {
            var shift = 64 - 5 * (i + 1);
            var idx = shift >= 0 ? (int)((v >> shift) & 31) : (int)((v << -shift) & 31);
            sb.Append(Alphabet[idx]);
        }
        return $"{name}_{sb}";
    }

    public override Task<DiscoveryResult> DiscoverAsync(CancellationToken ct) => Run(() =>
    {
        var games = new List<DiscoveredGame>();
        var anyRoot = false;
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var root = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            if (!Directory.Exists(root)) continue;
            anyRoot = true;
            foreach (var gameDir in Directory.EnumerateDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var content = Path.Combine(gameDir, "Content");
                    var manifest = Path.Combine(content, "appxmanifest.xml");
                    if (!File.Exists(manifest)) continue;
                    var x = XDocument.Load(manifest);
                    var identity = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity");
                    var app = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "Application");
                    var name = identity?.Attribute("Name")?.Value; var pub = identity?.Attribute("Publisher")?.Value;
                    if (name is null || pub is null || app is null) continue;
                    var appId = app.Attribute("Id")?.Value ?? "Game";
                    var exeName = app.Attribute("Executable")?.Value;
                    var exe = exeName is null ? null : Path.Combine(content, exeName);
                    games.Add(new DiscoveredGame(name, new DirectoryInfo(gameDir).Name, content, exe is not null && File.Exists(exe) ? exe : null,
                        $"shell:AppsFolder\\{PackageFamilyName(name, pub)}!{appId}"));
                }
                catch (Exception ex) { Log.Warn($"Xbox package {Path.GetFileName(gameDir)} skipped: {ex.Message}"); }
            }
        }
        return anyRoot ? new DiscoveryResult(true, games) : DiscoveryResult.NotPresent("No XboxGames folder found.");
    });
}

/// <summary>Standalone games from user-configured scan directories: each sub folder that contains a plausible game EXE.</summary>
public sealed class StandaloneScanner : PlatformIntegrationBase
{
    private readonly Func<IReadOnlyList<string>> _dirs;
    public StandaloneScanner(Func<IReadOnlyList<string>> scanDirectories) => _dirs = scanDirectories;

    public override string Id => PlatformIds.Standalone;
    public override string DisplayName => "Standalone";
    public override PlatformCapabilities Capabilities => PlatformCapabilities.Discovery | PlatformCapabilities.Launch;
    public override string Limitations => "Scans only the folders you add under Settings > Scan directories (never the whole disk). Each sub folder with a plausible game EXE becomes a game; the EXE is a best guess, so correct it with Edit if needed.";

    public override Task<DiscoveryResult> DiscoverAsync(CancellationToken ct) => Run(() =>
    {
        var dirs = _dirs().Where(Directory.Exists).ToList();
        if (dirs.Count == 0) return DiscoveryResult.NotPresent("No scan directories configured.");
        var games = new List<DiscoveredGame>();
        foreach (var root in dirs)
            foreach (var d in Directory.EnumerateDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                var exe = ExeFinder.FindMainExe(d);
                if (exe is null) continue;
                games.Add(new DiscoveredGame(PathUtil.Norm(d).ToLowerInvariant(), new DirectoryInfo(d).Name, d, exe));
            }
        return new DiscoveryResult(true, games);
    });
}
