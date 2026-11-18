using CommunityToolkit.Mvvm.ComponentModel;
using GameIndex.Core;
using GameIndex.Core.Integrations;
using GameIndex.Core.Models;
using GameIndex.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GameIndex.ViewModels;

public sealed partial class GameViewModel : ObservableObject
{
    public static Dictionary<string, PlatformCapabilities> Capabilities { get; } = new();
    public static bool HasSteamKey { get; set; }

    private readonly Action<GameViewModel> _select, _launch;
    private string? _coverPath, _bgPath, _iconPath;
    private string? _artPath;
    private BitmapImage? _cover, _bg, _icon, _art;

    public Game Model { get; private set; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isRunning;

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(StatusText));

    public GameViewModel(Game g, Action<GameViewModel> select, Action<GameViewModel> launch) { Model = g; _select = select; _launch = launch; }

    public void Update(Game g) { Model = g; OnPropertyChanged(string.Empty); }
    public void RequestSelect() => _select(this);
    public void RequestLaunch() => _launch(this);

    private static BitmapImage? Load(string? path, ref string? loadedPath, ref BitmapImage? cache, int width)
    {
        if (path != loadedPath) { loadedPath = path; cache = null; }
        if (cache is null && !string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                var bi = new BitmapImage();
                if (width > 0) { bi.DecodePixelWidth = width; bi.DecodePixelType = DecodePixelType.Logical; }   // width 0 = natural size
                bi.UriSource = new Uri(path);
                cache = bi;
            }
            catch { cache = null; }
        }
        return cache;
    }

    public ImageSource? CoverImage => Load(Model.CoverPath, ref _coverPath, ref _cover, 320);
    /// <summary>Only a real wide background/hero image. Covers and icons are never stretched over the whole window.</summary>
    public ImageSource? BackgroundImage => Load(Model.BackgroundPath, ref _bgPath, ref _bg, 1920);
    /// <summary>Cover (or icon) shown fitted, un-cropped, when the game has no background image.</summary>
    public ImageSource? ArtworkImage => Load(Model.CoverPath ?? Model.IconPath, ref _artPath, ref _art, 0);
    public ImageSource? IconImage => Load(Model.IconPath, ref _iconPath, ref _icon, 128);
    public bool HasCover => CoverImage is not null;

    public long Id => Model.Id;
    public string Title => Model.Title;
    public string PlatformName => Model.PlatformName;
    public string PlatformBadge => Model.PlatformName.ToUpperInvariant();
    public string Category => Model.Category ?? "";
    public bool IsFavorite => Model.IsFavorite;
    public string FavoriteGlyph => Model.IsFavorite ? "\uEB52" : "\uEB51";
    public string FavoriteLabel => Loc.T(Model.IsFavorite ? "Unfavorite" : "Favorite");
    public string StatusText => Loc.T(IsRunning ? "RUNNING" : (Model.IsInstalled ? "INSTALLED" : "NOT INSTALLED"));
    public string InstallDir => Model.InstallDir ?? "—";
    public string ExePath => !string.IsNullOrEmpty(Model.ExePath) ? Model.ExePath! : (string.IsNullOrEmpty(Model.LaunchUri) ? "—" : Model.PlatformName);
    public string LaunchInfo => !string.IsNullOrEmpty(Model.LaunchUri) ? Model.LaunchUri! : "EXE";
    public bool IsManual => Model.IsManual;

    // ---- the five headline statistics ----
    public string PlaytimeText => Model.TotalPlaytimeSeconds <= 0 && Model.ImportedPlaytimeSeconds is null ? Loc.T("Not played yet") : Loc.Duration(Model.TotalPlaytimeSeconds);
    public string PlaytimeDetail => Model.ImportedPlaytimeSeconds is long imp
        ? Loc.F("Imported from {0}: {1} • Tracked by GameIndex since: {2}", Model.PlatformName, Loc.Duration(imp), Loc.Duration(Model.TrackedSinceImportSeconds))
        : Loc.T(Model.LocalPlaytimeSeconds > 0 ? "Tracked by GameIndex" : "Tracked by GameIndex (no sessions yet)");

    public string SizeText => !Model.IsInstalled ? Loc.T("Not installed") : Model.SizeBytes is long b ? Format.Size(b) : Loc.T("Calculating…");
    public string SizeDetail => Model.SizeUpdatedUtc is DateTime t ? Loc.F("Installed size • updated {0}", Format.Date(t)) : Loc.T("Installed size on disk");

    private bool DlcSupported => Capabilities.TryGetValue(Model.PlatformId, out var c) && (c & PlatformCapabilities.InstalledDlc) != 0 && !Model.IsManual;
    public string DlcText => Model.DlcInstalled is int n ? n.ToString(CultureInfo.CurrentCulture) : Loc.T(DlcSupported && Model.DlcUpdatedUtc is null ? "Checking…" : "Not available");
    public string DlcDetail => Model.DlcSource == "folders" && Model.DlcInstalled is not null ? Loc.T("Estimated from the game's DLC folders") : Model.DlcInstalled is not null ? Loc.T("Installed DLC only") : (DlcSupported ? Loc.T("Could not be read") : Loc.F("Not exposed by {0}", Model.PlatformName));

    public string AchievementsText => Model.AchUnlocked is int u && Model.AchTotal is int t && t > 0 ? Loc.F("{0} / {1} UNLOCKED", u, t) : Loc.T("NOT AVAILABLE");
    public string AchievementsDetail
    {
        get
        {
            if (Model.AchUnlocked is int u && Model.AchTotal is int t && t > 0) return Loc.F("{0}% COMPLETED", (int)Math.Round(u * 100.0 / t));
            if (Model.IsManual) return Loc.T("Manual game");
            if (Model.PlatformId != PlatformIds.Steam) return Loc.F("Not exposed by {0}", Model.PlatformName);
            return Loc.T("No data: set your Steam profile's Game details to Public");
        }
    }
    public double AchievementProgress => AchievementPercent;     // 0..100, or -1 when unavailable (progress bar hidden)
    public int AchievementPercent => Model.AchUnlocked is int u && Model.AchTotal is int t && t > 0 ? (int)Math.Round(u * 100.0 / t) : -1;

    public string LastPlayedText => Model.LastPlayedUtc is DateTime d ? Format.Date(d) : Loc.T("Never");
    public string LastPlayedDetail => Model.LastPlayedUtc is null ? Loc.T("No sessions recorded") : (Model.LastPlayedIsImported ? Loc.F("Imported from {0}", Model.PlatformName) : Loc.T("Tracked by GameIndex"));

    /// <summary>Initials for the generated fallback card (shown when a game has neither cover nor icon).</summary>
    public string Monogram
    {
        get
        {
            var words = Model.Title.Split(new[] { ' ', '-', ':', '_' }, StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).Take(2).ToList();
            return words.Count == 0 ? "?" : string.Concat(words.Select(w => char.ToUpperInvariant(w[0])));
        }
    }
}

public sealed record AchievementItem(string Name, string Description, string Status, bool Unlocked, string? IconUrl)
{
    public double ItemOpacity => Unlocked ? 1 : 0.55;
    public ImageSource? Icon => string.IsNullOrEmpty(IconUrl) ? null : new BitmapImage(new Uri(IconUrl)) { DecodePixelWidth = 64 };
}
public sealed record DlcItem(string Name, string Status);
public sealed record SessionItem(string Start, string Duration, string Note);

public static class GameViewModelExtensions
{
    public static void OnPropertyChangedPublic(this GameViewModel v) => v.Update(v.Model);
}
