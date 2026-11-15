namespace GameIndex.Core.Storage;

public static class StorageAnalyzer
{
    /// <summary>Actual size of all files below a directory (installed size, not download size). Skips reparse points.</summary>
    public static long GetDirectorySize(string dir, CancellationToken ct = default)
    {
        long total = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opts))
        {
            ct.ThrowIfCancellationRequested();
            try { total += f.Length; } catch { }
        }
        return total;
    }

    /// <summary>Installed bytes grouped by drive letter (multi-drive libraries).</summary>
    public static Dictionary<string, long> ByDrive(IEnumerable<Game> games) =>
        games.Where(g => g.SizeBytes is not null && !string.IsNullOrEmpty(g.InstallDir))
             .GroupBy(g => Path.GetPathRoot(g.InstallDir!) ?? "?", StringComparer.OrdinalIgnoreCase)
             .ToDictionary(x => x.Key, x => x.Sum(g => g.SizeBytes!.Value), StringComparer.OrdinalIgnoreCase);
}
