using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GameIndex.Core.Database;

/// <summary>Lightweight SQLite data layer (Microsoft.Data.Sqlite). All timestamps are unix seconds (UTC).</summary>
public sealed partial class Db
{
    private readonly string _file, _backupDir, _root, _cs;

    public Db(string file, string backupDir, string root)
    {
        _file = file; _backupDir = backupDir; _root = root;
        _cs = new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        using var p = c.CreateCommand();
        p.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        p.ExecuteNonQuery();
        return c;
    }

    // ---------- helpers ----------
    private static SqliteCommand Cmd(SqliteConnection c, string sql, params (string, object?)[] p)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }
    private static int Exec(SqliteConnection c, string sql, params (string, object?)[] p) { using var cmd = Cmd(c, sql, p); return cmd.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection c, string sql, params (string, object?)[] p) { using var cmd = Cmd(c, sql, p); var o = cmd.ExecuteScalar(); return o is DBNull ? null : o; }
    private static object? B(bool? v) => v is null ? null : (v.Value ? 1 : 0);
    private static DateTime? Dt(long? ts) => ts is null ? null : Format.FromUnix(ts.Value);
    private static long? Ts(DateTime? d) => d is null ? null : Format.ToUnix(d.Value);

    /// <summary>Artwork inside the portable root is stored relative so the folder can move between drives.</summary>
    private string? ToStored(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return PathUtil.IsUnder(path, _root) ? Path.GetRelativePath(_root, path) : path; } catch { return path; }
    }
    private string? FromStored(string? path) => string.IsNullOrEmpty(path) ? null : (Path.IsPathRooted(path) ? path : Path.Combine(_root, path));

    // ---------- initialization / migrations / recovery ----------
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE platforms(id TEXT PRIMARY KEY, name TEXT NOT NULL);
        CREATE TABLE integrations(platform_id TEXT PRIMARY KEY REFERENCES platforms(id) ON DELETE CASCADE,
            enabled INTEGER NOT NULL DEFAULT 1, status TEXT, message TEXT, last_scan_ts INTEGER, game_count INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE categories(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE COLLATE NOCASE);
        CREATE TABLE games(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            title TEXT NOT NULL,
            platform_id TEXT NOT NULL REFERENCES platforms(id),
            platform_name TEXT,
            external_id TEXT NOT NULL,
            install_dir TEXT, exe_path TEXT, launch_args TEXT, working_dir TEXT, launch_uri TEXT,
            is_manual INTEGER NOT NULL DEFAULT 0, is_installed INTEGER NOT NULL DEFAULT 1, is_favorite INTEGER NOT NULL DEFAULT 0,
            user_edited INTEGER NOT NULL DEFAULT 0, track_folder INTEGER NOT NULL DEFAULT 0,
            category_id INTEGER REFERENCES categories(id) ON DELETE SET NULL,
            date_added_ts INTEGER NOT NULL,
            size_bytes INTEGER, size_updated_ts INTEGER,
            imported_playtime_s INTEGER, local_at_import_s INTEGER NOT NULL DEFAULT 0, imported_last_played_ts INTEGER,
            ach_unlocked INTEGER, ach_total INTEGER, ach_updated_ts INTEGER,
            dlc_installed INTEGER, dlc_updated_ts INTEGER, stats_updated_ts INTEGER,
            UNIQUE(platform_id, external_id));
        CREATE TABLE artwork(game_id INTEGER NOT NULL REFERENCES games(id) ON DELETE CASCADE, kind TEXT NOT NULL,
            path TEXT NOT NULL, is_custom INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(game_id, kind));
        CREATE TABLE sessions(id INTEGER PRIMARY KEY AUTOINCREMENT, game_id INTEGER NOT NULL REFERENCES games(id) ON DELETE CASCADE,
            start_ts INTEGER NOT NULL, end_ts INTEGER, heartbeat_ts INTEGER NOT NULL, recovered INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX ix_sessions_game ON sessions(game_id);
        CREATE TABLE dlcs(id INTEGER PRIMARY KEY AUTOINCREMENT, game_id INTEGER NOT NULL REFERENCES games(id) ON DELETE CASCADE,
            external_id TEXT NOT NULL, name TEXT NOT NULL, installed INTEGER NOT NULL, UNIQUE(game_id, external_id));
        CREATE TABLE achievements(id INTEGER PRIMARY KEY AUTOINCREMENT, game_id INTEGER NOT NULL REFERENCES games(id) ON DELETE CASCADE,
            api_name TEXT NOT NULL, name TEXT NOT NULL, description TEXT, icon_url TEXT, icon_gray_url TEXT,
            unlocked INTEGER NOT NULL, unlock_ts INTEGER, UNIQUE(game_id, api_name));
        CREATE TABLE user_settings(key TEXT PRIMARY KEY, value TEXT);
        INSERT INTO platforms(id,name) VALUES ('steam','Steam'),('epic','Epic Games'),('gog','GOG'),('ubisoft','Ubisoft Connect'),
            ('ea','EA App'),('xbox','Xbox'),('battlenet','Battle.net'),('rockstar','Rockstar Games'),('riot','Riot Client'),
            ('standalone','Standalone'),('manual','Manual');
        INSERT INTO integrations(platform_id) SELECT id FROM platforms;
        """,
        "ALTER TABLE games ADD COLUMN dlc_source TEXT;"
    ];

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        Directory.CreateDirectory(_backupDir);
        if (File.Exists(_file) && !IsHealthy(_file))
        {
            Log.Error("Database failed integrity check; attempting recovery");
            SqliteConnection.ClearAllPools();
            var bad = _file + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Move(_file, bad, true);
            var newest = new DirectoryInfo(_backupDir).GetFiles("*.db").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault(f => IsHealthy(f.FullName));
            if (newest is not null) { File.Copy(newest.FullName, _file, true); Log.Warn($"Restored database from backup {newest.Name}"); }
        }
        Migrate();
    }

    private static bool IsHealthy(string file)
    {
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            c.Open();
            return string.Equals(Scalar(c, "PRAGMA quick_check") as string, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private void Migrate()
    {
        using var c = Open();
        var v = Convert.ToInt32(Scalar(c, "PRAGMA user_version") ?? 0);
        for (var i = v; i < Migrations.Length; i++)
        {
            using var tx = c.BeginTransaction();
            Exec(c, Migrations[i]);
            Exec(c, $"PRAGMA user_version = {i + 1}");
            tx.Commit();
        }
    }

    // ---------- games ----------
    private const string GameSelect = """
        SELECT g.*, c.name AS category,
          (SELECT path FROM artwork a WHERE a.game_id=g.id AND a.kind='cover') AS cover_path,
          (SELECT path FROM artwork a WHERE a.game_id=g.id AND a.kind='background') AS bg_path,
          (SELECT path FROM artwork a WHERE a.game_id=g.id AND a.kind='logo') AS logo_path,
          (SELECT path FROM artwork a WHERE a.game_id=g.id AND a.kind='icon') AS icon_path,
          COALESCE(s.total,0) AS local_total, s.last_ts AS local_last
        FROM games g
        LEFT JOIN categories c ON c.id=g.category_id
        LEFT JOIN (SELECT game_id, SUM(MAX(0, COALESCE(end_ts,heartbeat_ts)-start_ts)) AS total, MAX(COALESCE(end_ts,heartbeat_ts)) AS last_ts
                   FROM sessions GROUP BY game_id) s ON s.game_id=g.id
        """;

    private Game ReadGame(SqliteDataReader r)
    {
        string? S(string n) { var i = r.GetOrdinal(n); return r.IsDBNull(i) ? null : r.GetString(i); }
        long? L(string n) { var i = r.GetOrdinal(n); return r.IsDBNull(i) ? null : r.GetInt64(i); }
        bool Bo(string n) => (L(n) ?? 0) != 0;
        return new Game
        {
            Id = L("id")!.Value, Title = S("title") ?? "", PlatformId = S("platform_id") ?? PlatformIds.Manual,
            CustomPlatformName = S("platform_name"), ExternalId = S("external_id") ?? "",
            InstallDir = S("install_dir"), ExePath = S("exe_path"), LaunchArgs = S("launch_args"), WorkingDir = S("working_dir"), LaunchUri = S("launch_uri"),
            IsManual = Bo("is_manual"), IsInstalled = Bo("is_installed"), IsFavorite = Bo("is_favorite"), UserEdited = Bo("user_edited"), TrackFolder = Bo("track_folder"),
            Category = S("category"), CoverPath = FromStored(S("cover_path")), BackgroundPath = FromStored(S("bg_path")),
            LogoPath = FromStored(S("logo_path")), IconPath = FromStored(S("icon_path")),
            DateAddedUtc = Format.FromUnix(L("date_added_ts") ?? 0),
            SizeBytes = L("size_bytes"), SizeUpdatedUtc = Dt(L("size_updated_ts")),
            ImportedPlaytimeSeconds = L("imported_playtime_s"), LocalAtImportSeconds = L("local_at_import_s") ?? 0,
            ImportedLastPlayedUtc = Dt(L("imported_last_played_ts")),
            LocalPlaytimeSeconds = L("local_total") ?? 0, LocalLastPlayedUtc = Dt(L("local_last")),
            AchUnlocked = (int?)L("ach_unlocked"), AchTotal = (int?)L("ach_total"), AchUpdatedUtc = Dt(L("ach_updated_ts")),
            DlcInstalled = (int?)L("dlc_installed"), DlcUpdatedUtc = Dt(L("dlc_updated_ts")), DlcSource = S("dlc_source"), StatsUpdatedUtc = Dt(L("stats_updated_ts"))
        };
    }

    public List<Game> GetGames()
    {
        using var c = Open();
        using var cmd = Cmd(c, GameSelect);
        using var r = cmd.ExecuteReader();
        var list = new List<Game>();
        while (r.Read()) list.Add(ReadGame(r));
        return list;
    }

    public Game? GetGame(long id)
    {
        using var c = Open();
        using var cmd = Cmd(c, GameSelect + " WHERE g.id=@id", ("@id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadGame(r) : null;
    }

    public long? FindGameId(string platformId, string externalId)
    {
        using var c = Open();
        return Scalar(c, "SELECT id FROM games WHERE platform_id=@p AND external_id=@e", ("@p", platformId), ("@e", externalId)) as long?;
    }

    /// <summary>Insert or update a platform-discovered game. Respects user edits and never touches manual games.</summary>
    public (long Id, bool IsNew) UpsertDiscovered(string platformId, DiscoveredGame d)
    {
        using var c = Open();
        var existing = Scalar(c, "SELECT id FROM games WHERE platform_id=@p AND external_id=@e", ("@p", platformId), ("@e", d.ExternalId)) as long?;
        if (existing is long id)
        {
            Exec(c, """
                UPDATE games SET install_dir=@dir, launch_uri=COALESCE(@uri, launch_uri), is_installed=1,
                  exe_path = CASE WHEN user_edited=1 THEN exe_path ELSE COALESCE(@exe, exe_path) END,
                  launch_args = CASE WHEN user_edited=1 THEN launch_args ELSE COALESCE(@args, launch_args) END,
                  working_dir = CASE WHEN user_edited=1 THEN working_dir ELSE COALESCE(@wd, working_dir) END,
                  title = CASE WHEN user_edited=1 THEN title ELSE @title END
                WHERE id=@id AND is_manual=0
                """, ("@dir", d.InstallDir), ("@uri", d.LaunchUri), ("@exe", d.ExePath), ("@args", d.LaunchArgs), ("@wd", d.WorkingDir), ("@title", d.Title), ("@id", id));
            return (id, false);
        }
        Exec(c, """
            INSERT INTO games(title,platform_id,external_id,install_dir,exe_path,launch_args,working_dir,launch_uri,is_manual,is_installed,date_added_ts)
            VALUES(@title,@p,@e,@dir,@exe,@args,@wd,@uri,0,1,@now)
            """, ("@title", d.Title), ("@p", platformId), ("@e", d.ExternalId), ("@dir", d.InstallDir), ("@exe", d.ExePath),
            ("@args", d.LaunchArgs), ("@wd", d.WorkingDir), ("@uri", d.LaunchUri), ("@now", Format.NowUnix()));
        return ((long)Scalar(c, "SELECT last_insert_rowid()")!, true);
    }

    /// <summary>Marks non-manual games of a platform as uninstalled if the platform no longer reports them.</summary>
    public int MarkMissing(string platformId, ISet<string> presentExternalIds)
    {
        using var c = Open();
        var ids = new List<long>();
        using (var cmd = Cmd(c, "SELECT id, external_id FROM games WHERE platform_id=@p AND is_manual=0 AND is_installed=1", ("@p", platformId)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) if (!presentExternalIds.Contains(r.GetString(1))) ids.Add(r.GetInt64(0));
        foreach (var id in ids) Exec(c, "UPDATE games SET is_installed=0 WHERE id=@id", ("@id", id));
        return ids.Count;
    }

    public void SetInstalled(long id, bool installed)
    {
        using var c = Open();
        Exec(c, "UPDATE games SET is_installed=@i WHERE id=@id AND is_installed<>@i", ("@i", installed ? 1 : 0), ("@id", id));
    }

    public long InsertManual(Game g)
    {
        using var c = Open();
        var cat = EnsureCategory(c, g.Category);
        Exec(c, """
            INSERT INTO games(title,platform_id,platform_name,external_id,install_dir,exe_path,launch_args,working_dir,launch_uri,
                is_manual,is_installed,is_favorite,user_edited,track_folder,category_id,date_added_ts)
            VALUES(@title,'manual',@pn,@ext,@dir,@exe,@args,@wd,NULL,1,@inst,@fav,1,@tf,@cat,@now)
            """, ("@title", g.Title), ("@pn", g.CustomPlatformName), ("@ext", Guid.NewGuid().ToString("N")), ("@dir", g.InstallDir),
            ("@exe", g.ExePath), ("@args", g.LaunchArgs), ("@wd", g.WorkingDir), ("@inst", g.ExePath is not null && File.Exists(g.ExePath) ? 1 : 0),
            ("@fav", g.IsFavorite ? 1 : 0), ("@tf", g.TrackFolder ? 1 : 0), ("@cat", cat), ("@now", Format.NowUnix()));
        return (long)Scalar(c, "SELECT last_insert_rowid()")!;
    }

    /// <summary>Saves user edits. Marks the game as user-edited so scans stop overwriting these fields.</summary>
    public void UpdateGameDetails(Game g)
    {
        using var c = Open();
        var cat = EnsureCategory(c, g.Category);
        Exec(c, """
            UPDATE games SET title=@title, platform_name=@pn, exe_path=@exe, launch_args=@args, working_dir=@wd,
                install_dir=COALESCE(@dir, install_dir), track_folder=@tf, category_id=@cat, user_edited=1 WHERE id=@id
            """, ("@title", g.Title), ("@pn", g.CustomPlatformName), ("@exe", g.ExePath), ("@args", g.LaunchArgs), ("@wd", g.WorkingDir),
            ("@dir", g.InstallDir), ("@tf", g.TrackFolder ? 1 : 0), ("@cat", cat), ("@id", g.Id));
    }

    private static long? EnsureCategory(SqliteConnection c, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();
        Exec(c, "INSERT OR IGNORE INTO categories(name) VALUES(@n)", ("@n", name));
        return Scalar(c, "SELECT id FROM categories WHERE name=@n", ("@n", name)) as long?;
    }

    public List<string> GetCategories()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT name FROM categories ORDER BY name");
        using var r = cmd.ExecuteReader();
        var l = new List<string>();
        while (r.Read()) l.Add(r.GetString(0));
        return l;
    }

    public void SetFavorite(long id, bool fav) { using var c = Open(); Exec(c, "UPDATE games SET is_favorite=@f WHERE id=@id", ("@f", fav ? 1 : 0), ("@id", id)); }
    public void DeleteGame(long id) { using var c = Open(); Exec(c, "DELETE FROM games WHERE id=@id", ("@id", id)); }

    // ---------- artwork ----------
    public bool IsArtworkCustom(long id, string kind)
    {
        using var c = Open();
        return (Scalar(c, "SELECT is_custom FROM artwork WHERE game_id=@g AND kind=@k", ("@g", id), ("@k", kind)) as long? ?? 0) != 0;
    }

    /// <summary>Automatic artwork never replaces custom artwork (returns false in that case).</summary>
    public bool SetArtwork(long id, string kind, string path, bool isCustom)
    {
        using var c = Open();
        if (!isCustom && IsArtworkCustom(id, kind)) return false;
        Exec(c, "INSERT INTO artwork(game_id,kind,path,is_custom) VALUES(@g,@k,@p,@c) ON CONFLICT(game_id,kind) DO UPDATE SET path=excluded.path, is_custom=excluded.is_custom",
            ("@g", id), ("@k", kind), ("@p", ToStored(path)), ("@c", isCustom ? 1 : 0));
        return true;
    }

    public void ClearArtwork(long id, string kind) { using var c = Open(); Exec(c, "DELETE FROM artwork WHERE game_id=@g AND kind=@k", ("@g", id), ("@k", kind)); }

    public List<string> ClearAutomaticArtwork()
    {
        using var c = Open();
        var paths = new List<string>();
        using (var cmd = Cmd(c, "SELECT path FROM artwork WHERE is_custom=0")) using (var r = cmd.ExecuteReader()) while (r.Read()) paths.Add(FromStored(r.GetString(0))!);
        Exec(c, "DELETE FROM artwork WHERE is_custom=0");
        return paths;
    }

    // ---------- statistics ----------
    public void SetSize(long id, long bytes) { using var c = Open(); Exec(c, "UPDATE games SET size_bytes=@s, size_updated_ts=@t WHERE id=@id", ("@s", bytes), ("@t", Format.NowUnix()), ("@id", id)); }

    public void SetImportedStats(long id, long? playtimeSeconds, long? lastPlayedTs)
    {
        using var c = Open();
        Exec(c, """
            UPDATE games SET
              local_at_import_s = CASE WHEN @p IS NULL THEN local_at_import_s ELSE
                 (SELECT COALESCE(SUM(MAX(0, COALESCE(end_ts,heartbeat_ts)-start_ts)),0) FROM sessions WHERE game_id=@id) END,
              imported_playtime_s = COALESCE(@p, imported_playtime_s),
              imported_last_played_ts = COALESCE(@lp, imported_last_played_ts),
              stats_updated_ts=@now
            WHERE id=@id
            """, ("@p", playtimeSeconds), ("@lp", lastPlayedTs), ("@now", Format.NowUnix()), ("@id", id));
    }

    public void SetAchievements(long id, IReadOnlyList<AchievementRecord>? items)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "DELETE FROM achievements WHERE game_id=@id", ("@id", id));
        if (items is null)
            Exec(c, "UPDATE games SET ach_unlocked=NULL, ach_total=NULL, ach_updated_ts=@t WHERE id=@id", ("@t", Format.NowUnix()), ("@id", id));
        else
        {
            foreach (var a in items)
                Exec(c, "INSERT OR REPLACE INTO achievements(game_id,api_name,name,description,icon_url,icon_gray_url,unlocked,unlock_ts) VALUES(@g,@a,@n,@d,@i,@ig,@u,@ut)",
                    ("@g", id), ("@a", a.ApiName), ("@n", a.Name), ("@d", a.Description), ("@i", a.IconUrl), ("@ig", a.IconGrayUrl), ("@u", a.Unlocked ? 1 : 0), ("@ut", Ts(a.UnlockedUtc)));
            Exec(c, "UPDATE games SET ach_unlocked=@u, ach_total=@t, ach_updated_ts=@now WHERE id=@id",
                ("@u", items.Count(i => i.Unlocked)), ("@t", items.Count), ("@now", Format.NowUnix()), ("@id", id));
        }
        tx.Commit();
    }

    public List<AchievementRecord> GetAchievements(long id)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT api_name,name,description,icon_url,icon_gray_url,unlocked,unlock_ts FROM achievements WHERE game_id=@id ORDER BY unlocked DESC, unlock_ts DESC, name", ("@id", id));
        using var r = cmd.ExecuteReader();
        var l = new List<AchievementRecord>();
        while (r.Read())
            l.Add(new AchievementRecord(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5) != 0, r.IsDBNull(6) ? null : Format.FromUnix(r.GetInt64(6))));
        return l;
    }

    /// <summary>items == null means "platform cannot tell" (stored as unavailable, never as 0).</summary>
    public void SetDlcs(long id, IReadOnlyList<DlcInfo>? items, string? source = null)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "DELETE FROM dlcs WHERE game_id=@id", ("@id", id));
        if (items is not null)
            foreach (var d in items)
                Exec(c, "INSERT OR REPLACE INTO dlcs(game_id,external_id,name,installed) VALUES(@g,@e,@n,@i)", ("@g", id), ("@e", d.ExternalId), ("@n", d.Name), ("@i", d.Installed ? 1 : 0));
        Exec(c, "UPDATE games SET dlc_installed=@n, dlc_updated_ts=@t, dlc_source=@s WHERE id=@id", ("@n", items?.Count(d => d.Installed)), ("@t", Format.NowUnix()), ("@s", source), ("@id", id));
        tx.Commit();
    }

    public List<DlcInfo> GetDlcs(long id)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT external_id,name,installed FROM dlcs WHERE game_id=@id ORDER BY installed DESC, name", ("@id", id));
        using var r = cmd.ExecuteReader();
        var l = new List<DlcInfo>();
        while (r.Read()) l.Add(new DlcInfo(r.GetString(0), r.GetString(1), r.GetInt64(2) != 0));
        return l;
    }

    // ---------- sessions ----------
    public long BeginSession(long gameId, long startTs)
    {
        using var c = Open();
        Exec(c, "INSERT INTO sessions(game_id,start_ts,end_ts,heartbeat_ts) VALUES(@g,@s,NULL,@s)", ("@g", gameId), ("@s", startTs));
        return (long)Scalar(c, "SELECT last_insert_rowid()")!;
    }
    public void Heartbeat(long sessionId, long ts) { using var c = Open(); Exec(c, "UPDATE sessions SET heartbeat_ts=@t WHERE id=@id AND end_ts IS NULL", ("@t", ts), ("@id", sessionId)); }
    public void EndSession(long sessionId, long endTs) { using var c = Open(); Exec(c, "UPDATE sessions SET end_ts=@t, heartbeat_ts=@t WHERE id=@id", ("@t", endTs), ("@id", sessionId)); }
    public void DeleteSession(long sessionId) { using var c = Open(); Exec(c, "DELETE FROM sessions WHERE id=@id", ("@id", sessionId)); }

    /// <summary>After a crash or forced shutdown: close open sessions at their last heartbeat.</summary>
    public int RecoverOpenSessions()
    {
        using var c = Open();
        return Exec(c, "UPDATE sessions SET end_ts=heartbeat_ts, recovered=1 WHERE end_ts IS NULL");
    }

    public List<SessionRecord> GetSessions(long gameId, int limit = 200)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT id,game_id,start_ts,end_ts,heartbeat_ts,recovered FROM sessions WHERE game_id=@g ORDER BY start_ts DESC LIMIT @l", ("@g", gameId), ("@l", limit));
        using var r = cmd.ExecuteReader();
        var l = new List<SessionRecord>();
        while (r.Read())
            l.Add(new SessionRecord(r.GetInt64(0), r.GetInt64(1), Format.FromUnix(r.GetInt64(2)), r.IsDBNull(3) ? null : Format.FromUnix(r.GetInt64(3)), Format.FromUnix(r.GetInt64(4)), r.GetInt64(5) != 0));
        return l;
    }

    // ---------- integrations status & key/value settings ----------
    public void SetIntegrationStatus(string platformId, string status, string? message, int gameCount)
    {
        using var c = Open();
        Exec(c, "UPDATE integrations SET status=@s, message=@m, last_scan_ts=@t, game_count=@n WHERE platform_id=@p",
            ("@s", status), ("@m", message), ("@t", Format.NowUnix()), ("@n", gameCount), ("@p", platformId));
    }

    public List<IntegrationStatus> GetIntegrationStatuses()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT platform_id,enabled,status,message,last_scan_ts,game_count FROM integrations");
        using var r = cmd.ExecuteReader();
        var l = new List<IntegrationStatus>();
        while (r.Read())
            l.Add(new IntegrationStatus(r.GetString(0), r.GetInt64(1) != 0, r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : Format.FromUnix(r.GetInt64(4)), (int)r.GetInt64(5)));
        return l;
    }

    public string? GetSetting(string key) { using var c = Open(); return Scalar(c, "SELECT value FROM user_settings WHERE key=@k", ("@k", key)) as string; }
    public void SetSetting(string key, string? value) { using var c = Open(); Exec(c, "INSERT INTO user_settings(key,value) VALUES(@k,@v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("@k", key), ("@v", value)); }
}
