using GameIndex.Core;
using GameIndex.Core.Models;
using GameIndex.Services;
using GameIndex.ViewModels;
using GameIndex.Views;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace GameIndex;

public sealed partial class MainWindow : Window
{
    private const double SlotWidth = 136, CarouselMargin = 28;
    public MainViewModel ViewModel { get; }

    private readonly AppHost _h;
    private readonly SettingsViewModel _settingsVm;
    private readonly TrayIcon _tray;
    private readonly GamepadNavigator _pad;
    private bool _exiting, _syncing, _sidebarExpanded;
    private ImageSource? _currentBg;
    private Storyboard? _bgSb;
    private string _lang = "";
    private List<GameViewModel> _quickAll = new();
    private string _typeBuf = "";
    private DateTime _typeAt = DateTime.MinValue;

    public MainWindow(AppHost host, bool startHidden)
    {
        _h = host;
        ViewModel = new MainViewModel(host, Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());   // must exist before InitializeComponent (x:Bind)
        InitializeComponent();

        _settingsVm = new SettingsViewModel(host, ApplyAppearance);
        SettingsHost.Initialize(_settingsVm, this, ViewModel, () => HideSettings());
        DetailsHost.Initialize(ViewModel);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDrag);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1560, 940));
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "GameIndex.ico");
        if (File.Exists(ico)) AppWindow.SetIcon(ico);

        _tray = new TrayIcon(Platform.Hwnd(this));
        _tray.OpenRequested += ShowFromTray;
        _tray.ScanRequested += () => DispatcherQueue.TryEnqueue(() => _ = ViewModel.ScanNowAsync());
        _tray.ExitRequested += () => DispatcherQueue.TryEnqueue(() => { _exiting = true; Close(); });

        _pad = new GamepadNavigator(DispatcherQueue);
        _pad.Action += OnPad;

        AppWindow.Closing += (_, e) =>
        {
            if (!_exiting && _h.Settings.Current.MinimizeToTray) { e.Cancel = true; AppWindow.Hide(); }
        };
        Closed += (_, _) => { _pad.Dispose(); _tray.Dispose(); _h.Dispose(); };

        ViewModel.SelectionChanged += OnSelectionChanged;
        ViewModel.Reloaded += SyncControls;

        var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        clock.Start();
        ClockText.Text = DateTime.Now.ToString("t", System.Globalization.CultureInfo.CurrentCulture);

        Root.Loaded += async (_, _) =>
        {
            ApplyAppearance();
            if (_h.Paths.Warning is not null) ViewModel.StatusText = _h.Paths.Warning;
            ViewBar.SelectedItem = ViewBar.Items[(int)ViewModel.ViewMode];
            await ViewModel.ReloadAsync();
            _h.Artwork.ExeIconProvider = Platform.ExtractExeIconAsync;
            _h.Tracker.Start();
            _h.Discovery.StartBackground();
            _ = _h.Info.RefreshStaleAsync();
            CarouselScroller.Focus(FocusState.Programmatic);
        };

        if (startHidden) DispatcherQueue.TryEnqueue(() => AppWindow.Hide());
    }

    // ---------- appearance ----------
    private void ApplyLanguage()
    {
        var code = _h.Settings.Current.Language;
        if (code == _lang) return;
        _lang = code;
        Loc.SetLanguage(code);
        Loc.Apply(Root);
        if (MoreButton.Flyout is MenuFlyout mf) foreach (var it in mf.Items) if (it is MenuFlyoutItem m) Loc.FixText(m);
        _ = ViewModel.ReloadAsync();           // rebuilds nav labels, status texts, dates and empty-state message
        _settingsVm.RefreshIntegrations();
    }

    private void ApplyAppearance()
    {
        ApplyLanguage();
        var s = _h.Settings.Current;
        ThemeService.Apply(s, this, Root);
        DimOverlay.Opacity = Math.Clamp(s.BackgroundDimPercent / 100.0, 0, 0.9);
        Sidebar.Width = _sidebarExpanded || !s.SidebarCompact ? 230 : 64;
        if (s.MinimizeToTray) _tray.Show(); else _tray.Hide();
        _currentBg = null; UpdateBackground(false);
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        _sidebarExpanded = !(_sidebarExpanded || !_h.Settings.Current.SidebarCompact);
        Sidebar.Width = _sidebarExpanded || !_h.Settings.Current.SidebarCompact ? 230 : 64;
    }

    private void ShowFromTray() => DispatcherQueue.TryEnqueue(() => { AppWindow.Show(); Activate(); });

    // ---------- background cross-fade ----------
    private void UpdateBackground(bool animate = true)
    {
        ImageSource? src = ViewModel.Selected?.BackgroundImage;
        var custom = _h.Settings.Current.CustomBackgroundImage;
        if (src is null && !string.IsNullOrEmpty(custom) && File.Exists(custom))
            src = new BitmapImage(new Uri(custom)) { DecodePixelWidth = 1920 };
        if (ReferenceEquals(src, _currentBg)) return;
        _currentBg = src;

        if (_bgSb is not null) { _bgSb.Stop(); if (BgB.Source is not null) BgA.Source = BgB.Source; BgB.Opacity = 0; BgA.Opacity = 1; }
        if (!animate || !_h.Settings.Current.AnimationsEnabled) { BgA.Source = src; BgA.Opacity = 1; BgB.Source = null; BgB.Opacity = 0; return; }

        var ms = 380.0 * 100 / Math.Clamp(_h.Settings.Current.AnimationSpeedPercent, 25, 400);
        var sb = new Storyboard();
        var fade = new DoubleAnimation { Duration = new Duration(TimeSpan.FromMilliseconds(ms)), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } };
        if (src is null) { fade.To = 0; Storyboard.SetTarget(fade, BgA); }
        else { BgB.Source = src; BgB.Opacity = 0; fade.To = 1; Storyboard.SetTarget(fade, BgB); }
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);
        sb.Completed += (_, _) =>
        {
            sb.Stop();
            BgA.Source = src; BgA.Opacity = 1; BgB.Source = null; BgB.Opacity = 0;
            if (ReferenceEquals(_bgSb, sb)) _bgSb = null;
        };
        _bgSb = sb;
        sb.Begin();
    }

    private void OnSelectionChanged()
    {
        UpdateBackground();
        CenterSelected();
        _syncing = true;
        LibraryGrid.SelectedItem = ViewModel.Selected;
        LibraryList.SelectedItem = ViewModel.Selected;
        _syncing = false;
        if (ViewModel.Selected is not null && LibraryList.Visibility == Visibility.Visible) LibraryList.ScrollIntoView(ViewModel.Selected);
        if (ViewModel.Selected is not null && LibraryGrid.Visibility == Visibility.Visible) LibraryGrid.ScrollIntoView(ViewModel.Selected);
    }

    /// <summary>Slots have a fixed width, so the target scroll offset is computed directly (no layout dependency).</summary>
    private void CenterSelected()
    {
        var i = ViewModel.SelectedIndex;
        if (i < 0 || CarouselScroller.ViewportWidth <= 0) return;
        var target = CarouselMargin + i * SlotWidth + SlotWidth / 2 - CarouselScroller.ViewportWidth / 2;
        CarouselScroller.ChangeView(Math.Max(0, target), null, null, !_h.Settings.Current.AnimationsEnabled);
    }

    private void SyncControls()
    {
        _syncing = true;
        if (!ViewModel.PlatformOptions.Contains(ViewModel.PlatformFilter)) ViewModel.PlatformFilter = ViewModel.PlatformOptions[0];
        if (!ViewModel.CategoryOptions.Contains(ViewModel.CategoryFilter)) ViewModel.CategoryFilter = ViewModel.CategoryOptions[0];
        PlatformCombo.SelectedItem = ViewModel.PlatformFilter;
        CategoryCombo.SelectedItem = ViewModel.CategoryFilter;
        NavList.SelectedItem = ViewModel.NavItems.FirstOrDefault(n => n.Key == ViewModel.Section);
        _syncing = false;
        _settingsVm.RefreshIntegrations();
    }

    // ---------- toolbar / nav ----------
    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || NavList.SelectedItem is not NavItem n) return;
        HideSettings();
        ViewModel.Section = n.Key;
    }
    private void PlatformCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_syncing && PlatformCombo.SelectedItem is string s) ViewModel.PlatformFilter = s; }
    private void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_syncing && CategoryCombo.SelectedItem is string s) ViewModel.CategoryFilter = s; }
    private void ViewBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var i = sender.Items.IndexOf(sender.SelectedItem);
        if (i < 0) return;
        ViewModel.ViewMode = (ViewMode)i;
        DispatcherQueue.TryEnqueue(CenterSelected);
    }
    private void LibraryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (e.AddedItems.FirstOrDefault() is GameViewModel v) ViewModel.Select(v);
    }
    private void Library_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) { if (ReferenceEquals(sender, LibraryList)) ViewModel.Launch(); }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CloseDetails(); CloseQuick();
        SettingsHost.Visibility = Visibility.Visible; _settingsVm.Load();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Loc.Apply(SettingsHost));
    }
    private void HideSettings() => SettingsHost.Visibility = Visibility.Collapsed;
    private void ScanButton_Click(object sender, RoutedEventArgs e) => _ = ViewModel.ScanNowAsync();
    private async void AddButton_Click(object sender, RoutedEventArgs e) => await ShowEditDialogAsync(null, null);

    // ---------- carousel interaction ----------
    private void Carousel_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(CarouselScroller).Properties.MouseWheelDelta;
        if (delta == 0) return;
        ViewModel.Move(delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    private bool OverlayOpen => ViewModel.DetailsVisibility == Visibility.Visible || SettingsHost.Visibility == Visibility.Visible || QuickHost.Visibility == Visibility.Visible;

    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var focus = FocusManager.GetFocusedElement(Root.XamlRoot);
        var typing = focus is TextBox or PasswordBox or NumberBox or ComboBox or ColorPicker;
        var onButton = focus is Button or ToggleSwitch or CheckBox;

        var ctrl = CtrlDown();
        if (e.Key == VirtualKey.Escape) { GoBack(); e.Handled = true; return; }
        if (ctrl && e.Key == VirtualKey.K) { if (QuickHost.Visibility == Visibility.Visible) CloseQuick(); else OpenQuick(); e.Handled = true; return; }
        if (typing || OverlayOpen) return;

        switch (e.Key)
        {
            case VirtualKey.Left when !FocusInInfo(focus):  ViewModel.Move(-1); e.Handled = true; break;
            case VirtualKey.Right when !FocusInInfo(focus): ViewModel.Move(1); e.Handled = true; break;
            case VirtualKey.Down when ViewModel.ViewMode == ViewMode.Carousel && !FocusInInfo(focus): PlayButton.Focus(FocusState.Keyboard); e.Handled = true; break;
            case VirtualKey.Up when FocusInInfo(focus): CarouselScroller.Focus(FocusState.Keyboard); e.Handled = true; break;
            case VirtualKey.Enter when !onButton: ViewModel.Launch(); e.Handled = true; break;
            case VirtualKey.PageUp: ViewModel.Move(-10); e.Handled = true; break;
            case VirtualKey.PageDown: ViewModel.Move(10); e.Handled = true; break;
            case VirtualKey.Home when !FocusInInfo(focus): ViewModel.MoveToEdge(false); e.Handled = true; break;
            case VirtualKey.End when !FocusInInfo(focus): ViewModel.MoveToEdge(true); e.Handled = true; break;
            case VirtualKey.F when Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down):
                SearchBox.Focus(FocusState.Keyboard); e.Handled = true; break;
            default:
                // type-ahead: press letters/digits to jump to a game by its first characters
                var k = (int)e.Key;
                if (!ctrl && (k is >= 65 and <= 90 || k is >= 48 and <= 57)) { TypeAhead((char)k); e.Handled = true; }
                break;
        }
    }

    private static bool CtrlDown() =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void TypeAhead(char c)
    {
        var now = DateTime.UtcNow;
        if ((now - _typeAt).TotalMilliseconds > 900) _typeBuf = "";
        _typeAt = now;
        _typeBuf += c;
        if (!ViewModel.JumpToPrefix(_typeBuf) && _typeBuf.Length > 1) { _typeBuf = c.ToString(); ViewModel.JumpToPrefix(_typeBuf); }
        ViewModel.StatusText = _typeBuf;
    }

    // ---------- quick switcher ----------
    private void QuickButton_Click(object sender, RoutedEventArgs e) => OpenQuick();

    private void OpenQuick()
    {
        HideSettings(); ViewModel.CloseDetails();
        _quickAll = ViewModel.AllGames.ToList();
        QuickHost.Visibility = Visibility.Visible;
        QuickBox.Text = "";
        FilterQuick("");
        DispatcherQueue.TryEnqueue(() => { Loc.Apply(QuickHost); QuickBox.Focus(FocusState.Programmatic); });
    }

    private void CloseQuick()
    {
        QuickHost.Visibility = Visibility.Collapsed;
        CarouselScroller.Focus(FocusState.Programmatic);
    }

    private static int QuickRank(string title, string q)
    {
        if (q.Length == 0) return 3;
        if (title.StartsWith(q, StringComparison.CurrentCultureIgnoreCase)) return 0;
        if (title.Contains(q, StringComparison.CurrentCultureIgnoreCase)) return 1;
        var i = 0;                                                  // subsequence match: "gtv" finds "Grand Theft Auto V"
        foreach (var ch in title) if (i < q.Length && char.ToLowerInvariant(ch) == char.ToLowerInvariant(q[i])) i++;
        return i == q.Length ? 2 : -1;
    }

    private void FilterQuick(string text)
    {
        var q = text.Trim();
        var list = _quickAll.Select(g => (g, r: QuickRank(g.Title, q))).Where(x => x.r >= 0).OrderBy(x => x.r).Select(x => x.g).Take(60).ToList();
        QuickList.ItemsSource = list;
        if (list.Count > 0) QuickList.SelectedIndex = 0;
    }

    private void QuickBox_TextChanged(object sender, TextChangedEventArgs e) => FilterQuick(QuickBox.Text);

    private void QuickBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var count = (QuickList.ItemsSource as List<GameViewModel>)?.Count ?? 0;
        switch (e.Key)
        {
            case VirtualKey.Down: if (count > 0) QuickList.SelectedIndex = Math.Min(count - 1, QuickList.SelectedIndex + 1); QuickList.ScrollIntoView(QuickList.SelectedItem); e.Handled = true; break;
            case VirtualKey.Up: if (count > 0) QuickList.SelectedIndex = Math.Max(0, QuickList.SelectedIndex - 1); QuickList.ScrollIntoView(QuickList.SelectedItem); e.Handled = true; break;
            case VirtualKey.Enter: QuickChoose(QuickList.SelectedItem as GameViewModel, CtrlDown()); e.Handled = true; break;
        }
    }

    private void QuickList_ItemClick(object sender, ItemClickEventArgs e) => QuickChoose(e.ClickedItem as GameViewModel, false);
    private void QuickHost_Tapped(object sender, TappedRoutedEventArgs e) => CloseQuick();
    private void QuickPanel_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void QuickChoose(GameViewModel? v, bool play)
    {
        if (v is null) return;
        CloseQuick();
        ViewModel.JumpTo(v);
        if (play) ViewModel.Launch(v);
    }

    private bool FocusInInfo(object? focus) => focus is DependencyObject d && VisualTreeHelperContains(InfoPanel, d);
    private static bool VisualTreeHelperContains(DependencyObject root, DependencyObject node)
    {
        for (var n = node; n is not null; n = VisualTreeHelper.GetParent(n)) if (ReferenceEquals(n, root)) return true;
        return false;
    }

    private void GoBack()
    {
        if (QuickHost.Visibility == Visibility.Visible) CloseQuick();
        else if (SettingsHost.Visibility == Visibility.Visible) HideSettings();
        else if (ViewModel.DetailsVisibility == Visibility.Visible) ViewModel.CloseDetails();
        else if (!string.IsNullOrEmpty(ViewModel.SearchText)) ViewModel.SearchText = "";
        else if (ViewModel.Section != "home") ViewModel.Section = "home";
        SyncControls();
    }

    private void OnPad(PadAction a)
    {
        if (!IsActive()) return;
        switch (a)
        {
            case PadAction.Left when !OverlayOpen: ViewModel.Move(-1); break;
            case PadAction.Right when !OverlayOpen: ViewModel.Move(1); break;
            case PadAction.PageLeft when !OverlayOpen: ViewModel.Move(-10); break;
            case PadAction.PageRight when !OverlayOpen: ViewModel.Move(10); break;
            case PadAction.Down when !OverlayOpen: PlayButton.Focus(FocusState.Keyboard); break;
            case PadAction.Up when !OverlayOpen: CarouselScroller.Focus(FocusState.Keyboard); break;
            case PadAction.Accept when !OverlayOpen: ViewModel.Launch(); break;
            case PadAction.Favorite when !OverlayOpen: _ = ViewModel.ToggleFavoriteAsync(); break;
            case PadAction.Back: GoBack(); break;
        }
    }
    private bool IsActive() => Root.XamlRoot is not null && Visible;

    // ---------- info panel actions ----------
    private void Play_Click(object sender, RoutedEventArgs e) => ViewModel.Launch();
    private async void Details_Click(object sender, RoutedEventArgs e) { await ViewModel.OpenDetailsAsync(); DetailsHost.SelectTab(0); DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Loc.Apply(DetailsHost)); }
    private async void Achievements_Click(object sender, RoutedEventArgs e) { await ViewModel.OpenDetailsAsync(); DetailsHost.SelectTab(1); DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Loc.Apply(DetailsHost)); }
    private async void Dlc_Click(object sender, RoutedEventArgs e) { await ViewModel.OpenDetailsAsync(); DetailsHost.SelectTab(2); DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Loc.Apply(DetailsHost)); }
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => ViewModel.OpenFolder();
    private async void Favorite_Click(object sender, RoutedEventArgs e) => await ViewModel.ToggleFavoriteAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();
    private async void Edit_Click(object sender, RoutedEventArgs e) { if (ViewModel.Selected is { } v) await ShowEditDialogAsync(v.Model, null); }
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } v) return;
        var dlg = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.F("Remove {0}?", v.Title),
            Content = Loc.T(v.IsManual ? "The game is removed from GameIndex (its files are not touched). Playtime history for it is deleted."
                                       : "The game is removed from GameIndex (its files are not touched). It will come back on the next scan while it is still installed."),
            PrimaryButtonText = Loc.T("Remove"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Close
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary) await ViewModel.RemoveAsync(v);
    }

    // ---------- add / edit / drag & drop ----------
    public async Task ShowEditDialogAsync(Game? existing, string? prefillExe)
    {
        var dlg = new GameEditDialog(this, existing, prefillExe, _h.Db.GetCategories()) { XamlRoot = Root.XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || dlg.Result is not { } g) return;
        if (existing is null) { var id = await ViewModel.AddManualAsync(g, dlg.CoverFile, dlg.BackgroundFile); ViewModel.Select(ViewModel.Games.FirstOrDefault(v => v.Id == id)); }
        else await ViewModel.SaveEditAsync(g, dlg.CoverFile, dlg.BackgroundFile);
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems)) { e.AcceptedOperation = DataPackageOperation.Copy; e.DragUIOverride.Caption = "Add game"; }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var f in items.OfType<Windows.Storage.StorageFile>().Where(f => f.FileType.Equals(".exe", StringComparison.OrdinalIgnoreCase)))
            await ShowEditDialogAsync(null, f.Path);
    }
}
