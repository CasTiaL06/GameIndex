using System.Runtime.InteropServices;
using GameIndex.Core;
using GameIndex.Core.Settings;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.UI;

namespace GameIndex.Services;

/// <summary>
/// Round, always-on-top, animated desktop icon: drag it anywhere (position is remembered), click to show/hide GameIndex, right-click for a menu.
/// Windows refuses top-level windows smaller than ~136 px, so the window is 152 DIP and the visible shape is cut out with a circular
/// window region (clicks outside the circle fall through to the desktop). Built in code; the first ShowIcon() must Activate() it
/// or XAML never renders.
/// </summary>
public sealed class FloatingIconWindow : Window
{
    private const double WindowDip = 152, RegionDip = 124, OrbDip = 88;

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateEllipticRgn(int l, int t, int r, int b);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private readonly IntPtr _hwnd;
    private readonly AppSettings _settings;
    private readonly Action<int, int> _savePosition;
    private readonly Grid _root;
    private readonly CompositeTransform _orbScale = new();
    private bool _pressed, _dragging, _activatedOnce;
    private POINT _startCursor;
    private PointInt32 _startPos;
    private int _px;
    private double _scale = 1;

    public bool IsShown { get; private set; }
    public event Action? Clicked, ScanRequested, HideIconRequested, ExitRequested;

    /// <summary>Window bounds; its centre is the orb's centre.</summary>
    public RectInt32 Bounds => new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

    public FloatingIconWindow(AppSettings settings, Action<int, int> savePosition)
    {
        _settings = settings; _savePosition = savePosition;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Title = "GameIndex";

        try
        {
            var presenter = OverlappedPresenter.Create();
            presenter.IsAlwaysOnTop = true; presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
            AppWindow.SetPresenter(presenter);
            AppWindow.IsShownInSwitchers = false;           // no taskbar button / Alt-Tab entry for the icon
        }
        catch (Exception ex) { Log.Warn("Floating icon presenter setup failed: " + ex.Message); }

        _root = BuildUi();
        Content = _root;

        _scale = Math.Max(1.0, GetDpiForWindow(_hwnd) / 96.0);
        _px = (int)Math.Round(WindowDip * _scale);
        AppWindow.Resize(new SizeInt32(_px, _px));
        Place();
        ApplyCircle();
        Log.Info($"Floating icon created ({_px}px at {AppWindow.Position.X},{AppWindow.Position.Y})");
    }

    private void ApplyCircle()
    {
        if (!GetWindowRect(_hwnd, out var r)) return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var d = Math.Min((int)Math.Round(RegionDip * _scale), Math.Min(w, h));
        int ox = (w - d) / 2, oy = (h - d) / 2;
        SetWindowRgn(_hwnd, CreateEllipticRgn(ox, oy, ox + d + 1, oy + d + 1), true);
    }

    // ---------- placement ----------
    private void Place()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var x = _settings.FloatingX; var y = _settings.FloatingY;
        var valid = x != int.MinValue && y != int.MinValue &&
                    DisplayArea.GetFromPoint(new PointInt32(x + _px / 2, y + _px / 2), DisplayAreaFallback.None) is not null;
        if (!valid) { x = area.X + area.Width - _px - 24; y = area.Y + area.Height - _px - 90; }   // bottom-right corner by default
        AppWindow.Move(new PointInt32(x, y));
    }

    public void ResetPosition()
    {
        _settings.FloatingX = int.MinValue; _settings.FloatingY = int.MinValue;
        Place();
    }

    public void ShowIcon()
    {
        if (IsShown) return;
        IsShown = true;
        try
        {
            if (!_activatedOnce) { _activatedOnce = true; Activate(); }   // XAML only renders after the first Activate()
            AppWindow.Show();
        }
        catch (Exception ex) { Log.Error("Floating icon could not be shown", ex); }
    }

    public void HideIcon()
    {
        if (!IsShown) return;
        IsShown = false;
        try { AppWindow.Hide(); } catch { }
    }

    // ---------- visuals ----------
    private Grid BuildUi()
    {
        var accent = (Brush)Application.Current.Resources["AccentBrush"];
        var accent2 = (Brush)Application.Current.Resources["Accent2Brush"];
        var half = new Windows.Foundation.Point(0.5, 0.5);

        var root = new Grid { Width = WindowDip, Height = WindowDip, Background = new SolidColorBrush(Color.FromArgb(255, 13, 16, 24)) };

        // two expanding "ripple" rings behind the orb
        Ellipse Ripple() => new()
        {
            Width = OrbDip, Height = OrbDip, Stroke = accent, StrokeThickness = 2, Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = half, RenderTransform = new CompositeTransform()
        };
        var ripple1 = Ripple(); var ripple2 = Ripple();

        // the orb: glass disc, rotating dashed rings, breathing glyph
        var orb = new Grid { Width = OrbDip, Height = OrbDip, RenderTransformOrigin = half, RenderTransform = _orbScale, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        orb.Children.Add(new Ellipse
        {
            Fill = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops = { new GradientStop { Color = Color.FromArgb(255, 40, 48, 70), Offset = 0 }, new GradientStop { Color = Color.FromArgb(255, 17, 21, 30), Offset = 1 } }
            },
            Stroke = accent, StrokeThickness = 1.5
        });
        var ring1 = new Ellipse { Margin = new Thickness(5), Stroke = accent, StrokeThickness = 2.5, StrokeDashArray = new DoubleCollection { 5, 3.2 }, RenderTransformOrigin = half, RenderTransform = new RotateTransform() };
        var ring2 = new Ellipse { Margin = new Thickness(13), Stroke = accent2, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 2, 5 }, RenderTransformOrigin = half, RenderTransform = new RotateTransform(), Opacity = 0.75 };
        var glyphXf = new CompositeTransform();
        var glyph = new FontIcon { Glyph = "\uE7FC", FontSize = 34, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = half, RenderTransform = glyphXf };
        orb.Children.Add(ring1); orb.Children.Add(ring2); orb.Children.Add(glyph);

        root.Children.Add(ripple1); root.Children.Add(ripple2); root.Children.Add(orb);

        // animations (all run forever)
        var sb = new Storyboard();
        void Loop(DependencyObject target, string prop, double from, double to, double seconds, bool autoReverse = false, double beginSeconds = 0, EasingFunctionBase? ease = null)
        {
            var a = new DoubleAnimation
            {
                From = from, To = to, Duration = new Duration(TimeSpan.FromSeconds(seconds)), RepeatBehavior = RepeatBehavior.Forever,
                AutoReverse = autoReverse, EasingFunction = ease, BeginTime = TimeSpan.FromSeconds(beginSeconds)
            };
            Storyboard.SetTarget(a, target); Storyboard.SetTargetProperty(a, prop); sb.Children.Add(a);
        }
        var soft = new SineEase { EasingMode = EasingMode.EaseInOut };
        Loop(ring1.RenderTransform, "Angle", 0, 360, 14);
        Loop(ring2.RenderTransform, "Angle", 360, 0, 22);
        Loop(glyphXf, "TranslateY", -2.5, 2.5, 2.2, true, 0, soft);
        Loop(glyphXf, "ScaleX", 1.0, 1.12, 2.2, true, 0, soft);
        Loop(glyphXf, "ScaleY", 1.0, 1.12, 2.2, true, 0, soft);
        foreach (var (rip, delay) in new[] { (ripple1, 0.0), (ripple2, 1.3) })
        {
            var xf = rip.RenderTransform;
            Loop(xf, "ScaleX", 1.0, 1.38, 2.6, false, delay);
            Loop(xf, "ScaleY", 1.0, 1.38, 2.6, false, delay);
            Loop(rip, "Opacity", 0.65, 0.0, 2.6, false, delay);
        }
        root.Loaded += (_, _) => sb.Begin();

        // drag + click
        root.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(root).Properties.IsLeftButtonPressed) return;
            _pressed = true; _dragging = false;
            GetCursorPos(out _startCursor); _startPos = AppWindow.Position;
            root.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        root.PointerMoved += (_, _) =>
        {
            if (!_pressed) return;
            GetCursorPos(out var c);
            int dx = c.X - _startCursor.X, dy = c.Y - _startCursor.Y;
            if (!_dragging && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4)) _dragging = true;
            if (_dragging) AppWindow.Move(new PointInt32(_startPos.X + dx, _startPos.Y + dy));
        };
        root.PointerReleased += (_, e) =>
        {
            if (!_pressed) return;
            _pressed = false;
            root.ReleasePointerCapture(e.Pointer);
            if (_dragging) { _dragging = false; _savePosition(AppWindow.Position.X, AppWindow.Position.Y); }
            else Clicked?.Invoke();
        };
        root.PointerCanceled += (_, _) => { _pressed = false; _dragging = false; };
        root.PointerEntered += (_, _) => Hover(1.12);
        root.PointerExited += (_, _) => Hover(1.0);
        ToolTipService.SetToolTip(root, "GameIndex");

        // right-click menu
        var menu = new MenuFlyout();
        void Item(string text, Action act) { var i = new MenuFlyoutItem { Text = Loc.T(text) }; i.Click += (_, _) => act(); menu.Items.Add(i); }
        Item("Open GameIndex", () => Clicked?.Invoke());
        Item("Scan for games", () => ScanRequested?.Invoke());
        Item("Hide desktop icon", () => HideIconRequested?.Invoke());
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("Exit GameIndex", () => ExitRequested?.Invoke());
        root.ContextFlyout = menu;
        return root;
    }

    private void Hover(double scale)
    {
        var sb = new Storyboard();
        foreach (var prop in new[] { "ScaleX", "ScaleY" })
        {
            var a = new DoubleAnimation { To = scale, Duration = new Duration(TimeSpan.FromMilliseconds(160)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(a, _orbScale); Storyboard.SetTargetProperty(a, prop); sb.Children.Add(a);
        }
        sb.Begin();
    }
}
