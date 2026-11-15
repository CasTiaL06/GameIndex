namespace GameIndex.Core.Achievements;

/// <summary>Loads achievements through the game's platform integration and stores them. Never fabricates counts: unavailable stays unavailable.</summary>
public sealed class AchievementService
{
    private readonly Db _db;
    private readonly IReadOnlyList<IPlatformIntegration> _integrations;
    public AchievementService(Db db, IReadOnlyList<IPlatformIntegration> integrations) { _db = db; _integrations = integrations; }

    public async Task<Result<AchievementData>> RefreshAsync(Game g, IntegrationContext ctx, CancellationToken ct)
    {
        var integ = _integrations.FirstOrDefault(i => i.Id == g.PlatformId);
        if (integ is null || g.IsManual || (integ.Capabilities & PlatformCapabilities.Achievements) == 0)
        {
            _db.SetAchievements(g.Id, null);
            return Result<AchievementData>.Fail(g.IsManual ? "Manual games have no achievement source." : $"{g.PlatformName} does not expose achievements to third-party apps.");
        }
        var r = await integ.GetAchievementsAsync(g, ctx, ct);
        _db.SetAchievements(g.Id, r.Ok ? r.Value!.Items : null);
        return r;
    }
}
