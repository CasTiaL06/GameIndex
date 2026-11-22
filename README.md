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

## Taskbar integration
* GameIndex sets its own AppUserModelID (`GameIndex.Launcher`), so the taskbar button, a pinned icon and the jump list always belong together.
  Windows does not let apps pin themselves: right-click the taskbar icon → **Pin to taskbar**.
* **Jump list** (right-click the taskbar icon): *Recent games*, *Favorite games*, and tasks *Open GameIndex* / *Scan for games*.
  Clicking a game launches it through the already running GameIndex (single-instance named pipe); the window is not forced to the front.
  Toggle in Settings → Application. If the jump list cannot be written it is skipped silently (see `Logs/`).
* With *Minimize to system tray* enabled the taskbar button disappears while hidden and the tray icon takes over.

## Icons / artwork pipeline
For every game without cover art, in this order: Steam CDN/local cache → logo/`.ico` files shipped in the game folder (Xbox packages, GOG) →
the embedded icon of up to 8 executables found in the game folder (a candidate without an icon resource is skipped, so the generic Windows
icon is never used; Epic/Riot launcher stubs are therefore bypassed) → a per-game coloured card with the game's initials.
Failed lookups are not repeated for 7 days; *Refresh Information* on a game retries immediately. Covers whose proportions differ from a
poster are fitted completely instead of being cropped, and games without a wide background image show their cover/icon fitted, never zoomed.

## Responsiveness
All scanning, artwork, size, DLC, achievement and database work runs on thread-pool threads; settings are written to disk once after you stop
moving a slider; UI text translation visits each element exactly once.

## Tray icon, hotkey, desktop icon, sounds
* **Tray icon** is always present. Left click = show/hide, right click = menu (Open / Scan / Show-Hide desktop icon / Exit).
  Closing the window with ✕ hides it to the tray when *Minimize to system tray* is on (playtime tracking keeps running).
* **Global hotkey** (default **Alt+G**, choices in Settings): shows GameIndex from anywhere, even over a game; pressing it again while GameIndex
  is in front hides it. If the combination is taken by another program, the status bar says so.
* **Floating desktop icon**: a small round, always-on-top, animated icon (rotating ring, breathing glyph). Drag it anywhere – the position is
  remembered; click it and the window *grows out of the icon* (≈0.4 s); hiding the window shrinks it back into the icon. Right-click for a menu.
  It is hidden while the window is open. Toggle / reset in Settings → Desktop & shortcuts.
* **Sounds**: `startup.wav`, `open.wav`, `close.wav` in `Assets/Sounds`, synthesized by `tools/generate_sounds.py` (no samples, no copyright
  issues). They are an original airy-chime design, *not* a copy of any console's sound. Re-run the script to tweak them. Toggle in Settings.
* Artwork without a wide background (icons / covers) is shown in a glass tile at most at its own resolution, and the scene is tinted with the
  artwork's dominant colour.

## Look & feel (v-next)
* **Rounded window**: Windows 11 → DWM round corners (smooth). Windows 10 → rounded clip region (edges are not anti-aliased). Maximized = square.
  Sidebar, cards, tiles, toolbar controls and pills are all rounded; the sidebar floats as its own glass panel.
* **Sidebar**: compact by default (icons only). Every entry shows its name in a tooltip; section headers (PLATFORMS / CATEGORIES) become thin dividers
  instead of clipped text; ☰ expands it to full labels. Add / Scan / Settings buttons also have tooltips.
* **Game info** slides/fades in when you change game; tiles have icons, achievements show a progress bar; cards highlight on hover without moving.

## Floating desktop icon – troubleshooting
The icon is a 152 DIP window cut to a circle (Windows will not create smaller top-level windows). It is shown at the bottom-right of the primary
screen by default and stays visible while GameIndex is open (optional: Settings → *Hide the desktop icon while GameIndex is open*).
Drag to move, click to show/hide GameIndex, right-click for the menu, tray menu → *Show desktop icon*. If it does not appear, open
`Logs/gameindex-<date>.log` and look for `Floating icon created` or `Floating icon could not be shown`.

## Desktop icon v2 (native, truly transparent)
The floating icon is now a native layered window (per-pixel alpha, GDI+), not a WinUI window, so there is **no rectangle behind it** and transparent
pixels let clicks through. It never takes focus. Settings → Desktop & shortcuts:
* **Visibility**: *Desktop only* (sits above the wallpaper but below every app – e.g. invisible while you watch a stream) or *Always on screen* (topmost).
* **Size** (48–160), **opacity**, **style** (Neon, Pulse, Planet, Crystal, Minimal, Gamepad – generated by `tools/generate_icons.py`),
  reset position, hide-while-open.
* **Closing the window** only closes the window: tray icon, desktop icon and playtime tracking keep running. Exit completely via tray / icon menu →
  *Exit GameIndex* or Task Manager (turn this off in Settings to make ✕ exit the app).

## Other settings added
* **Startup sound** choice: Aurora, Crystal, Deep, Pulse, Retro, Zen, Off (+ preview button). All synthesized by `tools/generate_sounds.py`.
* **Window corner roundness** (0 = square … 48). ≤ 9 uses Windows 11's smooth DWM corners; larger values use a clip region (slightly less smooth edge).
* **Artwork scaling**: *Fit* (default; whole image, aspect ratio kept, tinted scene around it) or *Fill* (crop).
* **Default library view** now applies immediately.
* Icons of executables are extracted at the largest available resolution (alpha kept) instead of an upscaled shell thumbnail.

## Installed DLC – how it is detected now
Steam: DLC depots in the app manifest **plus** DLC that are installed as their own Steam app (store DLC list ∩ local `appmanifest_*.acf`).
GOG: registry `dependsOn` **plus** `goggame-*.info` files in the game folder. Epic: add-on manifests.
Other platforms / when the platform reports none: an **estimate** from `DLC`, `Addons`, `Expansions` folders inside the game directory
(shown as "Estimated from the game's DLC folders"). Anything that is merged into the base game's own files cannot be told apart.

## Round 6 changes
* **Open / close animation** no longer resizes the window (that caused the lag and the stray small frame). The window is placed at full size while hidden,
  then fades in (layered-window alpha) while the content eases in from slightly smaller/lower; closing reverses it in 0.2 s.
* **Window corners**: Square / Slightly rounded / Rounded, using only Windows 11's DWM rounding. The old clip-region approach caused white corners and was removed.
* **Steam playtime** is now the server-side number: Web API (if a key is set) → public Steam Community profile XML (no key) → local `localconfig.vdf`;
  the largest value wins. It refreshes on every GameIndex start, every 15 min, and ~20 s after a Steam game closes.
  **Achievements** likewise work without an API key from the public profile (`Game details = Public`).
* **Window material**: Mica / Mica Alt / Acrylic / None were hidden behind an opaque artwork layer. *Material visibility* lets it show through.
* **Startup animation** (toggle in Settings; click to skip) plays together with the startup sound.
* **12 desktop icon styles**. Icons in cards/tiles are enlarged to fill their space with the aspect ratio kept and tinted with the icon's own colour.
* **Global hotkey** explanation added next to the setting.

## Round 7
* **Open/close smoothness**: the window animation is a Storyboard (compositor thread, whole tree cached as one bitmap while it runs); while Settings or
  Game Details cover the window, the library views underneath are switched off completely (`LibraryHost`), so heavy pages no longer drop frames.
* **Steam achievements**: XML view → HTML achievements page fallback; HTTP 429 (rate limit) is detected and backed off for 15 min; bulk background
  refresh is limited to 6 games per pass (most recently played first). The refresh message now states what was found and why something is missing.
* **Desktop icon**: 12 new styles (6 with a geometric "G": Neon, Gradient, Orb, Hex, Shield, Minimal; plus Planet, Crystal, Star, Atom, Pad, Ember) and
  8 animations (Classic, Breathe, Float, Flip, Orbit, Ripples, Sparkle, Jelly). Regenerate with `tools/generate_icons.py`.
* **Fit mode**: the hero image is reflected below itself, so the lower half of a tall/maximized window is no longer black.
* **Startup animation**: fixed an invalid animation that left the cover on screen; there is now a 5 s watchdog.
