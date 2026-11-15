using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameIndex.Core.Settings;

public sealed class AppSettings
{
    // Appearance
    public string Theme { get; set; } = "Dark";            // Dark | Light | System
    public string AccentColor { get; set; } = "#65D9FF";
    public string Language { get; set; } = "system";        // system | en | tr
    public bool TransparencyEnabled { get; set; } = true;   // false = solid, fully opaque windows
    public int OverlayOpacityPercent { get; set; } = 95;    // Settings / Details / Quick switch windows
    public int TransparencyPercent { get; set; } = 72;     // opacity of glass panels
    public string Backdrop { get; set; } = "Mica";         // Mica | MicaAlt | Acrylic | None
    public int BackgroundDimPercent { get; set; } = 35;
    public string? CustomBackgroundImage { get; set; }
    public bool AnimationsEnabled { get; set; } = true;
    public int AnimationSpeedPercent { get; set; } = 100;
    public bool SidebarCompact { get; set; } = true;
    // Application
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool TaskbarJumpList { get; set; } = true;     // recent/favorite games in the taskbar right-click menu
    public bool AutoDetectGames { get; set; } = true;
    public int ScanIntervalMinutes { get; set; } = 15;
    public string DefaultLibraryView { get; set; } = "Carousel"; // Carousel | Grid | List
    public bool AutoMetadataRefresh { get; set; } = true;
    public List<string> ScanDirectories { get; set; } = new();
    // Data
    public string? BackupDirectory { get; set; }
    // Integrations (secrets are DPAPI-protected, never plain text)
    public string? SteamApiKeyProtected { get; set; }
    public string? SteamId64 { get; set; }
    public List<string> DisabledPlatforms { get; set; } = new();
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;
    private readonly object _gate = new();
    public AppSettings Current { get; private set; } = new();
    public event Action? Changed;

    public SettingsService(string file) { _file = file; Load(); }

    public void Load()
    {
        try
        {
            if (File.Exists(_file))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_file)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("Settings unreadable, using defaults", ex);
            try { File.Move(_file, _file + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
                File.Move(tmp, _file, true);
            }
        }
        catch (Exception ex) { Log.Error("Could not save settings", ex); }
        Changed?.Invoke();
    }

    public string? GetSteamApiKey()
    {
        var p = Current.SteamApiKeyProtected;
        if (string.IsNullOrEmpty(p)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(p), null, DataProtectionScope.CurrentUser)); }
        catch { return null; } // e.g. portable folder moved to another Windows user/PC: key must be re-entered
    }

    public void SetSteamApiKey(string? key)
    {
        Current.SteamApiKeyProtected = string.IsNullOrWhiteSpace(key) ? null :
            Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));
        Save();
    }
}
