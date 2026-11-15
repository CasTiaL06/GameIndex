namespace GameIndex.Core;

public static class Format
{
    public static string Duration(long seconds)
    {
        if (seconds <= 0) return "0 minutes";
        long totalMin = seconds / 60, h = totalMin / 60, m = totalMin % 60;
        if (h == 0) return m == 0 ? "Less than a minute" : $"{m} minute{(m == 1 ? "" : "s")}";
        var hs = $"{h} hour{(h == 1 ? "" : "s")}";
        return m == 0 ? hs : $"{hs} {m} minute{(m == 1 ? "" : "s")}";
    }

    public static string Size(long bytes)
    {
        const double KB = 1024, MB = KB * 1024, GB = MB * 1024;
        var c = CultureInfo.CurrentCulture;
        if (bytes >= GB) return string.Format(c, "{0:0.0} GB", bytes / GB);
        if (bytes >= MB) return string.Format(c, "{0:0} MB", bytes / MB);
        if (bytes >= KB) return string.Format(c, "{0:0} KB", bytes / KB);
        return $"{bytes} B";
    }

    /// <summary>Day/month/year with a full month name, localized, e.g. "14 November 2026".</summary>
    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture);

    public static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
    public static DateTime FromUnix(long ts) => DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
    public static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

public static class PathUtil
{
    public static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');

    public static bool IsUnder(string path, string dir)
    {
        var d = Norm(dir) + Path.DirectorySeparatorChar;
        return Norm(path).StartsWith(d, StringComparison.OrdinalIgnoreCase) || Norm(path).Equals(Norm(dir), StringComparison.OrdinalIgnoreCase);
    }
}
