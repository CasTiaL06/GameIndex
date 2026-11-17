using GameIndex.Core;
using GameIndex.Core.Database;
using GameIndex.Core.Integrations;
using GameIndex.Core.Models;
using GameIndex.Core.Tracking;
using Xunit;

namespace GameIndex.Tests;

public sealed class TempDb : IDisposable
{
    public string Dir = Path.Combine(Path.GetTempPath(), "gi-test-" + Guid.NewGuid().ToString("N"));
    public Db Db;
    public TempDb()
    {
        Directory.CreateDirectory(Dir);
        Db = new Db(Path.Combine(Dir, "t.db"), Path.Combine(Dir, "bk"), Dir);
        Db.Initialize();
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(Dir, true); } catch { } }
}

public class FormatTests
{
    [Theory]
    [InlineData(0, "0 minutes")]
    [InlineData(45 * 60, "45 minutes")]
    [InlineData((12 * 60 + 45) * 60, "12 hours 45 minutes")]
    [InlineData(250 * 3600, "250 hours")]
    [InlineData(3600, "1 hour")]
    public void Duration_FormatsLikeSpec(long seconds, string expected) => Assert.Equal(expected, Format.Duration(seconds));

    [Fact] public void Size_UsesGbAndMb()
    {
        Assert.EndsWith("GB", Format.Size((long)(87.6 * 1024 * 1024 * 1024)));
        Assert.Equal("512 MB", Format.Size(512L * 1024 * 1024));
    }
}

public class ParserTests
{
    [Fact] public void Vdf_ParsesNestedAndEscapes()
    {
        var n = VdfParser.Parse("\"libraryfolders\"\n{\n \"0\" { \"path\" \"D:\\\\Games\\\\Steam\" \"apps\" { \"10\" \"1\" } }\n}");
        Assert.Equal("D:\\Games\\Steam", n["libraryfolders"]!["0"]!.GetString("path"));
        Assert.Equal("1", n["libraryfolders"]!["0"]!["apps"]!.GetString("10"));
    }

    [Fact] public void SteamManifest_ParsesInstalledDlcDepots()
    {
        const string acf = "\"AppState\" { \"appid\" \"123\" \"name\" \"Test Game\" \"installdir\" \"TestGame\" \"StateFlags\" \"4\" \"SizeOnDisk\" \"1000\" \"LastPlayed\" \"1700000000\" " +
            "\"InstalledDepots\" { \"124\" { \"manifest\" \"1\" } \"125\" { \"manifest\" \"2\" \"dlcappid\" \"9001\" } \"126\" { \"dlcappid\" \"9002\" } } }";
        var m = SteamIntegration.ParseManifest(acf)!;
        Assert.Equal("Test Game", m.Name);
        Assert.Equal(4, m.StateFlags);
        Assert.Equal(new[] { "9001", "9002" }, m.DlcAppIds);
        Assert.Equal(1700000000, m.LastPlayed);
    }

    [Fact] public void EpicItem_DetectsAddonsAndIncomplete()
    {
        var game = EpicIntegration.ParseItem("{\"AppName\":\"Fortnite\",\"DisplayName\":\"X\",\"InstallLocation\":\"C:\\\\X\",\"MainGameAppName\":\"Fortnite\",\"bIsIncompleteInstall\":false}")!;
        var dlc = EpicIntegration.ParseItem("{\"AppName\":\"AddOn1\",\"DisplayName\":\"Pack\",\"InstallLocation\":\"C:\\\\X\",\"MainGameAppName\":\"Fortnite\"}")!;
        Assert.False(game.IsAddon);
        Assert.True(dlc.IsAddon);
    }

    [Fact] public void Riot_ParsesInstallPath() =>
        Assert.Equal("C:/Riot Games/VALORANT", RiotIntegration.ParseInstallPath("foo: bar\nproduct_install_full_path: \"C:/Riot Games/VALORANT\"\nx: y"));

    [Fact] public void Xbox_PackageFamilyName_MatchesKnownWindowsValue() =>
        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe",
            XboxIntegration.PackageFamilyName("Microsoft.WindowsCalculator", "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"));

    [Fact] public void ExeFinder_PicksLargestAndIgnoresInstallers()
    {
        var d = Path.Combine(Path.GetTempPath(), "gi-exe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(d, "bin"));
        File.WriteAllBytes(Path.Combine(d, "unins000.exe"), new byte[5000]);
        File.WriteAllBytes(Path.Combine(d, "small.exe"), new byte[10]);
        File.WriteAllBytes(Path.Combine(d, "bin", "game.exe"), new byte[100]);
        try { Assert.EndsWith("game.exe", ExeFinder.FindMainExe(d)); } finally { Directory.Delete(d, true); }
    }
}

public class WatchListTests
{
    [Fact] public void MatchesExactExeAndFolders_PrefersMostSpecificFolder()
    {
        var games = new[]
        {
            new Game { Id = 1, IsManual = true, ExePath = @"C:\Games\A\a.exe" },
            new Game { Id = 2, InstallDir = @"D:\Steam\common\B" },
            new Game { Id = 3, InstallDir = @"D:\Steam\common\B\Sub" }
        };
        var w = WatchList.Build(games);
        Assert.Equal(1, w.Match(@"C:\Games\A\a.exe"));
        Assert.Null(w.Match(@"C:\Games\A\other.exe"));
        Assert.Equal(2, w.Match(@"D:\Steam\common\B\bin\b.exe"));
        Assert.Equal(3, w.Match(@"D:\Steam\common\B\Sub\x.exe"));
    }
}

public class DbTests
{
    [Fact] public void Sync_NeverOverwritesUserEdits_AndNeverTouchesManualGames()
    {
        using var t = new TempDb();
        var (id, isNew) = t.Db.UpsertDiscovered("steam", new DiscoveredGame("10", "Original", @"C:\x", @"C:\x\a.exe"));
        Assert.True(isNew);
        t.Db.UpdateGameDetails(new Game { Id = id, Title = "My Title", ExePath = @"C:\x\custom.exe" });

        var (id2, isNew2) = t.Db.UpsertDiscovered("steam", new DiscoveredGame("10", "Renamed By Steam", @"C:\x", @"C:\x\other.exe"));
        Assert.Equal(id, id2); Assert.False(isNew2);
        var g = t.Db.GetGame(id)!;
        Assert.Equal("My Title", g.Title);
        Assert.Equal(@"C:\x\custom.exe", g.ExePath);

        var manual = t.Db.InsertManual(new Game { Title = "Manual", ExePath = @"C:\m\m.exe" });
        t.Db.MarkMissing("steam", new HashSet<string>());
        Assert.False(t.Db.GetGame(id)!.IsInstalled);
        Assert.NotNull(t.Db.GetGame(manual));
    }

    [Fact] public void Sessions_AccumulatePlaytime_AndRecoverAfterCrash()
    {
        using var t = new TempDb();
        var id = t.Db.InsertManual(new Game { Title = "G", ExePath = @"C:\g.exe" });
        var s1 = t.Db.BeginSession(id, 1000); t.Db.EndSession(s1, 1000 + 3600);
        var s2 = t.Db.BeginSession(id, 10000); t.Db.Heartbeat(s2, 10000 + 600);   // app crashed: never ended
        Assert.Equal(1, t.Db.RecoverOpenSessions());
        var g = t.Db.GetGame(id)!;
        Assert.Equal(4200, g.LocalPlaytimeSeconds);
        Assert.NotNull(g.LastPlayedUtc);
        Assert.Contains(t.Db.GetSessions(id), s => s.Recovered);
    }

    [Fact] public void ImportedPlaytime_DoesNotDoubleCountLocalSessions()
    {
        using var t = new TempDb();
        var (id, _) = t.Db.UpsertDiscovered("steam", new DiscoveredGame("5", "S", @"C:\s"));
        var s = t.Db.BeginSession(id, 0); t.Db.EndSession(s, 3600);               // 1 h tracked locally
        t.Db.SetImportedStats(id, 10 * 3600, null);                               // Steam already contains that hour
        Assert.Equal(10 * 3600, t.Db.GetGame(id)!.TotalPlaytimeSeconds);
        var s2 = t.Db.BeginSession(id, 100000); t.Db.EndSession(s2, 100000 + 1800); // 30 min after import
        Assert.Equal(10 * 3600 + 1800, t.Db.GetGame(id)!.TotalPlaytimeSeconds);
    }

    [Fact] public void Dlc_Unavailable_IsNullNotZero()
    {
        using var t = new TempDb();
        var id = t.Db.InsertManual(new Game { Title = "G", ExePath = @"C:\g.exe" });
        t.Db.SetDlcs(id, null);
        Assert.Null(t.Db.GetGame(id)!.DlcInstalled);
        t.Db.SetDlcs(id, new[] { new DlcInfo("1", "A", true), new DlcInfo("2", "B", false) });
        Assert.Equal(1, t.Db.GetGame(id)!.DlcInstalled);       // owned-but-not-installed is not counted
    }

    [Fact] public void Achievements_RoundTrip_AndUnavailableStaysNull()
    {
        using var t = new TempDb();
        var id = t.Db.InsertManual(new Game { Title = "G", ExePath = @"C:\g.exe" });
        t.Db.SetAchievements(id, null);
        Assert.Null(t.Db.GetGame(id)!.AchUnlocked);
        t.Db.SetAchievements(id, new[] { new AchievementRecord("a", "A", null, null, null, true, DateTime.UtcNow), new AchievementRecord("b", "B", null, null, null, false, null) });
        var g = t.Db.GetGame(id)!;
        Assert.Equal((1, 2), (g.AchUnlocked, g.AchTotal));
    }

    [Fact] public void Backup_And_Restore_PreserveData()
    {
        using var t = new TempDb();
        var id = t.Db.InsertManual(new Game { Title = "Keep Me", ExePath = @"C:\g.exe" });
        var file = t.Db.Backup();
        t.Db.DeleteGame(id);
        Assert.Empty(t.Db.GetGames());
        t.Db.Restore(file);
        Assert.Contains(t.Db.GetGames(), g => g.Title == "Keep Me");
    }

    [Fact] public void ExportImport_MergesWithoutDuplicates()
    {
        using var a = new TempDb(); using var b = new TempDb();
        var id = a.Db.InsertManual(new Game { Title = "M", ExePath = @"C:\m.exe", Category = "RPG" });
        var s = a.Db.BeginSession(id, 100); a.Db.EndSession(s, 200);
        var json = Path.Combine(a.Dir, "e.json");
        a.Db.ExportLibrary(json);
        Assert.Equal(1, b.Db.ImportLibrary(json));
        Assert.Equal(0, b.Db.ImportLibrary(json));
        Assert.Single(b.Db.GetGames());
    }
}

public class SettingsTests
{
    [Fact] public void SteamKey_IsNotStoredInPlainText()
    {
        var f = Path.Combine(Path.GetTempPath(), "gi-set-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var s = new GameIndex.Core.Settings.SettingsService(f);
            s.SetSteamApiKey("SECRET-KEY-123");
            Assert.DoesNotContain("SECRET-KEY-123", File.ReadAllText(f));
            Assert.Equal("SECRET-KEY-123", s.GetSteamApiKey());
        }
        finally { File.Delete(f); }
    }
}

public class PathTests
{
    [Fact] public void AppPaths_CreatesPortableStructure()
    {
        var d = Path.Combine(Path.GetTempPath(), "gi-portable-" + Guid.NewGuid().ToString("N"));
        try
        {
            var p = new AppPaths(d);
            Assert.True(p.IsPortable);
            foreach (var sub in new[] { "Data", @"Data\Backups", @"Assets\Covers", @"Assets\Backgrounds", @"Assets\Icons", "Settings", "Cache", "Logs" })
                Assert.True(Directory.Exists(Path.Combine(d, sub)), sub);
        }
        finally { Directory.Delete(d, true); }
    }
}

public class IconCandidateTests
{
    [Fact] public void FindCandidates_PrefersBigExes_SkipsToolsAndAntiCheatFolders()
    {
        var d = Path.Combine(Path.GetTempPath(), "gi-cand-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(d, "Game", "Binaries", "Win64"));
        Directory.CreateDirectory(Path.Combine(d, "EasyAntiCheat"));
        File.WriteAllBytes(Path.Combine(d, "Launcher.exe"), new byte[10]);
        File.WriteAllBytes(Path.Combine(d, "Game", "Binaries", "Win64", "Client-Win64-Shipping.exe"), new byte[5000]);
        File.WriteAllBytes(Path.Combine(d, "EasyAntiCheat", "huge.exe"), new byte[9000]);
        File.WriteAllBytes(Path.Combine(d, "vc_redist.x64.exe"), new byte[9000]);
        try
        {
            var c = ExeFinder.FindCandidates(d);
            Assert.Equal(2, c.Count);
            Assert.EndsWith("Client-Win64-Shipping.exe", c[0]);
        }
        finally { Directory.Delete(d, true); }
    }
}


public class DlcDetectionTests
{
    [Fact] public void Gog_InfoFile_RootGameId_MarksDlc()
    {
        var dlc = GogIntegration.ParseInfoDlc("{\"gameId\":\"222\",\"rootGameId\":\"111\",\"name\":\"Expansion\"}", "111");
        Assert.Single(dlc);
        Assert.Equal("Expansion", dlc[0].Name);
        Assert.Empty(GogIntegration.ParseInfoDlc("{\"gameId\":\"111\",\"rootGameId\":\"111\",\"name\":\"Base\"}", "111"));   // base game is not a DLC
        Assert.Empty(GogIntegration.ParseInfoDlc("{\"gameId\":\"333\",\"rootGameId\":\"999\"}", "111"));                             // belongs to another game
    }

    [Fact] public void FolderScanner_CountsOnlyNonEmptyDlcFolders()
    {
        var d = Path.Combine(Path.GetTempPath(), "gi-dlc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(d, "DLC", "Pack1")); Directory.CreateDirectory(Path.Combine(d, "DLC", "EmptyPack"));
        File.WriteAllText(Path.Combine(d, "DLC", "Pack1", "data.pak"), "x");
        try
        {
            var r = GameIndex.Core.Dlc.FolderDlcScanner.Scan(d);
            Assert.Single(r);
            Assert.Equal("Pack1", r[0].Name);
        }
        finally { Directory.Delete(d, true); }
    }
}
