using System.Runtime.InteropServices;
using GameIndex.Core.Settings;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.UI;

namespace GameIndex.Services;

/// <summary>
/// Small round, always-on-top, animated desktop icon. Drag it anywhere (position is remembered); a click opens GameIndex,
/// right-click gives a menu. Built entirely in code; clipped to a circle with a window region.
/// </summary>
public sealed class FloatingIconWindow : Window
{
    private const double DipSize = 76;

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
    private readonly CompositeTransform _hover = new() { CenterX = DipSize / 2, CenterY = DipSize / 2 };
    private bool _pressed, _dragging;
    private POINT _startCursor;
    private PointInt32 _startPos;
    private int _px;

    public bool IsShown { get; private set; }
    public event Action? Clicked, ScanRequested, HideIconRequested, ExitRequested;

    public RectInt32 Bounds => new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

    public FloatingIconWindow(AppSettings settings, Action<int, int> savePosition)
    {
        _settings = settings; _savePosition = savePosition;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Title = "GameIndex";

        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true; presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;           // no taskbar button for the icon itself

        _root = BuildUi();
        Content = _root;

        var scale = Math.Max(1.0, GetDpiForWindow(_hwnd) / 96.0);
        _px = (int)Math.Round(DipSize * scale);
        AppWindow.Resize(new SizeInt32(_px, _px));
        Place();
        if (GetWindowRect(_hwnd, out var r)) SetWindowRgn(_hwnd, CreateEllipticRgn(0, 0, r.Right - r.Left + 1, r.Bottom - r.Top + 1), true);   // circular window
    }

    // ---------- placement ----------
    private void Place()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var x = _settings.FloatingX; var y = _settings.FloatingY;
        var valid = x != int.MinValue && y != int.MinValue &&
                    DisplayArea.GetFromPoint(new PointInt32(x + _px / 2, y + _px / 2), DisplayAreaFallback.None) is not null;
        if (!valid) { x = area.X + area.Width - _px - 48; y = area.Y + area.Height - _px - 140; }
        AppWindow.Move(new PointInt32(x, y));
    }

    public void ResetPosition()
    {
        _settings.FloatingX = int.MinValue; _settings.FloatingY = int.MinValue;
        Place();
    }

    public void ShowIcon() { if (IsShown) return; IsShown = true; AppWindow.Show(); }
    public void HideIcon() { if (!IsShown) return; IsShown = false; AppWindow.Hide(); }

    // ---------- visuals ----------
    private Grid BuildUi()
    {
        var accent = (Brush)Application.Current.Resources["AccentBrush"];
        var accent2 = (Brush)Application.Current.Resources["Accent2Brush"];

        var root = new Grid { Width = DipSize, Height = DipSize, Background = new SolidColorBrush(Color.FromArgb(255, 15, 19, 27)), RenderTransform = _hover };

        // glassy inner disc
        var disc = new Ellipse { Margin = new Thickness(5), Fill = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops = { new GradientStop { Color = Color.FromArgb(255, 36, 43, 62), Offset = 0 }, new GradientStop { Color = Color.FromArgb(255, 17, 21, 29), Offset = 1 } }
        } };

        // slowly rotating dashed ring
        var rot = new RotateTransform { CenterX = DipSize / 2, CenterY = DipSize / 2 };
        var ring = new Ellipse { Margin = new Thickness(2), Stroke = accent, StrokeThickness = 2.5, StrokeDashArray = new DoubleCollection { 5, 3.2 }, RenderTransform = rot, Opacity = 0.95 };
        var ring2 = new Ellipse { Margin = new Thickness(9), Stroke = accent2, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 2, 5 }, RenderTransform = new RotateTransform { CenterX = DipSize / 2 - 7, CenterY = DipSize / 2 - 7 }, Opacity = 0.7 };

        // glyph that bobs and breathes
        var glyphT = new CompositeTransform { CenterX = 15, CenterY = 15 };
        var glyph = new FontIcon { Glyph = "\uE7FC", FontSize = 30, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RenderTransform = glyphT };

        root.Children.Add(disc); root.Children.Add(ring); root.Children.Add(ring2); root.Children.Add(glyph);

        var sb = new Storyboard();
        void Loop(DependencyObject target, string prop, double from, double to, double seconds, bool autoReverse, EasingFunctionBase? ease = null)
        {
            var a = new DoubleAnimation { From = from, To = to, Duration = new Duration(TimeSpan.FromSeconds(seconds)), RepeatBehavior = RepeatBehavior.Forever, AutoReverse = autoReverse, EasingFunction = ease };
            Storyboard.SetTarget(a, target); Storyboard.SetTargetProperty(a, prop); sb.Children.Add(a);
        }
        Loop(rot, "Angle", 0, 360, 14, false);
        Loop((RotateTransform)ring2.RenderTransform, "Angle", 360, 0, 22, false);
        Loop(glyphT, "TranslateY", -2.5, 2.5, 2.2, true, new SineEase { EasingMode = EasingMode.EaseInOut });
        Loop(glyphT, "ScaleX", 1.0, 1.1, 2.2, true, new SineEase { EasingMode = EasingMode.EaseInOut });
        Loop(glyphT, "ScaleY", 1.0, 1.1, 2.2, true, new SineEase { EasingMode = EasingMode.EaseInOut });
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
            Storyboard.SetTarget(a, _hover); Storyboard.SetTargetProperty(a, prop); sb.Children.Add(a);
        }
        sb.Begin();
    }
}
