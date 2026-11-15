using Microsoft.Win32;

namespace GameIndex.Core.Integrations;

[Flags]
public enum PlatformCapabilities
{
    None = 0, Discovery = 1, Launch = 2, InstalledDlc = 4, Achievements = 8, Playtime = 16, LastPlayed = 32, Artwork = 64
}

public sealed record DiscoveredGame(string ExternalId, string Title, string? InstallDir, string? ExePath = null,
    string? LaunchUri = null, string? LaunchArgs = null, string? WorkingDir = null);

public sealed record DiscoveryResult(bool PlatformPresent, IReadOnlyList<DiscoveredGame> Games, string? Message = null)
{
    public static DiscoveryResult NotPresent(string? message = null) => new(false, Array.Empty<DiscoveredGame>(), message);
}

public sealed record ImportedStats(long? PlaytimeSeconds, DateTime? LastPlayedUtc);
public sealed record AchievementData(IReadOnlyList<AchievementRecord> Items);
public sealed record DlcInfo(string ExternalId, string Name, bool Installed);

/// <summary>Either a value or a human readable reason why the data is unavailable. Never a fabricated value.</summary>
public sealed record Result<T>(T? Value, string? Reason = null) where T : class
{
    public bool Ok => Value is not null;
    public static Result<T> Fail(string reason) => new(null, reason);
}

public sealed record IntegrationContext(string? SteamApiKey, string? SteamId64, HttpClient Http, IReadOnlyDictionary<string, string> KnownDlcNames);

public interface IPlatformIntegration
{
    string Id { get; }
    string DisplayName { get; }
    PlatformCapabilities Capabilities { get; }
    /// <summary>Plain-language description of what this integration cannot do (shown in Settings).</summary>
    string Limitations { get; }
    Task<DiscoveryResult> DiscoverAsync(CancellationToken ct);
    Task<Result<ImportedStats>> ImportStatsAsync(Game game, IntegrationContext ctx, CancellationToken ct);
    Task<Result<AchievementData>> GetAchievementsAsync(Game game, IntegrationContext ctx, CancellationToken ct);
    Task<Result<IReadOnlyList<DlcInfo>>> GetDlcAsync(Game game, IntegrationContext ctx, CancellationToken ct);
}

public abstract class PlatformIntegrationBase : IPlatformIntegration
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract PlatformCapabilities Capabilities { get; }
    public abstract string Limitations { get; }
    public abstract Task<DiscoveryResult> DiscoverAsync(CancellationToken ct);

    public virtual Task<Result<ImportedStats>> ImportStatsAsync(Game g, IntegrationContext x, CancellationToken ct) =>
        Task.FromResult(Result<ImportedStats>.Fail($"{DisplayName} does not expose playtime to third-party apps."));
    public virtual Task<Result<AchievementData>> GetAchievementsAsync(Game g, IntegrationContext x, CancellationToken ct) =>
        Task.FromResult(Result<AchievementData>.Fail($"{DisplayName} does not expose achievements to third-party apps."));
    public virtual Task<Result<IReadOnlyList<DlcInfo>>> GetDlcAsync(Game g, IntegrationContext x, CancellationToken ct) =>
        Task.FromResult(Result<IReadOnlyList<DlcInfo>>.Fail($"{DisplayName} does not expose installed DLC information."));

    protected static Task<DiscoveryResult> Run(Func<DiscoveryResult> work) => Task.Run(work);
}

internal static class Reg
{
    public static string? Str(RegistryHive hive, RegistryView view, string path, string name)
    {
        try { using var b = RegistryKey.OpenBaseKey(hive, view); using var k = b.OpenSubKey(path); return k?.GetValue(name)?.ToString(); }
        catch { return null; }
    }

    public static RegistryKey? Open(RegistryHive hive, RegistryView view, string path)
    {
        try { using var b = RegistryKey.OpenBaseKey(hive, view); return b.OpenSubKey(path); } catch { return null; }
    }

    public static string[] SubKeys(RegistryHive hive, RegistryView view, string path)
    {
        try { using var k = Open(hive, view, path); return k?.GetSubKeyNames() ?? []; } catch { return []; }
    }
}

/// <summary>Minimal Valve KeyValues (VDF/ACF) text parser. Keys are case-insensitive.</summary>
public sealed class VdfNode
{
    public string Key { get; init; } = "";
    public string? Value { get; set; }
    public List<VdfNode> Children { get; } = new();
    public VdfNode? this[string key] => Children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
    public string? GetString(string key) => this[key]?.Value;
    public long? GetLong(string key) => long.TryParse(GetString(key), out var v) ? v : null;
}

public static class VdfParser
{
    public static VdfNode Parse(string text)
    {
        var tokens = Tokenize(text);
        var root = new VdfNode { Key = "root" };
        var i = 0;
        ParseInto(root, tokens, ref i, topLevel: true);
        return root;
    }

    private static void ParseInto(VdfNode parent, List<(string Text, bool Quoted)> t, ref int i, bool topLevel)
    {
        while (i < t.Count)
        {
            var (tok, quoted) = t[i++];
            if (!quoted && tok == "}") { if (topLevel) continue; return; }
            var node = new VdfNode { Key = tok };
            if (i < t.Count)
            {
                var (next, nq) = t[i];
                if (!nq && next == "{") { i++; ParseInto(node, t, ref i, false); }
                else { node.Value = next; i++; }
            }
            parent.Children.Add(node);
        }
    }

    private static List<(string, bool)> Tokenize(string s)
    {
        var list = new List<(string, bool)>();
        for (var p = 0; p < s.Length;)
        {
            var ch = s[p];
            if (char.IsWhiteSpace(ch)) { p++; continue; }
            if (ch == '/' && p + 1 < s.Length && s[p + 1] == '/') { while (p < s.Length && s[p] != '\n') p++; continue; }
            if (ch == '{' || ch == '}') { list.Add((ch.ToString(), false)); p++; continue; }
            if (ch == '"')
            {
                p++;
                var sb = new System.Text.StringBuilder();
                while (p < s.Length && s[p] != '"')
                {
                    if (s[p] == '\\' && p + 1 < s.Length)
                    {
                        p++;
                        sb.Append(s[p] switch { 'n' => '\n', 't' => '\t', '\\' => '\\', '"' => '"', var o => o });
                    }
                    else sb.Append(s[p]);
                    p++;
                }
                p++;
                list.Add((sb.ToString(), true));
                continue;
            }
            var start = p;
            while (p < s.Length && !char.IsWhiteSpace(s[p]) && s[p] != '{' && s[p] != '}' && s[p] != '"') p++;
            list.Add((s[start..p], false));
        }
        return list;
    }
}

/// <summary>Heuristic main-executable finder for platforms that do not tell us the EXE.</summary>
public static class ExeFinder
{
    private static readonly System.Text.RegularExpressions.Regex Ignore = new(
        @"unins|uninst|setup|install|redist|vcredist|dxsetup|dotnet|crash|report|helper|updater|bootstrap|cef|eac|easyanticheat|battleye|vc_|ue4prereq|prereq|diagnostic|benchmark|support",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string? FindMainExe(string dir, int maxDepth = 2)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            FileInfo? best = null;
            void Walk(string d, int depth)
            {
                try
                {
                    foreach (var f in new DirectoryInfo(d).EnumerateFiles("*.exe"))
                        if (!Ignore.IsMatch(f.Name) && (best is null || f.Length > best.Length)) best = f;
                    if (depth >= maxDepth) return;
                    foreach (var sub in Directory.EnumerateDirectories(d)) Walk(sub, depth + 1);
                }
                catch { }
            }
            Walk(dir, 0);
            return best?.FullName;
        }
        catch { return null; }
    }
}
