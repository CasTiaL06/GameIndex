using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GameIndex.Core;
using GameIndex.Core.Models;
using GameIndex.Core.Services;
using GameIndex.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace GameIndex.ViewModels;

public enum ViewMode { Carousel, Grid, List }
public enum SortMode { RecentlyPlayed, Alphabetical, MostPlayed, LeastPlayed, RecentlyAdded, LargestGames, AchievementCompletion }

public sealed record NavItem(string Key, string Glyph, string Label, bool IsHeader = false)
{
    public bool IsSelectable => !IsHeader;
    public Visibility HeaderVisibility => IsHeader ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ItemVisibility => IsHeader ? Visibility.Collapsed : Visibility.Visible;
}

public sealed partial class MainViewModel : ObservableObject
{
    private const string AllPlatforms = "All platforms", AllCategories = "All categories";
    private readonly AppHost _h;
    private readonly DispatcherQueue _dq;
    private readonly Dictionary<long, GameViewModel> _vms = new();
    private List<GameViewModel> _all = new();
    private bool _suppressFilter;

    [ObservableProperty] private ObservableCollection<GameViewModel> _games = new();
    [ObservableProperty] private GameViewModel? _selected;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _section = "home";
    [ObservableProperty] private int _sortIndex = 0;
    [ObservableProperty] private string _platformFilter = AllPlatforms;
    [ObservableProperty] private string _categoryFilter = AllCategories;
    [ObservableProperty] private ViewMode _viewMode = ViewMode.Carousel;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _sectionTitle = "Home";
    [ObservableProperty] private Visibility _emptyVisibility = Visibility.Collapsed;
    [ObservableProperty] private Visibility _detailsVisibility = Visibility.Collapsed;
    [ObservableProperty] private string _emptyMessage = "";

    public ObservableCollection<NavItem> NavItems { get; } = new();
    public ObservableCollection<string> PlatformOptions { get; } = new() { AllPlatforms };
    public ObservableCollection<string> CategoryOptions { get; } = new() { AllCategories };
    public ObservableCollection<AchievementItem> Achievements { get; } = new();
    public ObservableCollection<DlcItem> Dlcs { get; } = new();
    public ObservableCollection<SessionItem> Sessions { get; } = new();
    [ObservableProperty] private string _achievementsNote = "";
    [ObservableProperty] private string _dlcNote = "";

    public event Action? SelectionChanged;
    public event Action? Reloaded;
    public event Action? SelectedUpdated;
    public Visibility InfoVisibility => Selected is null ? Visibility.Collapsed : Visibility.Visible;
    partial void OnSelectedChanged(GameViewModel? value) => OnPropertyChanged(nameof(InfoVisibility));
    public Visibility CarouselVisibility => ViewMode == ViewMode.Carousel ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GridVisibility => ViewMode == ViewMode.Grid ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ListVisibility => ViewMode == ViewMode.List ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SortVisibility => Section is "recent" or "mostplayed" or "added" ? Visibility.Collapsed : Visibility.Visible;

    public MainViewModel(AppHost host, DispatcherQueue dq)
    {
        _h = host; _dq = dq;
        foreach (var i in host.Integrations) GameViewModel.Capabilities[i.Id] = i.Capabilities;
        GameViewModel.HasSteamKey = !string.IsNullOrEmpty(host.Settings.GetSteamApiKey());
        ViewMode = Enum.TryParse<ViewMode>(host.Settings.Current.DefaultLibraryView, out var vm) ? vm : ViewMode.Carousel;

        host.Tracker.RunningChanged += (id, running) => _dq.TryEnqueue(() =>
        {
            if (_vms.TryGetValue(id, out var v)) { v.IsRunning = running; v.OnPropertyChangedPublic(); }
            if (!running) _ = ReloadGameAsync(id);       // session ended: refresh playtime / last played
        });
        host.Discovery.LibraryChanged += () => _dq.TryEnqueue(async () => { await ReloadAsync(); host.Tracker.InvalidateWatchList(); _ = host.Info.RefreshStaleAsync(); });
        host.Info.GameUpdated += id => _dq.TryEnqueue(() => _ = ReloadGameAsync(id));
    }

    // ---------- loading ----------
    public async Task ReloadAsync()
    {
        var games = await Task.Run(() => _h.Db.GetGames());
        var cats = await Task.Run(() => _h.Db.GetCategories());
        _suppressFilter = true;
        var keep = new HashSet<long>(games.Select(g => g.Id));
        foreach (var gone in _vms.Keys.Where(k => !keep.Contains(k)).ToList()) _vms.Remove(gone);
        foreach (var g in games)
        {
            if (_vms.TryGetValue(g.Id, out var v)) v.Update(g);
            else _vms[g.Id] = new GameViewModel(g, Select, Launch);
            _vms[g.Id].IsRunning = _h.Tracker.IsRunning(g.Id);
        }
        _all = _vms.Values.ToList();

        SetOptions(PlatformOptions, AllPlatforms, games.Where(g => !string.IsNullOrEmpty(g.PlatformName)).Select(g => g.PlatformName).Distinct().OrderBy(x => x));
        SetOptions(CategoryOptions, AllCategories, cats);
        RebuildNav(games, cats);
        _suppressFilter = false;
        ApplyFilters();
        Reloaded?.Invoke();
    }

    private static void SetOptions(ObservableCollection<string> target, string first, IEnumerable<string> items)
    {
        var list = new[] { first }.Concat(items).ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear(); foreach (var i in list) target.Add(i);
    }

    private void RebuildNav(List<Game> games, List<string> cats)
    {
        NavItems.Clear();
        void A(string k, string g, string l, bool h = false) => NavItems.Add(new NavItem(k, g, Loc.T(l), h));
        A("home", "\uE80F", "Home"); A("all", "\uE7FC", "All Games"); A("installed", "\uE73E", "Installed Games"); A("favorites", "\uE734", "Favorites");
        A("recent", "\uE81C", "Recently Played"); A("mostplayed", "\uE9D2", "Most Played"); A("added", "\uE710", "Recently Added");
        var plats = games.GroupBy(g => g.PlatformId).Select(x => (Id: x.Key, Name: x.First().PlatformName)).OrderBy(x => x.Name).ToList();
        if (plats.Count > 0) { A("h1", "", "PLATFORMS", true); foreach (var p in plats) A("platform:" + p.Id, "\uE7F4", p.Name); }
        if (cats.Count > 0) { A("h2", "", "CATEGORIES", true); foreach (var c in cats) A("category:" + c, "\uE8EC", c); }
    }

    private async Task ReloadGameAsync(long id)
    {
        var g = await Task.Run(() => _h.Db.GetGame(id));
        if (g is null) { await ReloadAsync(); return; }
        if (_vms.TryGetValue(id, out var v)) { v.Update(g); v.IsRunning = _h.Tracker.IsRunning(id); }
        if (Selected?.Id == id) SelectedUpdated?.Invoke();
        if (Selected?.Id == id && DetailsVisibility == Visibility.Visible) await LoadDetailsAsync(id);
    }

    // ---------- filtering / sorting ----------
    partial void OnSearchTextChanged(string value) => ApplyFilters();
    partial void OnSortIndexChanged(int value) => ApplyFilters();
    partial void OnPlatformFilterChanged(string value) => ApplyFilters();
    partial void OnCategoryFilterChanged(string value) => ApplyFilters();
    partial void OnSectionChanged(string value) { OnPropertyChanged(nameof(SortVisibility)); ApplyFilters(); }
    partial void OnViewModeChanged(ViewMode value) { OnPropertyChanged(nameof(CarouselVisibility)); OnPropertyChanged(nameof(GridVisibility)); OnPropertyChanged(nameof(ListVisibility)); }

    private static string Title(string section) => section switch
    {
        "home" => "Home", "all" => "All Games", "installed" => "Installed Games", "favorites" => "Favorites", "recent" => "Recently Played",
        "mostplayed" => "Most Played", "added" => "Recently Added",
        var s when s.StartsWith("platform:") => PlatformIds.DisplayName(s[9..]), var s when s.StartsWith("category:") => s[9..], _ => "Library"
    };

    public void ApplyFilters()
    {
        if (_suppressFilter) return;
        IEnumerable<GameViewModel> q = _all;
        bool ownOrder = false;
        switch (Section)
        {
            case "home": case "installed": q = q.Where(v => v.Model.IsInstalled); break;
            case "favorites": q = q.Where(v => v.IsFavorite); break;
            case "recent": q = q.Where(v => v.Model.LastPlayedUtc != null).OrderByDescending(v => v.Model.LastPlayedUtc); ownOrder = true; break;
            case "mostplayed": q = q.Where(v => v.Model.TotalPlaytimeSeconds > 0).OrderByDescending(v => v.Model.TotalPlaytimeSeconds); ownOrder = true; break;
            case "added": q = q.OrderByDescending(v => v.Model.DateAddedUtc); ownOrder = true; break;
            case var s when s.StartsWith("platform:"): q = q.Where(v => v.Model.PlatformId == s[9..]); break;
            case var s when s.StartsWith("category:"): q = q.Where(v => v.Category.Equals(s[9..], StringComparison.OrdinalIgnoreCase)); break;
        }
        if (PlatformFilter != AllPlatforms) q = q.Where(v => v.PlatformName == PlatformFilter);
        if (CategoryFilter != AllCategories) q = q.Where(v => v.Category.Equals(CategoryFilter, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(SearchText)) q = q.Where(v => v.Title.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase));
        if (!ownOrder)
        {
            var title = StringComparer.CurrentCultureIgnoreCase;
            q = (SortMode)SortIndex switch
            {
                SortMode.Alphabetical => q.OrderBy(v => v.Title, title),
                SortMode.MostPlayed => q.OrderByDescending(v => v.Model.TotalPlaytimeSeconds).ThenBy(v => v.Title, title),
                SortMode.LeastPlayed => q.OrderBy(v => v.Model.TotalPlaytimeSeconds).ThenBy(v => v.Title, title),
                SortMode.RecentlyAdded => q.OrderByDescending(v => v.Model.DateAddedUtc),
                SortMode.LargestGames => q.OrderByDescending(v => v.Model.SizeBytes ?? -1).ThenBy(v => v.Title, title),
                SortMode.AchievementCompletion => q.OrderByDescending(v => v.AchievementPercent).ThenBy(v => v.Title, title),
                _ => q.OrderByDescending(v => v.Model.LastPlayedUtc ?? DateTime.MinValue).ThenBy(v => v.Title, title)
            };
        }
        var list = q.ToList();
        var keepId = Selected?.Id;
        Games = new ObservableCollection<GameViewModel>(list);
        SectionTitle = Loc.T(Title(Section));
        Select(list.FirstOrDefault(v => v.Id == keepId) ?? list.FirstOrDefault());
        EmptyVisibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyMessage = Loc.T(_all.Count == 0
            ? "No games yet. GameIndex is scanning your installed platforms. You can also add a game manually or drop an EXE here."
            : "No games match this view.");
    }

    // ---------- selection ----------
    public void Select(GameViewModel? v)
    {
        if (ReferenceEquals(v, Selected)) { SelectionChanged?.Invoke(); return; }
        if (Selected is not null) Selected.IsSelected = false;
        Selected = v;
        if (v is not null) v.IsSelected = true;
        SelectionChanged?.Invoke();
    }

    /// <summary>All games, most recently played first (used by the quick switcher).</summary>
    public IReadOnlyList<GameViewModel> AllGames => _all.OrderByDescending(v => v.Model.LastPlayedUtc ?? DateTime.MinValue).ThenBy(v => v.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Selects a game even if the current section/filters hide it.</summary>
    public void JumpTo(GameViewModel v)
    {
        if (!Games.Contains(v))
        {
            SearchText = ""; PlatformFilter = PlatformOptions[0]; CategoryFilter = CategoryOptions[0];
            Section = "all";
        }
        Select(v);
    }

    /// <summary>Jumps to the first game in the current list whose title starts with the typed text.</summary>
    public bool JumpToPrefix(string prefix)
    {
        var hit = Games.FirstOrDefault(g => g.Title.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase));
        if (hit is null) return false;
        Select(hit); return true;
    }

    public void MoveToEdge(bool last) { if (Games.Count > 0) Select(last ? Games[^1] : Games[0]); }

    public int SelectedIndex => Selected is null ? -1 : Games.IndexOf(Selected);

    public void Move(int delta)
    {
        if (Games.Count == 0) return;
        var i = Math.Clamp((SelectedIndex < 0 ? 0 : SelectedIndex) + delta, 0, Games.Count - 1);
        Select(Games[i]);
    }

    // ---------- actions ----------
    public void Launch(GameViewModel? v = null)
    {
        v ??= Selected;
        if (v is null) return;
        var r = _h.Launcher.Launch(v.Model);
        StatusText = r.Message;
    }

    public async Task ToggleFavoriteAsync(GameViewModel? v = null)
    {
        v ??= Selected; if (v is null) return;
        await Task.Run(() => _h.Db.SetFavorite(v.Id, !v.IsFavorite));
        await ReloadGameAsync(v.Id);
        if (Section == "favorites") ApplyFilters();
    }

    public void OpenFolder(GameViewModel? v = null) { v ??= Selected; if (v is not null) StatusText = _h.Launcher.OpenInstallFolder(v.Model).Message; }

    public async Task RemoveAsync(GameViewModel v)
    {
        // Platform games reappear on the next scan if still installed; manual games are removed for good.
        await Task.Run(() => _h.Db.DeleteGame(v.Id));
        _h.Tracker.InvalidateWatchList();
        await ReloadAsync();
        StatusText = Loc.F("Removed {0} from the library.", v.Title) + (v.IsManual ? "" : Loc.T(" It will return on the next scan while it is installed."));
    }

    public async Task RefreshAsync(GameViewModel? v = null)
    {
        v ??= Selected; if (v is null) return;
        StatusText = Loc.F("Refreshing {0}…", v.Title);
        var notes = await _h.Info.RefreshGameAsync(v.Id, RefreshKind.All);
        StatusText = string.IsNullOrWhiteSpace(notes) ? Loc.F("{0} updated.", v.Title) : notes;
    }

    public async Task ScanNowAsync()
    {
        IsScanning = true; StatusText = Loc.T("Scanning for games…");
        try { await _h.Discovery.ScanAsync(); await ReloadAsync(); _ = _h.Info.RefreshStaleAsync(); StatusText = Loc.F("Scan finished. {0} game(s) in library.", _all.Count); }
        finally { IsScanning = false; }
    }

    public async Task<long> AddManualAsync(Game g, string? cover, string? background)
    {
        var id = await Task.Run(() => _h.Db.InsertManual(g));
        await Task.Run(() =>
        {
            if (cover is not null) _h.Artwork.ImportCustom(id, "cover", cover);
            if (background is not null) _h.Artwork.ImportCustom(id, "background", background);
        });
        _h.Tracker.InvalidateWatchList();
        await ReloadAsync();
        _ = _h.Info.RefreshGameAsync(id, RefreshKind.Size | RefreshKind.Artwork);
        return id;
    }

    public async Task SaveEditAsync(Game g, string? cover, string? background)
    {
        await Task.Run(() => _h.Db.UpdateGameDetails(g));
        await Task.Run(() =>
        {
            if (cover is not null) _h.Artwork.ImportCustom(g.Id, "cover", cover);
            if (background is not null) _h.Artwork.ImportCustom(g.Id, "background", background);
        });
        _h.Tracker.InvalidateWatchList();
        await ReloadAsync();
    }

    // ---------- details overlay ----------
    public async Task OpenDetailsAsync(GameViewModel? v = null)
    {
        v ??= Selected; if (v is null) return;
        Select(v);
        DetailsVisibility = Visibility.Visible;
        await LoadDetailsAsync(v.Id);
    }

    public void CloseDetails() => DetailsVisibility = Visibility.Collapsed;

    private async Task LoadDetailsAsync(long id)
    {
        var (ach, dlc, ses) = await Task.Run(() => (_h.Db.GetAchievements(id), _h.Db.GetDlcs(id), _h.Db.GetSessions(id, 100)));
        Achievements.Clear(); Dlcs.Clear(); Sessions.Clear();
        foreach (var a in ach)
            Achievements.Add(new AchievementItem(a.Name, a.Description ?? "", a.Unlocked ? Loc.T("Unlocked") + (a.UnlockedUtc is DateTime d ? " • " + Format.Date(d) : "") : Loc.T("Locked"), a.Unlocked, a.Unlocked ? a.IconUrl : (a.IconGrayUrl ?? a.IconUrl)));
        foreach (var d in dlc) Dlcs.Add(new DlcItem(d.Name, Loc.T(d.Installed ? "Installed" : "Owned, not installed")));
        foreach (var s in ses)
            Sessions.Add(new SessionItem(s.StartUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), Loc.Duration((long)s.Duration.TotalSeconds),
                s.EndUtc is null ? Loc.T("In progress") : (s.Recovered ? Loc.T("Recovered after unexpected shutdown") : "")));
        var g = Selected?.Model;
        AchievementsNote = ach.Count > 0 ? "" : (Selected?.AchievementsDetail ?? "");
        DlcNote = dlc.Count > 0 ? Loc.T("Owned-but-not-installed DLC is not exposed by this platform; only installed DLC is listed.") :
            (Selected?.DlcDetail ?? "");
    }
}
