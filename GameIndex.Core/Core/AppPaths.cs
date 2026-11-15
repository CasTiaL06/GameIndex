namespace GameIndex.Core;

/// <summary>
/// Resolves every directory GameIndex uses. Portable by default: everything lives next to GameIndex.exe.
/// Only if that folder is read-only does it fall back to %LocalAppData%\GameIndex (and says so in <see cref="Warning"/>).
/// </summary>
public sealed class AppPaths
{
    public string Root { get; private set; } = "";
    public bool IsPortable { get; private set; }
    public string? Warning { get; private set; }

    public string DataDir => Path.Combine(Root, "Data");
    public string BackupsDir => Path.Combine(DataDir, "Backups");
    public string CoversDir => Path.Combine(Root, "Assets", "Covers");
    public string BackgroundsDir => Path.Combine(Root, "Assets", "Backgrounds");
    public string IconsDir => Path.Combine(Root, "Assets", "Icons");
    public string SettingsDir => Path.Combine(Root, "Settings");
    public string CacheDir => Path.Combine(Root, "Cache");
    public string LogsDir => Path.Combine(Root, "Logs");
    public string DatabaseFile => Path.Combine(DataDir, "gameindex.db");
    public string SettingsFile => Path.Combine(SettingsDir, "settings.json");

    public AppPaths(string? rootOverride = null)
    {
        var preferred = (rootOverride ?? AppContext.BaseDirectory).TrimEnd('\\', '/');
        Root = preferred;
        if (TryPrepare()) { IsPortable = true; return; }

        Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameIndex");
        IsPortable = false;
        Warning = $"The folder '{preferred}' is not writable, so GameIndex stores its data in '{Root}' instead (not portable).";
        if (!TryPrepare()) Warning += " That location is not writable either; changes cannot be saved.";
    }

    private bool TryPrepare()
    {
        try
        {
            foreach (var d in new[] { DataDir, BackupsDir, CoversDir, BackgroundsDir, IconsDir, SettingsDir, CacheDir, LogsDir })
                Directory.CreateDirectory(d);
            var probe = Path.Combine(DataDir, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}
