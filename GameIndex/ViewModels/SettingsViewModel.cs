using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GameIndex.Core.Integrations;
using GameIndex.Core.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GameIndex.ViewModels;

public sealed record IconStyleItem(string Id, string Name, ImageSource Preview);

public sealed partial class IntegrationRow : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string State { get; init; } = "";
    public string Capabilities { get; init; } = "";
    public string Limitations { get; init; } = "";
    [ObservableProperty] private bool _isEnabled = true;
}

/// <summary>Two-way bindable view of AppSettings. Any property change is written to settings.json and applied live.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppHost _h;
    private readonly Action _applied;
    private bool _loading = true;
    private readonly DispatcherQueueTimer _debounce;
    private bool _lastStartWithWindows;

    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private string _accentHex = "#65D9FF";
    [ObservableProperty] private bool _taskbarJumpList = true;
    [ObservableProperty] private bool _hotkeyEnabled = true;
    [ObservableProperty] private int _hotkeyIndex;
    [ObservableProperty] private bool _floatingIconEnabled = true;
    [ObservableProperty] private bool _floatingHideWhenOpen;
    [ObservableProperty] private int _floatingModeIndex;
    [ObservableProperty] private double _floatingSize = 84;
    [ObservableProperty] private double _floatingOpacity = 92;
    [ObservableProperty] private int _floatingStyleIndex;
    [ObservableProperty] private int _floatingAnimationIndex;
    [ObservableProperty] private bool _keepRunningInBackground = true;
    [ObservableProperty] private int _startupSoundIndex;
    [ObservableProperty] private int _artFitIndex;
    [ObservableProperty] private int _roundnessIndex = 2;
    [ObservableProperty] private double _materialVisibility = 25;
    [ObservableProperty] private bool _introAnimation = true;

    public IReadOnlyList<IconStyleItem> IconStyles { get; } = new[]
        {
            ("g_neon", "G Neon"), ("g_gradient", "G Gradient"), ("g_orb", "G Orb"), ("g_hex", "G Hex"), ("g_shield", "G Shield"), ("g_minimal", "G Minimal"),
            ("planet", "Planet"), ("crystal", "Crystal"), ("star", "Star"), ("atom", "Atom"), ("pad", "Pad"), ("ember", "Ember")
        }
        .Select(x => new IconStyleItem(x.Item1, x.Item2, new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "FloatingIcons", x.Item1 + "_base.png"))) { DecodePixelWidth = 128 })).ToList();

    public void PreviewStartupSound() => Services.SoundService.PlayStartup(Sounds[Math.Clamp(StartupSoundIndex, 0, Sounds.Length - 1)]);
    [ObservableProperty] private bool _soundsEnabled = true;
    public event Action? ResetIconRequested;
    public void RequestResetIcon() => ResetIconRequested?.Invoke();
    [ObservableProperty] private int _languageIndex = 2;
    [ObservableProperty] private bool _transparencyEnabled = true;
    [ObservableProperty] private double _overlayOpacity = 95;
    [ObservableProperty] private double _transparency = 72;
    [ObservableProperty] private int _backdropIndex;
    [ObservableProperty] private double _backgroundDim = 35;
    [ObservableProperty] private bool _animationsEnabled = true;
    [ObservableProperty] private double _animationSpeed = 100;
    [ObservableProperty] private bool _sidebarCompact = true;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _autoDetect = true;
    [ObservableProperty] private double _scanInterval = 15;
    [ObservableProperty] private int _defaultViewIndex;
    [ObservableProperty] private bool _autoMetadata = true;
    [ObservableProperty] private string _steamId = "";
    [ObservableProperty] private string _backupDirectory = "";
    [ObservableProperty] private string _customBackground = "";
    [ObservableProperty] private string _message = "";

    public ObservableCollection<string> ScanDirectories { get; } = new();
    public ObservableCollection<IntegrationRow> Integrations { get; } = new();

    public string DataLocation => _h.Paths.Root;
    public string ModeText => _h.Paths.IsPortable ? Services.Loc.T("Portable mode: all data is stored next to GameIndex.exe.") : (_h.Paths.Warning ?? "Installed mode");
    public string DatabasePath => _h.Paths.DatabaseFile;
    public string ArtworkPath => Path.Combine(_h.Paths.Root, "Assets");
    public string BackupPath => string.IsNullOrEmpty(BackupDirectory) ? _h.Paths.BackupsDir : BackupDirectory;
    public string SteamKeyState => Services.Loc.T(string.IsNullOrEmpty(_h.Settings.GetSteamApiKey()) ? "No API key saved" : "API key saved (encrypted with Windows DPAPI)");

    private static readonly string[] Themes = ["Dark", "Light", "System"], Langs = ["en", "tr", "system"], Hotkeys = ["Alt+G", "Ctrl+Alt+G", "Ctrl+Shift+G", "F9"], Modes = ["desktop", "always"], StyleIds = ["g_neon", "g_gradient", "g_orb", "g_hex", "g_shield", "g_minimal", "planet", "crystal", "star", "atom", "pad", "ember"],
        Animations = ["classic", "breathe", "float", "flip", "orbit", "ripples", "sparkle", "jelly"],
        Sounds = ["aurora", "crystal", "deep", "pulse", "retro", "zen", "off"], Fits = ["fit", "fill"], Backdrops = ["Mica", "MicaAlt", "Acrylic", "None"], Views = ["Carousel", "Grid", "List"];

    public SettingsViewModel(AppHost host, Action applied)
    {
        _h = host; _applied = applied;
        _debounce = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(250);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => Flush();
        Load();
    }

    public void Load()
    {
        _loading = true;
        var s = _h.Settings.Current;
        ThemeIndex = Math.Max(0, Array.IndexOf(Themes, s.Theme)); AccentHex = s.AccentColor;
        TaskbarJumpList = s.TaskbarJumpList; HotkeyEnabled = s.HotkeyEnabled; HotkeyIndex = Math.Max(0, Array.IndexOf(Hotkeys, s.Hotkey));
        FloatingIconEnabled = s.FloatingIconEnabled; FloatingHideWhenOpen = s.FloatingHideWhenOpen;
        FloatingModeIndex = Math.Max(0, Array.IndexOf(Modes, s.FloatingMode)); FloatingSize = s.FloatingSize; FloatingOpacity = s.FloatingOpacity;
        FloatingStyleIndex = Math.Max(0, Array.IndexOf(StyleIds, s.FloatingStyle)); FloatingAnimationIndex = Math.Max(0, Array.IndexOf(Animations, s.FloatingAnimation)); KeepRunningInBackground = s.KeepRunningInBackground;
        StartupSoundIndex = Math.Max(0, Array.IndexOf(Sounds, s.StartupSound)); ArtFitIndex = Math.Max(0, Array.IndexOf(Fits, s.ArtFit)); RoundnessIndex = Math.Clamp(s.WindowRoundness, 0, 2); MaterialVisibility = s.MaterialVisibility; IntroAnimation = s.IntroAnimation; SoundsEnabled = s.SoundsEnabled; _lastStartWithWindows = s.StartWithWindows;
        LanguageIndex = Math.Max(0, Array.IndexOf(Langs, s.Language)); TransparencyEnabled = s.TransparencyEnabled; OverlayOpacity = s.OverlayOpacityPercent; Transparency = s.TransparencyPercent;
        BackdropIndex = Math.Max(0, Array.IndexOf(Backdrops, s.Backdrop)); BackgroundDim = s.BackgroundDimPercent;
        AnimationsEnabled = s.AnimationsEnabled; AnimationSpeed = s.AnimationSpeedPercent; SidebarCompact = s.SidebarCompact;
        StartWithWindows = s.StartWithWindows; MinimizeToTray = s.MinimizeToTray; AutoDetect = s.AutoDetectGames; ScanInterval = s.ScanIntervalMinutes;
        DefaultViewIndex = Math.Max(0, Array.IndexOf(Views, s.DefaultLibraryView)); AutoMetadata = s.AutoMetadataRefresh;
        SteamId = s.SteamId64 ?? ""; BackupDirectory = s.BackupDirectory ?? ""; CustomBackground = s.CustomBackgroundImage ?? "";
        ScanDirectories.Clear(); foreach (var d in s.ScanDirectories) ScanDirectories.Add(d);
        RefreshIntegrations();
        _loading = false;
    }

    public void RefreshIntegrations()
    {
        var status = _h.Db.GetIntegrationStatuses().ToDictionary(x => x.PlatformId);
        Integrations.Clear();
        foreach (var i in _h.Integrations)
        {
            status.TryGetValue(i.Id, out var st);
            var row = new IntegrationRow
            {
                Id = i.Id, Name = i.DisplayName,
                State = st?.Status is { } t ? $"{Services.Loc.T(t)}{(st.GameCount > 0 ? $" • {st.GameCount}" : "")}" : Services.Loc.T("Not scanned yet"),
                Capabilities = string.Join(", ", Enum.GetValues<PlatformCapabilities>().Where(c => c != PlatformCapabilities.None && i.Capabilities.HasFlag(c))),
                Limitations = i.Limitations, IsEnabled = !_h.Settings.Current.DisabledPlatforms.Contains(i.Id)
            };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(IntegrationRow.IsEnabled)) return;
                var list = _h.Settings.Current.DisabledPlatforms;
                list.Remove(row.Id); if (!row.IsEnabled) list.Add(row.Id);
                _h.Settings.Save();
            };
            Integrations.Add(row);
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is nameof(Message) or nameof(BackupPath) or nameof(SteamKeyState)) return;
        Commit();
    }

    private void Commit()
    {
        var s = _h.Settings.Current;
        s.Theme = Themes[Math.Clamp(ThemeIndex, 0, 2)]; s.AccentColor = AccentHex;
        s.Language = Langs[Math.Clamp(LanguageIndex, 0, 2)]; s.TransparencyEnabled = TransparencyEnabled; s.OverlayOpacityPercent = (int)OverlayOpacity; s.TransparencyPercent = (int)Transparency;
        s.Backdrop = Backdrops[Math.Clamp(BackdropIndex, 0, 3)]; s.BackgroundDimPercent = (int)BackgroundDim;
        s.AnimationsEnabled = AnimationsEnabled; s.AnimationSpeedPercent = (int)AnimationSpeed; s.SidebarCompact = SidebarCompact;
        s.TaskbarJumpList = TaskbarJumpList; s.HotkeyEnabled = HotkeyEnabled; s.Hotkey = Hotkeys[Math.Clamp(HotkeyIndex, 0, Hotkeys.Length - 1)];
        s.FloatingIconEnabled = FloatingIconEnabled; s.FloatingHideWhenOpen = FloatingHideWhenOpen;
        s.FloatingMode = Modes[Math.Clamp(FloatingModeIndex, 0, 1)]; s.FloatingSize = (int)FloatingSize; s.FloatingOpacity = (int)FloatingOpacity;
        s.FloatingStyle = StyleIds[Math.Clamp(FloatingStyleIndex, 0, StyleIds.Length - 1)]; s.FloatingAnimation = Animations[Math.Clamp(FloatingAnimationIndex, 0, Animations.Length - 1)]; s.KeepRunningInBackground = KeepRunningInBackground;
        s.StartupSound = Sounds[Math.Clamp(StartupSoundIndex, 0, Sounds.Length - 1)]; s.ArtFit = Fits[Math.Clamp(ArtFitIndex, 0, 1)]; s.WindowRoundness = Math.Clamp(RoundnessIndex, 0, 2); s.MaterialVisibility = (int)MaterialVisibility; s.IntroAnimation = IntroAnimation; s.SoundsEnabled = SoundsEnabled;
        s.StartWithWindows = StartWithWindows; s.MinimizeToTray = MinimizeToTray; s.AutoDetectGames = AutoDetect;
        s.ScanIntervalMinutes = Math.Max(1, (int)ScanInterval); s.DefaultLibraryView = Views[Math.Clamp(DefaultViewIndex, 0, 2)];
        s.AutoMetadataRefresh = AutoMetadata; s.SteamId64 = string.IsNullOrWhiteSpace(SteamId) ? null : SteamId.Trim();
        s.BackupDirectory = string.IsNullOrWhiteSpace(BackupDirectory) ? null : BackupDirectory; s.CustomBackgroundImage = string.IsNullOrWhiteSpace(CustomBackground) ? null : CustomBackground;
        s.ScanDirectories = ScanDirectories.ToList();
        _debounce.Stop(); _debounce.Start();      // sliders fire dozens of changes per second: save + apply once
    }

    private void Flush()
    {
        _h.Settings.Save();
        if (_lastStartWithWindows != StartWithWindows) { _lastStartWithWindows = StartWithWindows; Services.Platform.SetStartWithWindows(StartWithWindows); }
        _applied();
    }

    public void AddScanDirectory(string dir) { if (!ScanDirectories.Contains(dir, StringComparer.OrdinalIgnoreCase)) { ScanDirectories.Add(dir); Commit(); } }
    public void RemoveScanDirectory(string dir) { ScanDirectories.Remove(dir); Commit(); }
    public void SaveSteamKey(string key) { _h.Settings.SetSteamApiKey(key); GameViewModel.HasSteamKey = !string.IsNullOrEmpty(key.Trim()); OnPropertyChanged(nameof(SteamKeyState)); Message = Services.Loc.T(string.IsNullOrEmpty(key.Trim()) ? "Steam API key removed." : "Steam API key saved."); }
}
