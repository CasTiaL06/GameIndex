using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GameIndex.Core.Database;

public sealed record ExportSession(long StartTs, long? EndTs);
public sealed record ExportGame(string Title, string PlatformId, string? PlatformName, string ExternalId, string? InstallDir, string? ExePath,
    string? LaunchArgs, string? WorkingDir, string? LaunchUri, bool IsManual, bool IsFavorite, bool TrackFolder, string? Category,
    long DateAddedTs, long? ImportedPlaytimeSeconds, long? ImportedLastPlayedTs, List<ExportSession> Sessions);
public sealed record LibraryExport(int Version, DateTime ExportedUtc, List<ExportGame> Games);

public sealed partial class Db
{
    /// <summary>Consistent copy of the database using VACUUM INTO.</summary>
    public string Backup(string? directory = null)
    {
        var dir = directory ?? _backupDir;
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"gameindex-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        using var c = Open();
        Exec(c, $"VACUUM INTO '{target.Replace("'", "''")}'");
        return target;
    }

    /// <summary>Daily automatic backup, keeping the newest 7.</summary>
    public void AutoBackup(string? directory = null)
    {
        try
        {
            var dir = directory ?? _backupDir;
            Directory.CreateDirectory(dir);
            var files = new DirectoryInfo(dir).GetFiles("gameindex-*.db").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            if (files.Count == 0 || DateTime.UtcNow - files[0].LastWriteTimeUtc > TimeSpan.FromHours(24)) Backup(dir);
            foreach (var old in new DirectoryInfo(dir).GetFiles("gameindex-*.db").OrderByDescending(f => f.LastWriteTimeUtc).Skip(7)) old.Delete();
        }
        catch (Exception ex) { Log.Error("Automatic backup failed", ex); }
    }

    public void Restore(string backupFile)
    {
        if (!IsHealthy(backupFile)) throw new InvalidDataException("The selected file is not a valid GameIndex database.");
        try { Backup(); } catch { }
        SqliteConnection.ClearAllPools();
        File.Copy(backupFile, _file, true);
        Migrate();
    }

    public void ExportLibrary(string jsonPath)
    {
        var games = GetGames().Select(g => new ExportGame(g.Title, g.PlatformId, g.CustomPlatformName, g.ExternalId, g.InstallDir, g.ExePath, g.LaunchArgs,
            g.WorkingDir, g.LaunchUri, g.IsManual, g.IsFavorite, g.TrackFolder, g.Category, Format.ToUnix(g.DateAddedUtc),
            g.ImportedPlaytimeSeconds, g.ImportedLastPlayedUtc is null ? null : Format.ToUnix(g.ImportedLastPlayedUtc.Value),
            GetSessions(g.Id, 100000).Where(s => s.EndUtc is not null).Select(s => new ExportSession(Format.ToUnix(s.StartUtc), Format.ToUnix(s.EndUtc!.Value))).ToList())).ToList();
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(new LibraryExport(1, DateTime.UtcNow, games), new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Merges an export into the library; never deletes or overwrites existing user data. Returns number of games added.</summary>
    public int ImportLibrary(string jsonPath)
    {
        var data = JsonSerializer.Deserialize<LibraryExport>(File.ReadAllText(jsonPath)) ?? throw new InvalidDataException("Empty export file.");
        var added = 0;
        foreach (var e in data.Games)
        {
            var id = FindGameId(e.PlatformId, e.ExternalId);
            if (id is null)
            {
                using var c = Open();
                var cat = EnsureCategory(c, e.Category);
                Exec(c, """
                    INSERT INTO games(title,platform_id,platform_name,external_id,install_dir,exe_path,launch_args,working_dir,launch_uri,is_manual,is_installed,is_favorite,
                        user_edited,track_folder,category_id,date_added_ts,imported_playtime_s,imported_last_played_ts)
                    VALUES(@t,@p,@pn,@e,@d,@x,@a,@w,@u,@m,@i,@f,@m,@tf,@cat,@da,@ip,@il)
                    """, ("@t", e.Title), ("@p", e.PlatformId), ("@pn", e.PlatformName), ("@e", e.ExternalId), ("@d", e.InstallDir), ("@x", e.ExePath),
                    ("@a", e.LaunchArgs), ("@w", e.WorkingDir), ("@u", e.LaunchUri), ("@m", e.IsManual ? 1 : 0),
                    ("@i", (e.ExePath is not null && File.Exists(e.ExePath)) || (e.InstallDir is not null && Directory.Exists(e.InstallDir)) ? 1 : 0),
                    ("@f", e.IsFavorite ? 1 : 0), ("@tf", e.TrackFolder ? 1 : 0), ("@cat", cat), ("@da", e.DateAddedTs), ("@ip", e.ImportedPlaytimeSeconds), ("@il", e.ImportedLastPlayedTs));
                id = (long)Scalar(c, "SELECT last_insert_rowid()")!;
                added++;
            }
            foreach (var s in e.Sessions)
            {
                using var c = Open();
                if (Scalar(c, "SELECT 1 FROM sessions WHERE game_id=@g AND start_ts=@s", ("@g", id), ("@s", s.StartTs)) is null)
                    Exec(c, "INSERT INTO sessions(game_id,start_ts,end_ts,heartbeat_ts) VALUES(@g,@s,@e,@e)", ("@g", id), ("@s", s.StartTs), ("@e", s.EndTs));
            }
        }
        return added;
    }
}
