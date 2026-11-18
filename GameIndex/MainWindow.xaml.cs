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
    private FloatingIcon? _floating;
    private GlobalHotkey _hotkey = null!;
    private string? _hotkeyRegistered;
    private Windows.Graphics.RectInt32 _normal;
    private bool _animating, _startHidden;
    private string _lastDefaultView = "", _lastArtFit = "";
    private readonly Dictionary<string, Windows.UI.Color> _tints = new();
    private Brush? _defaultFallback;

    private string? _pendingCommand;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _jlTimer;

    public MainWindow(AppHost host, bool startHidden, string? initialCommand = null)
    {
        _h = host;
        _pendingCommand = initialCommand;
        ViewModel = new MainViewModel(host, Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());   // must exist before InitializeComponent (x:Bind)
        InitializeComponent();

        _settingsVm = new SettingsViewModel(host, ApplyAppearance);
        SettingsHost.Initialize(_settingsVm, this, ViewModel, () => HideSettings());
        DetailsHost.Initialize(ViewModel);

        ExtendsContentIntoTitleBar = true;
        Platform.RoundWindow(this, host.Settings.Current.WindowRoundness);
        _lastDefaultView = host.Settings.Current.DefaultLibraryView; _lastArtFit = host.Settings.Current.ArtFit;
        SetTitleBar(TitleDrag);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1560, 940));
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "GameIndex.ico");
        if (File.Exists(ico)) AppWindow.SetIcon(ico);

        _tray = new TrayIcon(Platform.Hwnd(this));
        _tray.OpenRequested += ShowFromTray;
        _tray.ToggleRequested += () => DispatcherQueue.TryEnqueue(ToggleMain);
        _tray.ToggleIconRequested += () => DispatcherQueue.TryEnqueue(() => { _h.Settings.Current.FloatingIconEnabled = !_h.Settings.Current.FloatingIconEnabled; _h.Settings.Save(); _settingsVm.Load(); ApplyShell(); });
        _hotkey = new GlobalHotkey(Platform.Hwnd(this));
        _hotkey.Pressed += () => DispatcherQueue.TryEnqueue(ToggleMain);
        _settingsVm.ResetIconRequested += () => { _floating?.ResetPosition(); _h.Settings.Save(); };
        _defaultFallback = FallbackGradient.Fill;
        _tray.ScanRequested += () => DispatcherQueue.TryEnqueue(() => _ = ViewModel.ScanNowAsync());
        _tray.ExitRequested += () => DispatcherQueue.TryEnqueue(() => { _exiting = true; Close(); });

        _pad = new GamepadNavigator(DispatcherQueue);
        _pad.Action += OnPad;

        AppWindow.Closing += (_, e) =>
        {
            if (!_exiting && _h.Settings.Current.KeepRunningInBackground) { e.Cancel = true; HideMain(true); }   // only the window closes; tray, desktop icon and tracking keep running
        };
        AppWindow.Changed += (_, a) =>
        {
            if ((a.DidSizeChange || a.DidPositionChange) && !_animating) RememberNormal();
        };
        Closed += (_, _) => { _floating?.Close(); _hotkey.Dispose(); _pad.Dispose(); _tray.Dispose(); _h.Dispose(); };

        ViewModel.SelectionChanged += OnSelectionChanged;
        ViewModel.Reloaded += SyncControls;
        ViewModel.Reloaded += ScheduleJumpList;
        ViewModel.SelectedUpdated += () => { UpdateBackground(); UpdateArt(); };

        var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        clock.Start();
        ClockText.Text = DateTime.Now.ToString("t", System.Globalization.CultureInfo.CurrentCulture);

        Root.Loaded += async (_, _) =>
        {
            ApplyAppearance();
            if (_startHidden) { AppWindow.Hide(); AppWindow.MoveAndResize(DefaultRect()); _startHidden = false; }
            else { PlayIntro(); SoundService.PlayStartup(_h.Settings.Current.StartupSound, _h.Settings.Current.SoundsEnabled); }
            ApplyShell();
            if (_h.Paths.Warning is not null) ViewModel.StatusText = _h.Paths.Warning;
            ViewBar.SelectedItem = ViewBar.Items[(int)ViewModel.ViewMode];
            await ViewModel.ReloadAsync();
            if (_pendingCommand is not null) { var c = _pendingCommand; _pendingCommand = null; RunCommand(c); }
            _h.Artwork.ExeIconProvider = Platform.ExtractExeIconAsync;
            _h.Tracker.Start();
            _h.Discovery.StartBackground();
            _ = _h.Info.RefreshStaleAsync(forceStats: true);          // pull the real Steam playtime as soon as GameIndex opens
            CarouselScroller.Focus(FocusState.Programmatic);
        };

        // Started hidden (e.g. with Windows): the window must still be activated once so XAML loads; park it off-screen until Loaded hides it.
        _startHidden = startHidden;
        if (startHidden) AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
    }

    // ---------- taskbar / single-instance commands ----------
    /// <summary>Called from the pipe server thread (jump-list clicks, second launches).</summary>
    public void HandleCommand(string msg) => DispatcherQueue.TryEnqueue(() => RunCommand(msg));

    private void RunCommand(string msg)
    {
        if (msg == "scan") { _ = ViewModel.ScanNowAsync(); return; }
        if (msg.StartsWith("launch:") && long.TryParse(msg[7..], out var id))
        {
            var v = ViewModel.AllGames.FirstOrDefault(g => g.Id == id);
            if (v is not null) { ViewModel.JumpTo(v); ViewModel.Launch(v); }
            return;
        }
        ShowFromTray();                                     // "show"
    }

    private void ScheduleJumpList()
    {
        if (_jlTimer is null)
        {
            _jlTimer = DispatcherQueue.CreateTimer();
            _jlTimer.Interval = TimeSpan.FromSeconds(3);
            _jlTimer.IsRepeating = false;
            _jlTimer.Tick += (_, _) => UpdateJumpList();
        }
        _jlTimer.Stop(); _jlTimer.Start();                   // debounce: library reloads can come in bursts
    }

    private void UpdateJumpList()
    {
        if (!_h.Settings.Current.TaskbarJumpList) { Taskbar.Clear(); return; }
        var all = ViewModel.AllGames.Where(v => v.Model.IsInstalled).ToList();
        var recent = all.Where(v => v.Model.LastPlayedUtc is not null).Take(6).ToList();
        var favs = all.Where(v => v.IsFavorite && !recent.Contains(v)).Take(6).ToList();
        JumpItem Map(GameViewModel v) => new(v.Title, $"--launch {v.Id}", v.Model.ExePath);
        Taskbar.Update(recent.Select(Map), favs.Select(Map));
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
        UpdateSidebar();
    }

    private void ApplyAppearance()
    {
        ApplyLanguage();
        ScheduleJumpList();
        var s = _h.Settings.Current;
        if (s.DefaultLibraryView != _lastDefaultView)            // "Default library view" applies immediately
        {
            _lastDefaultView = s.DefaultLibraryView;
            if (Enum.TryParse<ViewMode>(s.DefaultLibraryView, out var vmode)) { ViewModel.ViewMode = vmode; ViewBar.SelectedItem = ViewBar.Items[(int)vmode]; }
        }
        if (s.ArtFit != _lastArtFit) { _lastArtFit = s.ArtFit; _ = ViewModel.ReloadAsync(); }   // cards re-render with the new scaling
        ApplyBackgroundStretch();
        Platform.RoundWindow(this, s.WindowRoundness);
        BackgroundHost.Opacity = s.TransparencyEnabled && s.Backdrop != "None" ? 1 - Math.Clamp(s.MaterialVisibility, 0, 80) / 100.0 : 1.0;
        ThemeService.Apply(s, this, Root);
        DimOverlay.Opacity = Math.Clamp(s.BackgroundDimPercent / 100.0, 0, 0.9);
        UpdateSidebar();
        _tray.Show();                                  // the tray icon is always present (left click = show/hide)
        ApplyShell();
        _currentBg = null; UpdateBackground(false);
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        _sidebarExpanded = !(_sidebarExpanded || !_h.Settings.Current.SidebarCompact);
        UpdateSidebar();
    }

    private bool SidebarOpen => _sidebarExpanded || !_h.Settings.Current.SidebarCompact;

    /// <summary>Compact sidebar = icons only (no clipped texts), every item explains itself in a tooltip; the ☰ button expands it.</summary>
    private void UpdateSidebar()
    {
        var open = SidebarOpen;
        Sidebar.Width = open ? 236 : 72;
        ViewModel.SidebarExpanded = open;                      // nav items hide labels / turn headers into dividers
        foreach (var (btn, text, tip) in new[] { (AddButton, AddText, "Add Game"), (ScanButton, ScanText, "Scan for games"), (SettingsButton, SettingsText, "Settings") })
        {
            text.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            btn.HorizontalContentAlignment = open ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            btn.Padding = open ? new Thickness(14, 0, 14, 0) : new Thickness(0);
            ToolTipService.SetToolTip(btn, Loc.T(tip));
        }
        ToolTipService.SetToolTip(MenuButton, Loc.T(open ? "Collapse menu" : "Expand menu"));
    }

    private void ShowFromTray() => DispatcherQueue.TryEnqueue(() => ShowMain(true));

    // ---------- show / hide with animation, hotkey, floating icon ----------
    private void ApplyShell()
    {
        var s = _h.Settings.Current;

        var wanted = s.HotkeyEnabled ? s.Hotkey : null;
        if (wanted != _hotkeyRegistered)
        {
            _hotkey.Unregister(); _hotkeyRegistered = null;
            if (wanted is not null)
            {
                if (_hotkey.Register(wanted)) _hotkeyRegistered = wanted;
                else ViewModel.StatusText = Loc.F("Hotkey {0} is already used by another program.", wanted);
            }
        }

        if (s.FloatingIconEnabled)
        {
            if (_floating is null)
            {
                try
                {
                    _floating = new FloatingIcon(s, (x, y) => { s.FloatingX = x; s.FloatingY = y; _h.Settings.Save(); });
                    _floating.Clicked += () => DispatcherQueue.TryEnqueue(() => { if (AppWindow.IsVisible && Platform.IsForeground(this)) HideMain(true); else ShowMain(true); });
                    _floating.ScanRequested += () => DispatcherQueue.TryEnqueue(() => _ = ViewModel.ScanNowAsync());
                    _floating.HideIconRequested += () => DispatcherQueue.TryEnqueue(() => { s.FloatingIconEnabled = false; _h.Settings.Save(); _settingsVm.Load(); ApplyShell(); });
                    _floating.ExitRequested += () => DispatcherQueue.TryEnqueue(() => { _exiting = true; Close(); });
                }
                catch (Exception ex)
                {
                    Log.Error("Could not create the floating desktop icon", ex);
                    ViewModel.StatusText = Loc.T("The floating desktop icon could not be created (see Logs).");
                    _floating = null;
                }
            }
            if (_floating is not null)
            {
                _floating.ApplySettings();
                if (AppWindow.IsVisible && s.FloatingHideWhenOpen) _floating.HideIcon();
                else if (!_floating.IsShown) _floating.ShowIcon();      // never activates, so it cannot steal focus from a game or stream
            }
        }
        else if (_floating is not null) { var f = _floating; _floating = null; f.Close(); }
        _tray.IconVisible = _floating?.IsShown == true;
    }

    private Windows.Graphics.RectInt32 DefaultRect()
    {
        var wa = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min(1560, (int)(wa.Width * 0.92)), h = Math.Min(940, (int)(wa.Height * 0.92));
        return new(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h);
    }

    private void RememberNormal()
    {
        if (AppWindow.IsVisible && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored } && AppWindow.Position.X > -30000)
            _normal = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
    }

    private Windows.Graphics.RectInt32 TargetRect()
    {
        if (_normal.Width > 300 && _normal.Height > 200 &&
            Microsoft.UI.Windowing.DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(_normal.X + _normal.Width / 2, _normal.Y + _normal.Height / 2), Microsoft.UI.Windowing.DisplayAreaFallback.None) is not null)
            return _normal;
        return DefaultRect();
    }

    private void ToggleMain()
    {
        if (_animating) return;
        if (AppWindow.IsVisible && Platform.IsForeground(this)) HideMain(true); else ShowMain(true);
    }

    private void ShowMain(bool animate)
    {
        if (_animating) return;
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        if (presenter?.State == OverlappedPresenterState.Minimized) presenter.Restore();
        if (AppWindow.IsVisible) { Activate(); Platform.Foreground(this); return; }       // already open: just bring it to the front

        var s = _h.Settings.Current;
        SoundService.Play("open", s.SoundsEnabled);
        var maximized = presenter?.State == OverlappedPresenterState.Maximized;
        if (!maximized) AppWindow.MoveAndResize(TargetRect());                              // size/position are restored while the window is still hidden
        if (animate && s.AnimationsEnabled)
        {
            // The window never changes size while animating (that caused the lag and a stray small frame): it appears at full size, fully transparent,
            // then fades in while the content eases in from slightly smaller / lower.
            Platform.SetWindowAlpha(this, 0);
            RootXf.ScaleX = RootXf.ScaleY = 0.95; RootXf.TranslateY = 22;
            AppWindow.Show(); Activate(); Platform.Foreground(this);
            AnimateWindow(opening: true, done: null);
        }
        else { AppWindow.Show(); Activate(); Platform.Foreground(this); }
        if (s.FloatingHideWhenOpen) _floating?.HideIcon();
        _tray.IconVisible = _floating?.IsShown == true;
    }

    private void HideMain(bool animate)
    {
        if (_animating || !AppWindow.IsVisible) return;
        var s = _h.Settings.Current;
        RememberNormal();
        SoundService.Play("close", s.SoundsEnabled);
        if (s.FloatingIconEnabled) _floating?.ShowIcon();
        _tray.IconVisible = _floating?.IsShown == true;

        var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        if (!maximized && animate && s.AnimationsEnabled)
            AnimateWindow(opening: false, done: () =>
            {
                AppWindow.Hide();
                Platform.SetWindowAlpha(this, 255);                                          // reset for the next open
                RootXf.ScaleX = RootXf.ScaleY = 1; RootXf.TranslateY = 0;
            });
        else AppWindow.Hide();
    }

    private void AnimateWindow(bool opening, Action? done)
    {
        _animating = true;
        var ms = (opening ? 300.0 : 200.0) * 100 / Math.Clamp(_h.Settings.Current.AnimationSpeedPercent, 25, 400);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(15);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            var k = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / ms);
            var e = opening ? 1 - Math.Pow(1 - k, 3) : k * k * k;                            // ease-out when opening, ease-in when closing
            var p = opening ? e : 1 - e;                                                     // 0 = hidden ... 1 = fully shown
            Platform.SetWindowAlpha(this, (byte)Math.Clamp(p * 255, 0, 255));
            RootXf.ScaleX = RootXf.ScaleY = 0.95 + 0.05 * p;
            RootXf.TranslateY = (1 - p) * 22;
            if (k < 1) return;
            timer.Stop();
            _animating = false;
            done?.Invoke();
            if (opening) { Platform.SetWindowAlpha(this, 255); RememberNormal(); }
        };
        timer.Start();
    }

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

    // ---------- startup animation ----------
    private Storyboard? _introSb, _introSpin;

    private void PlayIntro()
    {
        if (!_h.Settings.Current.IntroAnimation) return;
        IntroOverlay.Visibility = Visibility.Visible; IntroOverlay.Opacity = 1;
        var main = new Storyboard(); var spin = new Storyboard();
        void Add(Storyboard sb, DependencyObject t, string prop, double from, double to, double begin, double seconds, EasingFunctionBase? ease = null, bool dependent = false, bool forever = false)
        {
            var a = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = from, To = to, BeginTime = TimeSpan.FromSeconds(begin), Duration = new Duration(TimeSpan.FromSeconds(seconds)),
                EasingFunction = ease, EnableDependentAnimation = dependent
            };
            if (forever) a.RepeatBehavior = RepeatBehavior.Forever;
            Storyboard.SetTarget(a, t); Storyboard.SetTargetProperty(a, prop); sb.Children.Add(a);
        }
        var outCubic = new CubicEase { EasingMode = EasingMode.EaseOut };
        var back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        Add(main, IntroOrb, "Opacity", 0, 1, 0.05, 0.6, outCubic);
        Add(main, IntroOrbXf, "ScaleX", 0.5, 1, 0.05, 0.9, back); Add(main, IntroOrbXf, "ScaleY", 0.5, 1, 0.05, 0.9, back);
        Add(main, IntroTitle, "Opacity", 0, 1, 0.55, 0.7, outCubic);
        Add(main, IntroTitleXf, "TranslateY", 14, 0, 0.55, 0.7, outCubic);
        Add(main, IntroTitle, "CharacterSpacing", 700, 260, 0.55, 1.5, outCubic, dependent: true);
        Add(main, IntroSub, "Opacity", 0, 0.85, 1.1, 0.6, outCubic);
        Add(main, IntroBar, "Width", 0, 260, 0.3, 1.9, new CubicEase { EasingMode = EasingMode.EaseInOut }, dependent: true);
        Add(main, IntroOverlay, "Opacity", 1, 0, 2.45, 0.55, new CubicEase { EasingMode = EasingMode.EaseIn });      // fade out, revealing the app
        Add(spin, IntroRing1Xf, "Angle", 0, 360, 0, 7, null, false, true);
        Add(spin, IntroRing2Xf, "Angle", 360, 0, 0, 11, null, false, true);
        main.Completed += (_, _) => FinishIntro();
        _introSb = main; _introSpin = spin;
        spin.Begin(); main.Begin();
    }

    private void FinishIntro()
    {
        _introSb?.Stop(); _introSpin?.Stop(); _introSb = _introSpin = null;
        IntroOverlay.Visibility = Visibility.Collapsed;
    }

    private void Intro_Tapped(object sender, TappedRoutedEventArgs e) => FinishIntro();           // click to skip

    private void ApplyBackgroundStretch()
    {
        var fit = _h.Settings.Current.ArtFit != "fill";
        foreach (var img in new[] { BgA, BgB })
        {
            img.Stretch = fit ? Stretch.Uniform : Stretch.UniformToFill;           // fit = whole image, aspect ratio kept (tinted gradient fills the rest)
            img.VerticalAlignment = fit ? VerticalAlignment.Top : VerticalAlignment.Center;
        }
    }

    private void UpdateArt()
    {
        var sel = ViewModel.Selected;
        var art = sel is not null && sel.BackgroundImage is null ? sel.ArtworkImage : null;
        if (sel is null || art is null)
        {
            BgArtHost.Visibility = Visibility.Collapsed; BgArt.Source = null;
            if (sel is not null && _h.Settings.Current.ArtFit != "fill") _ = ApplyTintAsync(sel); else ResetTint();
            return;
        }
        BgArt.Source = art;
        if (art is Microsoft.UI.Xaml.Media.Imaging.BitmapImage bi)
        {
            if (bi.PixelWidth > 0) FitArt(bi);
            else { void Opened(object s, RoutedEventArgs e) { bi.ImageOpened -= Opened; FitArt(bi); } bi.ImageOpened += Opened; }
        }
        BgArtHost.Visibility = Visibility.Visible;
        _ = ApplyTintAsync(sel);
    }

    /// <summary>Shows the image at (at most) its own resolution: small icons stay crisp instead of being blown up and blurry.</summary>
    private void FitArt(Microsoft.UI.Xaml.Media.Imaging.BitmapImage bi)
    {
        var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        double w = bi.PixelWidth / scale, h = bi.PixelHeight / scale;
        var longest = Math.Max(w, h);
        var target = Math.Clamp(longest * 4, 240, 340);            // icons are enlarged to fill the tile (aspect ratio kept); big covers are reduced
        var k = target / longest;
        BgArt.Width = Math.Max(48, w * k); BgArt.Height = Math.Max(48, h * k);
    }

    private static Windows.UI.Color Mix(Windows.UI.Color a, Windows.UI.Color b, double t) =>
        Windows.UI.Color.FromArgb(255, (byte)(a.R * t + b.R * (1 - t)), (byte)(a.G * t + b.G * (1 - t)), (byte)(a.B * t + b.B * (1 - t)));

    /// <summary>Tints the window background with the artwork's dominant colour so icon/cover art sits in a matching scene.</summary>
    private async Task ApplyTintAsync(GameViewModel sel)
    {
        var path = sel.Model.BackgroundPath ?? sel.Model.CoverPath ?? sel.Model.IconPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) { ResetTint(); return; }
        if (!_tints.TryGetValue(path, out var c))
        {
            var r = await Platform.AverageColorAsync(path);
            if (r is null) { ResetTint(); return; }
            c = r.Value; _tints[path] = c;
        }
        if (!ReferenceEquals(ViewModel.Selected, sel)) return;   // selection changed while decoding
        var bg = Windows.UI.Color.FromArgb(255, 0x0B, 0x0D, 0x12);
        var grad = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0.1, 0), EndPoint = new Windows.Foundation.Point(0.9, 1) };
        grad.GradientStops.Add(new GradientStop { Color = Mix(c, bg, 0.50), Offset = 0 });
        grad.GradientStops.Add(new GradientStop { Color = Mix(c, bg, 0.22), Offset = 0.55 });
        grad.GradientStops.Add(new GradientStop { Color = bg, Offset = 1 });
        FallbackGradient.Fill = grad;
        BgArtHost.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, c.R, c.G, c.B));
    }

    private void ResetTint()
    {
        if (_defaultFallback is not null) FallbackGradient.Fill = _defaultFallback;
        BgArtHost.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 255, 255, 255));
    }

    private long _lastInfoId = -1;
    private Storyboard? _infoSb;

    private void AnimateInfo()
    {
        if (!_h.Settings.Current.AnimationsEnabled) return;
        _infoSb?.Stop();
        InfoPanel.Opacity = 0; InfoXf.TranslateY = 16;
        var sb = new Storyboard();
        void Add(DependencyObject t, string prop, double to)
        {
            var a = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = to, Duration = new Duration(TimeSpan.FromMilliseconds(280)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(a, t); Storyboard.SetTargetProperty(a, prop); sb.Children.Add(a);
        }
        Add(InfoPanel, "Opacity", 1); Add(InfoXf, "TranslateY", 0);
        sb.Completed += (_, _) => { InfoPanel.Opacity = 1; InfoXf.TranslateY = 0; sb.Stop(); };
        _infoSb = sb; sb.Begin();
    }

    private void OnSelectionChanged()
    {
        if (ViewModel.Selected is { } cur && cur.Id != _lastInfoId) { _lastInfoId = cur.Id; AnimateInfo(); }
        UpdateBackground();
        UpdateArt();
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
