namespace GameIndex.Core.Dlc;

/// <summary>Detects installed DLC through platform metadata. Uninstalled/owned-only DLC is never counted as installed.</summary>
public sealed class DlcService
{
    private readonly Db _db;
    private readonly IReadOnlyList<IPlatformIntegration> _integrations;
    public DlcService(Db db, IReadOnlyList<IPlatformIntegration> integrations) { _db = db; _integrations = integrations; }

    public async Task<Result<IReadOnlyList<DlcInfo>>> RefreshAsync(Game g, IntegrationContext ctx, CancellationToken ct)
    {
        var integ = _integrations.FirstOrDefault(i => i.Id == g.PlatformId);
        if (integ is null || g.IsManual || (integ.Capabilities & PlatformCapabilities.InstalledDlc) == 0)
        {
            _db.SetDlcs(g.Id, null);
            return Result<IReadOnlyList<DlcInfo>>.Fail(g.IsManual ? "Manual games have no DLC source." : $"{g.PlatformName} does not expose installed DLC.");
        }
        var known = _db.GetDlcs(g.Id).ToDictionary(d => d.ExternalId, d => d.Name);
        var r = await integ.GetDlcAsync(g, ctx with { KnownDlcNames = known }, ct);
        _db.SetDlcs(g.Id, r.Ok ? r.Value : null);
        return r;
    }
}
