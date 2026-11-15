using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GameIndex.Core.Tracking;

/// <summary>Maps a running process image path to a game. Manual games match by exact EXE (or folder if TrackFolder); platform games by install folder.</summary>
public sealed class WatchList
{
    private readonly Dictionary<string, long> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Dir, long Id)> _dirs = new();

    public static WatchList Build(IEnumerable<Game> games)
    {
        var w = new WatchList();
        foreach (var g in games.Where(g => g.IsInstalled))
        {
            try
            {
                if (g.IsManual && !g.TrackFolder && !string.IsNullOrEmpty(g.ExePath)) { w._exact[PathUtil.Norm(g.ExePath)] = g.Id; continue; }
                var dir = !string.IsNullOrEmpty(g.InstallDir) ? g.InstallDir : (string.IsNullOrEmpty(g.ExePath) ? null : Path.GetDirectoryName(g.ExePath));
                if (!string.IsNullOrEmpty(dir)) w._dirs.Add((PathUtil.Norm(dir) + Path.DirectorySeparatorChar, g.Id));
            }
            catch { }
        }
        w._dirs.Sort((a, b) => b.Dir.Length.CompareTo(a.Dir.Length)); // longest (most specific) folder first
        return w;
    }

    public long? Match(string processPath)
    {
        if (_exact.TryGetValue(processPath, out var id)) return id;
        foreach (var (dir, gid) in _dirs)
            if (processPath.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return gid;
        return null;
    }
}

internal static class NativeProc
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    public static string? GetImagePath(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024); var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }
}

/// <summary>
/// Background process monitor. Starts a session when a matching process appears, ends it ~9 s after the last match disappears,
/// writes heartbeats so crashes can be recovered, and keeps running while the UI is minimized or hidden in the tray.
/// </summary>
public sealed class PlaytimeTracker : IDisposable
{
    private sealed class Active { public long SessionId; public int Misses; public long LastSeen; public long Start; public long LastBeat; }

    private readonly Db _db;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<long, Active> _active = new();
    private readonly Dictionary<int, (string? Path, long? GameId)> _pids = new();
    private WatchList _watch = new();
    private volatile bool _dirty = true;
    private long _lastLoad;
    private Task? _loop;

    public event Action<long, bool>? RunningChanged;   // gameId, isRunning (raised on a background thread)
    public int PollSeconds { get; set; } = 3;

    public PlaytimeTracker(Db db) => _db = db;

    public bool IsRunning(long gameId) { lock (_active) return _active.ContainsKey(gameId); }
    public void InvalidateWatchList() => _dirty = true;

    public void Start()
    {
        var recovered = _db.RecoverOpenSessions();
        if (recovered > 0) Log.Warn($"Recovered {recovered} unfinished session(s) from a previous run.");
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(PollSeconds));
        var lastTick = Format.NowUnix();
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                try { Tick(ref lastTick); }
                catch (Exception ex) { Log.Error("Playtime tick failed", ex); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Tick(ref long lastTick)
    {
        var now = Format.NowUnix();
        if (now - lastTick > 60) // PC slept / process was suspended: do not count the gap
        {
            CloseAll(lastTick);
        }
        lastTick = now;

        if (_dirty || now - _lastLoad > 30)
        {
            _watch = WatchList.Build(_db.GetGames());
            _pids.Clear();
            _dirty = false; _lastLoad = now;
        }

        var running = new HashSet<long>();
        var seen = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var pid = p.Id;
                if (pid <= 4) continue;
                seen.Add(pid);
                if (!_pids.TryGetValue(pid, out var e))
                {
                    var path = NativeProc.GetImagePath(pid);
                    e = (path, path is null ? null : _watch.Match(path));
                    _pids[pid] = e;
                }
                if (e.GameId is long gid) running.Add(gid);
            }
            catch { }
            finally { p.Dispose(); }
        }
        foreach (var dead in _pids.Keys.Where(k => !seen.Contains(k)).ToList()) _pids.Remove(dead);

        var started = new List<long>(); var ended = new List<long>();
        lock (_active)
        {
            foreach (var id in running)
            {
                if (!_active.TryGetValue(id, out var a))
                {
                    _active[id] = new Active { SessionId = _db.BeginSession(id, now), Start = now, LastSeen = now, LastBeat = now };
                    started.Add(id);
                }
                else
                {
                    a.Misses = 0; a.LastSeen = now;
                    if (now - a.LastBeat >= 15) { _db.Heartbeat(a.SessionId, now); a.LastBeat = now; }
                }
            }
            foreach (var (id, a) in _active.ToList())
            {
                if (running.Contains(id)) continue;
                if (++a.Misses < 3) continue; // ~9 s grace so launcher stubs / restarts do not split a session
                Finish(a, a.LastSeen);
                _active.Remove(id);
                ended.Add(id);
            }
        }
        foreach (var id in started) RunningChanged?.Invoke(id, true);
        foreach (var id in ended) RunningChanged?.Invoke(id, false);
    }

    private void Finish(Active a, long endTs)
    {
        if (endTs - a.Start < 5) _db.DeleteSession(a.SessionId); // process flicker, not a play session
        else _db.EndSession(a.SessionId, endTs);
    }

    private void CloseAll(long endTs)
    {
        List<long> ids;
        lock (_active)
        {
            ids = _active.Keys.ToList();
            foreach (var a in _active.Values) Finish(a, a.LastSeen);
            _active.Clear();
        }
        foreach (var id in ids) RunningChanged?.Invoke(id, false);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(2000); } catch { }
        lock (_active)
        {
            var now = Format.NowUnix();
            foreach (var a in _active.Values) Finish(a, now);
            _active.Clear();
        }
        _cts.Dispose();
    }
}
