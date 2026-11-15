# GameIndex

A PlayStation 5 inspired, Windows 11 Fluent / glassmorphism game launcher.
C# · .NET 10 · WinUI 3 (Windows App SDK) · MVVM · SQLite · portable, self-contained, no installer needed.

> **Status: complete implementation, NOT yet compiled or run.** The code was written in a Linux sandbox that has no
> .NET SDK, no Windows and no network. The first real compile happens in GitHub Actions. Expect to fix a few compile
> errors on the first run (see *First build* below). Everything listed under *Verified / Unverified* is honest about that.

## Features
* PS5-style horizontal carousel. **The selected card slides down (and scales slightly); unselected cards never move.**
  Each carousel cell has a fixed size (`Slot`); only the inner `Card` is moved via a `CompositeTransform`, which does not take part in layout.
  ~320 ms cubic ease-out, interrupted animations never jump. Virtualized (`ItemsRepeater`), so hundreds of games stay smooth.
* Dynamic full-window artwork with a 380 ms cross-fade, dark gradient overlays, `UniformToFill` (never stretched), local artwork cache.
* Mica / Mica Alt / Acrylic system backdrops + translucent glass panels, dark/light theme, custom accent, transparency and dimming sliders.
* Keyboard (arrows, Enter, Esc, Ctrl+F), mouse wheel, horizontal scroll, click/double-click, gamepad (D-pad/stick, A, B, Y), drag & drop of `.exe` files.
* Automatic discovery: Steam, Epic, GOG, Ubisoft Connect, EA App, Xbox/Microsoft Store, Battle.net, Rockstar, Riot + standalone folders you configure.
* Per game: total playtime, installed size, installed DLC count, achievements, last played date (each with its source clearly labeled).
* Manual games (EXE, args, working dir, custom cover/background, category, platform name, favorites) – never removed by scans.
* Library views: Home, All, Installed, Favorites, Recently Played, Most Played, Recently Added, Platforms, Categories; search, filters, 7 sort modes; carousel/grid/list.
* SQLite with migrations, integrity check + automatic recovery from backup, daily auto backup, backup/restore, JSON export/import.
* System tray, start with Windows (HKCU, no admin), single instance per folder.

## Folder layout (source)
```
GameIndex.slnx
GameIndex.Core/            # UI-independent engine (unit-testable)
  Core/                    # AppPaths (portable paths), Log, Format
  Models/  Database/  Settings/
  Integrations/            # Steam, Epic, GOG, Ubisoft, EA, Xbox(+Standalone), Battle.net, Rockstar, Riot
  GameDiscovery/  PlaytimeTracker/  StorageAnalyzer/  GameLauncher/
  AchievementService/  DLCService/  Services/ (ArtworkService, GameInfoService)
GameIndex/                 # WinUI 3 app: Views, ViewModels, Services (tray, gamepad, theme)
GameIndex.Tests/           # xunit tests (parsers, DB, sessions, double-count, recovery, backup…)
.github/workflows/build.yml
```

## Build locally
Requirements: Windows 10 1809+/11, .NET 10 SDK, Visual Studio 2022 17.14+ with the *Windows application development* workload (or just the CLI).
```
dotnet test    GameIndex.Tests/GameIndex.Tests.csproj -c Release
dotnet publish GameIndex/GameIndex.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -o publish/GameIndex
publish\GameIndex\GameIndex.exe
```
The solution file is `.slnx` (default in the .NET 10 SDK). Package versions use floating ranges (`1.8.*`, `8.4.*`…); pin exact versions once the first build succeeds.

## Portable distribution
The CI produces **GameIndex-Portable-x64.zip** (self-contained .NET *and* self-contained Windows App SDK – nothing to install).
Extract anywhere (also USB/external drives) and run `GameIndex.exe`. Data layout:
```
GameIndex/ GameIndex.exe  Data/gameindex.db  Data/Backups/  Assets/{Covers,Backgrounds,Icons}/  Settings/settings.json  Cache/  Logs/
```
* Artwork paths inside the database are stored **relative**, so the folder can move between drives.
* If the folder is read-only, GameIndex falls back to `%LocalAppData%\GameIndex` and tells you in the status bar (no crash).
* No administrator rights are required for normal operation.
* Single-file publishing is intentionally **not** used (not reliable with WinUI 3); the ZIP is a folder.
* The Steam API key is encrypted with Windows DPAPI for the current Windows user; if you move the folder to another PC/user, re-enter it.

## GitHub release
```
git tag v1.0.0 && git push origin v1.0.0
```
The workflow builds, tests, publishes and attaches `GameIndex-Portable-x64.zip` to the GitHub Release for that tag.
Manual run: Actions → *Build GameIndex* → *Run workflow*; the ZIP is under *Artifacts*.
An installer (MSIX or Inno Setup wrapping the same publish folder) is planned for later releases; it is not implemented yet.

## Platform integration matrix (what really works)
| Platform | Discovery | Launch | Installed DLC | Achievements | Playtime / last played |
|---|---|---|---|---|---|
| Steam | manifests + libraryfolders | steam:// | installed DLC depots in manifest | Web API (key + public profile) | Web API (key) or local config if present |
| Epic | launcher manifests | protocol | add-on manifests | – | – |
| GOG | registry | exe | `dependsOn` registry entries | – | – |
| Ubisoft | registry | uplay:// | – | – | – |
| EA App | registry (`EA Games`) – incomplete coverage | exe | – | – | – |
| Xbox / MS Store | `X:\XboxGames` manifests | AppsFolder | – | – | – |
| Battle.net | Blizzard uninstall entries | exe | – | – | – |
| Rockstar | registry | exe | – | – | – |
| Riot | product_settings metadata | RiotClientServices | – | – | – |
| Standalone / manual | your scan folders / EXE | exe | – | – | tracked by GameIndex |

"–" always shows **Not Available** in the UI; GameIndex never invents numbers. Playtime tracked by GameIndex itself works for every game.
Platform logos and game logo *images* are not bundled (trademarks); the platform name and game title are rendered as text, Steam logo/cover/hero art is fetched from Steam's public CDN or the local Steam cache.
Blur radius is not adjustable: Windows exposes no such API for Mica/Acrylic, so strength = chosen material + transparency slider.

## Playtime accuracy
A background poller (3 s) matches running processes to games (exact EXE for manual games, install folder for platform games), writes a heartbeat
every 15 s, ends a session ~9 s after the last process disappears, ignores sleep gaps, discards <5 s flickers and **recovers unfinished sessions** after a crash.
Imported platform playtime is stored separately; total = imported + only GameIndex time tracked *since the import*, so sessions are never counted twice.
Limitation: while GameIndex is fully closed, nothing is tracked.

## First build: what to expect
Because nothing could be compiled here, send the first failing GitHub Actions log and the issues can be fixed quickly. Most likely spots:
Windows App SDK / .NET 10 package version compatibility, XAML compiler strictness, and small API signature differences.

## Keyboard / gamepad shortcuts
| Action | Keyboard | Gamepad |
|---|---|---|
| Previous / next game | ← → , mouse wheel | D-pad / left stick |
| Jump 10 games | PageUp / PageDown | LB / RB |
| First / last game | Home / End | – |
| **Quick switcher** (search any game, even filtered out ones) | **Ctrl+K** (Enter = select, Ctrl+Enter = play) | – |
| Type-ahead | press letters/digits to jump to a title | – |
| Play | Enter | A |
| Favorite | – | Y |
| Back / close | Esc | B |

## Language
Settings → Language: English, Türkçe or follow Windows. Switches live (no restart). Dates use the chosen culture (e.g. "14 Kasım 2026").
Some technical messages coming from the engine (e.g. backup file paths, platform error texts) are still English.

## Transparency
Settings → Appearance: *Enable transparency effects* turns all see-through effects off (solid windows), *Settings / Details window opacity*
controls how opaque those pop-up windows are (default 95 %), and *Panel transparency* controls the glass panels.
