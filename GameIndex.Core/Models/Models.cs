namespace GameIndex.Core.Models;

public static class PlatformIds
{
    public const string Steam = "steam", Epic = "epic", Gog = "gog", Ubisoft = "ubisoft", Ea = "ea",
        Xbox = "xbox", BattleNet = "battlenet", Rockstar = "rockstar", Riot = "riot",
        Standalone = "standalone", Manual = "manual";

    public static readonly IReadOnlyList<(string Id, string Name)> All =
    [
        (Steam, "Steam"), (Epic, "Epic Games"), (Gog, "GOG"), (Ubisoft, "Ubisoft Connect"), (Ea, "EA App"),
        (Xbox, "Xbox"), (BattleNet, "Battle.net"), (Rockstar, "Rockstar Games"), (Riot, "Riot Client"),
        (Standalone, "Standalone"), (Manual, "Manual")
    ];

    public static string DisplayName(string id) => All.FirstOrDefault(p => p.Id == id).Name ?? id;
}

public sealed class Game
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string PlatformId { get; set; } = PlatformIds.Manual;
    /// <summary>User supplied platform name for manual games ("Custom platform").</summary>
    public string? CustomPlatformName { get; set; }
    public string ExternalId { get; set; } = "";
    public string? InstallDir { get; set; }
    public string? ExePath { get; set; }
    public string? LaunchArgs { get; set; }
    public string? WorkingDir { get; set; }
    public string? LaunchUri { get; set; }
    public bool IsManual { get; set; }
    public bool IsInstalled { get; set; } = true;
    public bool IsFavorite { get; set; }
    /// <summary>When true, automatic scans never overwrite title / exe / args of this game.</summary>
    public bool UserEdited { get; set; }
    /// <summary>Manual games: track any process inside the game's folder instead of only the exact EXE.</summary>
    public bool TrackFolder { get; set; }
    public string? Category { get; set; }
    public string? CoverPath { get; set; }
    public string? BackgroundPath { get; set; }
    public string? LogoPath { get; set; }
    public string? IconPath { get; set; }
    public DateTime DateAddedUtc { get; set; } = DateTime.UtcNow;

    public long? SizeBytes { get; set; }
    public DateTime? SizeUpdatedUtc { get; set; }

    public long? ImportedPlaytimeSeconds { get; set; }
    public long LocalAtImportSeconds { get; set; }
    public DateTime? ImportedLastPlayedUtc { get; set; }
    /// <summary>Playtime recorded by GameIndex sessions (all time).</summary>
    public long LocalPlaytimeSeconds { get; set; }
    public DateTime? LocalLastPlayedUtc { get; set; }

    public int? AchUnlocked { get; set; }
    public int? AchTotal { get; set; }
    public DateTime? AchUpdatedUtc { get; set; }
    public int? DlcInstalled { get; set; }
    public DateTime? DlcUpdatedUtc { get; set; }
    public DateTime? StatsUpdatedUtc { get; set; }

    public string PlatformName => !string.IsNullOrWhiteSpace(CustomPlatformName) ? CustomPlatformName! : PlatformIds.DisplayName(PlatformId);

    /// <summary>
    /// Imported platform playtime + only the GameIndex time tracked since that import.
    /// This never double counts: a later platform import already contains our earlier sessions.
    /// </summary>
    public long TrackedSinceImportSeconds => ImportedPlaytimeSeconds is null ? LocalPlaytimeSeconds : Math.Max(0, LocalPlaytimeSeconds - LocalAtImportSeconds);
    public long TotalPlaytimeSeconds => (ImportedPlaytimeSeconds ?? 0) + TrackedSinceImportSeconds;

    public DateTime? LastPlayedUtc =>
        (LocalLastPlayedUtc, ImportedLastPlayedUtc) switch
        {
            (null, null) => null,
            (var l, null) => l,
            (null, var i) => i,
            (var l, var i) => l > i ? l : i
        };
    public bool LastPlayedIsImported => ImportedLastPlayedUtc is not null && (LocalLastPlayedUtc is null || ImportedLastPlayedUtc > LocalLastPlayedUtc);
}

public sealed record SessionRecord(long Id, long GameId, DateTime StartUtc, DateTime? EndUtc, DateTime HeartbeatUtc, bool Recovered)
{
    public TimeSpan Duration => (EndUtc ?? HeartbeatUtc) - StartUtc;
}

public sealed record AchievementRecord(string ApiName, string Name, string? Description, string? IconUrl, string? IconGrayUrl, bool Unlocked, DateTime? UnlockedUtc);

public sealed record IntegrationStatus(string PlatformId, bool Enabled, string? Status, string? Message, DateTime? LastScanUtc, int GameCount);
