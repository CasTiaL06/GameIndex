using Microsoft.Win32;

namespace GameIndex.Core.Integrations;

public sealed class GogIntegration : PlatformIntegrationBase
{
    public override string Id => PlatformIds.Gog;
    public override string DisplayName => "GOG";
    public override PlatformCapabilities Capabilities => PlatformCapabilities.Discovery | PlatformCapabilities.Launch | PlatformCapabilities.InstalledDlc;
    public override string Limitations => "Reads GOG's install registry entries (works for games installed by GOG Galaxy or the offline installers). GOG Galaxy's playtime/achievements live in an undocumented internal database and are not read.";

    private const string Path_ = @"SOFTWARE\GOG.com\Games";

    private static List<(string Id, string? Name, string? Dir, string? Exe, string? Param, string? Work, string? DependsOn)> ReadAll()
    {
        var list = new List<(string, string?, string?, string?, string?, string?, string?)>();
        foreach (var name in Reg.SubKeys(RegistryHive.LocalMachine, RegistryView.Registry32, Path_))
        {
            using var k = Reg.Open(RegistryHive.LocalMachine, RegistryView.Registry32, Path_ + "\\" + name);
            if (k is null) continue;
            string? V(string n) => k.GetValue(n)?.ToString();
            list.Add((V("gameID") ?? name, V("gameName"), V("path"), V("exe"), V("launchParam"), V("workingDir"), V("dependsOn")));
        }
        return list;
    }

    public override Task<DiscoveryResult> DiscoverAsync(CancellationToken ct) => Run(() =>
    {
        var all = ReadAll();
        if (all.Count == 0) return DiscoveryResult.NotPresent("No GOG games registered.");
        var games = all.Where(a => string.IsNullOrEmpty(a.DependsOn) && !string.IsNullOrEmpty(a.Dir) && Directory.Exists(a.Dir))
            .Select(a => new DiscoveredGame(a.Id, a.Name ?? a.Id, a.Dir, !string.IsNullOrEmpty(a.Exe) && File.Exists(a.Exe) ? a.Exe : null,
                null, string.IsNullOrWhiteSpace(a.Param) ? null : a.Param, a.Work)).ToList();
        return new DiscoveryResult(true, games);
    });

    public override Task<Result<IReadOnlyList<DlcInfo>>> GetDlcAsync(Game g, IntegrationContext x, CancellationToken ct) => Task.Run(() =>
    {
        IReadOnlyList<DlcInfo> list = ReadAll().Where(a => string.Equals(a.DependsOn, g.ExternalId, StringComparison.Ordinal))
            .Select(a => new DlcInfo(a.Id, a.Name ?? $"DLC {a.Id}", true)).ToList();
        return new Result<IReadOnlyList<DlcInfo>>(list);
    }, ct);
}
