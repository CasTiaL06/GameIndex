namespace GameIndex.Core;

/// <summary>Tiny file logger (Logs/gameindex-yyyyMMdd.log). Never logs secrets; callers must not pass API keys.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _dir;

    public static void Init(string logsDir)
    {
        _dir = logsDir;
        try
        {
            Directory.CreateDirectory(logsDir);
            foreach (var f in new DirectoryInfo(logsDir).GetFiles("gameindex-*.log").OrderByDescending(f => f.Name).Skip(10))
                f.Delete();
        }
        catch { }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex is null ? msg : $"{msg}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string msg)
    {
        if (_dir is null) return;
        try
        {
            lock (Gate)
                File.AppendAllText(Path.Combine(_dir, $"gameindex-{DateTime.Now:yyyyMMdd}.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
