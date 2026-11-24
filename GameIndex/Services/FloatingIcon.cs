using System.Diagnostics;
using System.Runtime.InteropServices;
using GameIndex.Core;
using GameIndex.Core.Settings;
using Windows.Graphics;

namespace GameIndex.Services;

/// <summary>
/// The floating desktop icon as a native layered window with per-pixel alpha (UpdateLayeredWindow + GDI+):
/// truly transparent background (no rectangle), transparent pixels let clicks fall through to the desktop, never steals focus.
/// Animated at ~30 fps: breathing/bobbing body, rotating ring layer, expanding ripples. Drag to move, click = Clicked, right-click = menu.
/// Modes: "desktop" = sits at the bottom of the z-order (visible on the desktop only, hidden behind other apps),
/// "always" = topmost (stays on screen over other apps). Must be created and used on the UI thread.
/// </summary>
public sealed class FloatingIcon : IDisposable
{
    // ---------------- Win32 ----------------
    private delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct GdiplusStartupInput { public uint GdiplusVersion; public IntPtr DebugEventCallback; public int SuppressBackgroundThread, SuppressExternalCodecs; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint ex, string cls, string? title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, IntPtr pptDst, ref SIZE size, IntPtr src, ref POINT pptSrc, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr h, UIntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr h, UIntPtr id);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr m, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr m, uint flags, int x, int y, int reserved, IntPtr h, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr m);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT t);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfoW(uint action, uint param, ref RECT r, uint winIni);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdiplus.dll")] private static extern int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, IntPtr output);
    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)] private static extern int GdipCreateBitmapFromFile(string file, out IntPtr bitmap);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateBitmapFromScan0(int w, int h, int stride, int format, IntPtr scan0, out IntPtr bitmap);
    [DllImport("gdiplus.dll")] private static extern int GdipGetImageGraphicsContext(IntPtr image, out IntPtr graphics);
    [DllImport("gdiplus.dll")] private static extern int GdipDisposeImage(IntPtr image);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteGraphics(IntPtr g);
    [DllImport("gdiplus.dll")] private static extern int GdipGraphicsClear(IntPtr g, uint argb);
    [DllImport("gdiplus.dll")] private static extern int GdipSetSmoothingMode(IntPtr g, int mode);
    [DllImport("gdiplus.dll")] private static extern int GdipSetInterpolationMode(IntPtr g, int mode);
    [DllImport("gdiplus.dll")] private static extern int GdipSetPixelOffsetMode(IntPtr g, int mode);
    [DllImport("gdiplus.dll")] private static extern int GdipResetWorldTransform(IntPtr g);
    [DllImport("gdiplus.dll")] private static extern int GdipTranslateWorldTransform(IntPtr g, float dx, float dy, int order);
    [DllImport("gdiplus.dll")] private static extern int GdipRotateWorldTransform(IntPtr g, float angle, int order);
    [DllImport("gdiplus.dll")] private static extern int GdipScaleWorldTransform(IntPtr g, float sx, float sy, int order);
    [DllImport("gdiplus.dll")] private static extern int GdipDrawImageRect(IntPtr g, IntPtr image, float x, float y, float w, float h);
    [DllImport("gdiplus.dll")] private static extern int GdipCreatePen1(uint argb, float width, int unit, out IntPtr pen);
    [DllImport("gdiplus.dll")] private static extern int GdipDeletePen(IntPtr pen);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateSolidFill(uint argb, out IntPtr brush);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteBrush(IntPtr brush);
    [DllImport("gdiplus.dll")] private static extern int GdipFillEllipse(IntPtr g, IntPtr brush, float x, float y, float w, float h);
    [DllImport("gdiplus.dll")] private static extern int GdipDrawEllipse(IntPtr g, IntPtr pen, float x, float y, float w, float h);

    private const uint WS_POPUP = 0x80000000, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    private const uint WM_TIMER = 0x113, WM_MOUSEACTIVATE = 0x21, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, WM_MOUSELEAVE = 0x2A3;
    private const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10;
    private const int PixelFormat32bppPARGB = 0xE200B;

    private static readonly WndProc Proc = StaticProc;           // kept alive for the lifetime of the process
    private static readonly Dictionary<IntPtr, FloatingIcon> Instances = new();
    private static bool _classRegistered;
    private static IntPtr _gdiToken;
    private const string ClassName = "GameIndex.FloatingIcon";

    private static IntPtr StaticProc(IntPtr h, uint m, IntPtr w, IntPtr l) =>
        Instances.TryGetValue(h, out var inst) ? inst.Handle(h, m, w, l) : DefWindowProcW(h, m, w, l);

    // ---------------- instance ----------------
    private readonly AppSettings _settings;
    private readonly Action<int, int> _savePosition;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IntPtr _hwnd, _memDc, _dib, _bits, _bmp, _g, _base, _ring;
    private string? _styleId;
    private int _winPx, _iconPx, _opacity = 92, _tick;
    private double _scale = 1;
    private bool _hover, _tracking, _pressed, _dragging;
    private float _hoverV;
    private POINT _startCursor, _startPos;

    public bool IsShown { get; private set; }
    public event Action? Clicked, ScanRequested, HideIconRequested, ExitRequested;

    /// <summary>Window bounds in screen pixels; the centre is the orb's centre.</summary>
    public RectInt32 Bounds => GetWindowRect(_hwnd, out var r) ? new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : new(0, 0, _winPx, _winPx);

    public FloatingIcon(AppSettings settings, Action<int, int> savePosition)
    {
        _settings = settings; _savePosition = savePosition;
        EnsureGdiplus();
        RegisterClass();
        _hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, ClassName, "GameIndex", WS_POPUP, 0, 0, 16, 16, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
        Instances[_hwnd] = this;
        ApplySettings(first: true);
        Log.Info($"Floating icon created ({_winPx}px, mode {_settings.FloatingMode}, style {_settings.FloatingStyle})");
    }

    private static void EnsureGdiplus()
    {
        if (_gdiToken != IntPtr.Zero) return;
        var input = new GdiplusStartupInput { GdiplusVersion = 1 };
        if (GdiplusStartup(out _gdiToken, ref input, IntPtr.Zero) != 0) throw new InvalidOperationException("GDI+ could not be started");
    }

    private static void RegisterClass()
    {
        if (_classRegistered) return;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc), hInstance = GetModuleHandleW(null),
            hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)32649 /* IDC_HAND */), lpszClassName = ClassName
        };
        if (RegisterClassExW(ref wc) == 0) throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
        _classRegistered = true;
    }

    // ---------------- settings / layout ----------------
    /// <summary>Re-reads style, size, opacity and visibility mode from the settings (cheap; safe to call often).</summary>
    public void ApplySettings(bool first = false)
    {
        _scale = Math.Max(1.0, GetDpiForWindow(_hwnd) / 96.0);
        if (_styleId != _settings.FloatingStyle) LoadStyle(_settings.FloatingStyle);
        _iconPx = (int)Math.Round(Math.Clamp(_settings.FloatingSize, 40, 220) * _scale);
        var win = (int)Math.Round(_iconPx * 1.75);              // room for the ripples
        _opacity = Math.Clamp(_settings.FloatingOpacity, 20, 100);
        if (win != _winPx)
        {
            var center = first ? default : CenterPoint();
            _winPx = win;
            Reallocate();
            if (first) Place(); else SetWindowPos(_hwnd, IntPtr.Zero, center.X - win / 2, center.Y - win / 2, win, win, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        ApplyZOrder();
        Render();
    }

    private POINT CenterPoint() { GetWindowRect(_hwnd, out var r); return new POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 }; }

    private void Place()
    {
        int x = _settings.FloatingX, y = _settings.FloatingY;
        var valid = x != int.MinValue && y != int.MinValue &&
                    MonitorFromPoint(new POINT { X = x + _winPx / 2, Y = y + _winPx / 2 }, 0 /* MONITOR_DEFAULTTONULL */) != IntPtr.Zero;
        if (!valid)
        {
            var wa = new RECT();
            SystemParametersInfoW(0x30 /* SPI_GETWORKAREA */, 0, ref wa, 0);
            x = wa.Right - _winPx - (int)(30 * _scale); y = wa.Bottom - _winPx - (int)(70 * _scale);   // bottom-right of the primary screen
        }
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, _winPx, _winPx, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public void ResetPosition()
    {
        _settings.FloatingX = int.MinValue; _settings.FloatingY = int.MinValue;
        Place();
    }

    private void ApplyZOrder()
    {
        var topmost = _settings.FloatingMode == "always";
        SetWindowPos(_hwnd, topmost ? new IntPtr(-1) : new IntPtr(1) /* HWND_BOTTOM: above the desktop, below every app */, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void LoadStyle(string id)
    {
        if (_base != IntPtr.Zero) GdipDisposeImage(_base);
        if (_ring != IntPtr.Zero) GdipDisposeImage(_ring);
        _base = _ring = IntPtr.Zero;
        _styleId = id;
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "FloatingIcons");
        if (GdipCreateBitmapFromFile(Path.Combine(dir, id + "_base.png"), out var b) == 0) _base = b;
        if (GdipCreateBitmapFromFile(Path.Combine(dir, id + "_ring.png"), out var r) == 0) _ring = r;
        if (_base == IntPtr.Zero && id != "g_neon") { Log.Warn($"Floating icon style '{id}' not found in {dir}; using g_neon"); LoadStyle("g_neon"); return; }
        if (_base == IntPtr.Zero) Log.Warn($"Floating icon style '{id}' not found in {dir}");
    }

    private void Reallocate()
    {
        FreeSurface();
        var bmi = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = _winPx, biHeight = -_winPx, biPlanes = 1, biBitCount = 32 };   // top-down
        _memDc = CreateCompatibleDC(IntPtr.Zero);
        _dib = CreateDIBSection(_memDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        SelectObject(_memDc, _dib);
        GdipCreateBitmapFromScan0(_winPx, _winPx, _winPx * 4, PixelFormat32bppPARGB, _bits, out _bmp);   // GDI+ draws premultiplied alpha straight into the DIB
        GdipGetImageGraphicsContext(_bmp, out _g);
        GdipSetSmoothingMode(_g, 4);        // anti-alias
        GdipSetInterpolationMode(_g, 7);    // high quality bicubic
        GdipSetPixelOffsetMode(_g, 2);      // high quality
    }

    private void FreeSurface()
    {
        if (_g != IntPtr.Zero) { GdipDeleteGraphics(_g); _g = IntPtr.Zero; }
        if (_bmp != IntPtr.Zero) { GdipDisposeImage(_bmp); _bmp = IntPtr.Zero; }
        if (_dib != IntPtr.Zero) { DeleteObject(_dib); _dib = IntPtr.Zero; }
        if (_memDc != IntPtr.Zero) { DeleteDC(_memDc); _memDc = IntPtr.Zero; }
    }

    // ---------------- show / hide ----------------
    public void ShowIcon()
    {
        if (IsShown) return;
        IsShown = true;
        ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE */);
        ApplyZOrder();
        SetTimer(_hwnd, (UIntPtr)1, 33, IntPtr.Zero);
        Render();
    }

    public void HideIcon()
    {
        if (!IsShown) return;
        IsShown = false;
        KillTimer(_hwnd, (UIntPtr)1);
        ShowWindow(_hwnd, 0);
    }

    // ---------------- rendering ----------------
    private static uint Argb(int a, int r, int g, int b) => (uint)((a << 24) | (r << 16) | (g << 8) | b);

    private (int R, int G, int B) Accent()
    {
        var c = ThemeService.ParseColor(_settings.AccentColor, Windows.UI.Color.FromArgb(255, 101, 217, 255));
        return (c.R, c.G, c.B);
    }

    private void Dot(float x, float y, float r, int alpha, (int R, int G, int B) col)
    {
        if (alpha <= 0 || GdipCreateSolidFill(Argb(Math.Min(255, alpha), col.R, col.G, col.B), out var br) != 0) return;
        GdipFillEllipse(_g, br, x - r, y - r, 2 * r, 2 * r);
        GdipDeleteBrush(br);
    }

    private void Ripple(float c, float orb, double phase, (int R, int G, int B) col, float width, int maxAlpha, float grow)
    {
        var r = (float)(orb * 0.5 * (1 + grow * phase));
        var alpha = (int)((1 - phase) * (1 - phase) * maxAlpha);
        if (alpha > 0 && GdipCreatePen1(Argb(alpha, col.R, col.G, col.B), width, 2, out var pen) == 0)
        {
            GdipDrawEllipse(_g, pen, c - r, c - r, 2 * r, 2 * r);
            GdipDeletePen(pen);
        }
    }

    /// <summary>
    /// One frame. Animation styles: classic (ripples + bobbing + rotating ring), breathe, float, flip (coin flip every few seconds),
    /// orbit (glowing dots circle the icon), ripples (three strong waves), sparkle (twinkling stars), jelly (squash and stretch bounce).
    /// </summary>
    private void Render()
    {
        if (_g == IntPtr.Zero) return;
        var t = _clock.Elapsed.TotalSeconds;
        float c = _winPx / 2f, orb = _iconPx;
        var accent = Accent();
        var white = (255, 255, 255);
        var violet = (139, 124, 255);
        GdipGraphicsClear(_g, 0);
        GdipResetWorldTransform(_g);
        _hoverV += ((_hover ? 1f : 0f) - _hoverV) * 0.18f;

        float sx = 1, sy = 1, bob = 0, rot = 0, ringSpeed = 28;
        var anim = _settings.FloatingAnimation;
        switch (anim)
        {
            case "breathe":
                sx = sy = 1f + 0.07f * (float)Math.Sin(t * 1.5); ringSpeed = 10;
                Ripple(c, orb, (t / 4.0) % 1.0, accent, Math.Max(1.5f, orb * 0.02f), 120, 0.7f);
                break;
            case "float":
                bob = (float)Math.Sin(t * 1.3) * orb * 0.06f; rot = (float)Math.Sin(t * 0.9) * 5f; ringSpeed = 14;
                break;
            case "flip":
            {
                var phase = (t % 4.5) / 1.1;                                            // a quick coin flip, then rest
                if (phase < 1) sx = Math.Max(0.03f, Math.Abs((float)Math.Cos(phase * Math.PI)));
                bob = (float)Math.Sin(t * 1.6) * orb * 0.02f; ringSpeed = 20;
                break;
            }
            case "orbit":
            {
                bob = (float)Math.Sin(t * 1.7) * orb * 0.015f; ringSpeed = 12;
                var cols = new[] { accent, white, violet };
                for (var i = 0; i < 3; i++)
                {
                    var a = t * (1.3 + 0.45 * i) + i * 2.1; var rad = (float)(orb * (0.62 + 0.05 * i));
                    float x = c + (float)Math.Cos(a) * rad, y = c + (float)Math.Sin(a) * rad * 0.92f;
                    Dot(x, y, orb * 0.075f, 70, cols[i]); Dot(x, y, orb * 0.032f, 255, cols[i]);
                }
                break;
            }
            case "ripples":
                sx = sy = 1f + 0.025f * (float)Math.Sin(t * 2.2); ringSpeed = 0;
                for (var k = 0; k < 3; k++) Ripple(c, orb, (t / 2.2 + k / 3.0) % 1.0, accent, Math.Max(2f, orb * 0.03f), 200, 0.85f);
                break;
            case "sparkle":
                sx = sy = 1f + 0.03f * (float)Math.Sin(t * 1.8); ringSpeed = 16;
                for (var i = 0; i < 9; i++)
                {
                    var a = i * (Math.PI * 2 / 9) + t * 0.12; var rad = (float)(orb * (0.64 + 0.08 * ((i * 37) % 5) / 4.0));
                    var tw = 0.5 + 0.5 * Math.Sin(t * (1.6 + (i % 3) * 0.5) + i * 1.7);
                    float x = c + (float)Math.Cos(a) * rad, y = c + (float)Math.Sin(a) * rad;
                    Dot(x, y, orb * (0.03f + 0.03f * (float)tw), (int)(60 * tw), white); Dot(x, y, orb * (0.012f + 0.012f * (float)tw), (int)(255 * tw), white);
                }
                break;
            case "jelly":
            {
                var w = (float)Math.Sin(t * 4.4);
                sx = 1f + 0.08f * w; sy = 1f - 0.08f * w;
                bob = -(float)Math.Abs(Math.Sin(t * 2.2)) * orb * 0.06f; ringSpeed = 22;
                break;
            }
            default:                                                                  // classic
                sx = sy = 1f + 0.025f * (float)Math.Sin(t * 2.4);
                bob = (float)Math.Sin(t * 1.9) * orb * 0.018f;
                for (var k = 0; k < 2; k++) Ripple(c, orb, (t / 2.8 + k * 0.5) % 1.0, accent, Math.Max(1.5f, orb * 0.02f), 150, 0.62f);
                break;
        }
        var hover = 1f + 0.08f * _hoverV;
        sx *= hover; sy *= hover;
        var side = orb * 1.2f;

        if (_base != IntPtr.Zero)
        {
            GdipResetWorldTransform(_g);
            GdipTranslateWorldTransform(_g, c, c + bob, 0);
            if (rot != 0) GdipRotateWorldTransform(_g, rot, 0);
            GdipScaleWorldTransform(_g, sx, sy, 0);
            GdipDrawImageRect(_g, _base, -side / 2, -side / 2, side, side);
        }
        if (_ring != IntPtr.Zero)
        {
            GdipResetWorldTransform(_g);
            GdipTranslateWorldTransform(_g, c, c + bob * 0.5f, 0);
            GdipRotateWorldTransform(_g, (float)(t * ringSpeed % 360), 0);
            GdipScaleWorldTransform(_g, Math.Max(sx, sy), Math.Max(sx, sy), 0);
            GdipDrawImageRect(_g, _ring, -side / 2, -side / 2, side, side);
        }
        GdipResetWorldTransform(_g);

        var size = new SIZE { cx = _winPx, cy = _winPx };
        var src = new POINT();
        var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = (byte)(_opacity * 255 / 100), AlphaFormat = 1 /* AC_SRC_ALPHA */ };
        UpdateLayeredWindow(_hwnd, IntPtr.Zero, IntPtr.Zero, ref size, _memDc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
    }

    // ---------------- messages ----------------
    private IntPtr Handle(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        switch (m)
        {
            case WM_TIMER:
                Render();
                if (++_tick % 45 == 0 && _settings.FloatingMode != "always") ApplyZOrder();   // keep sitting on the desktop layer
                return IntPtr.Zero;
            case WM_MOUSEACTIVATE:
                return (IntPtr)3;                                                            // MA_NOACTIVATE: clicking never steals focus
            case WM_LBUTTONDOWN:
                _pressed = true; _dragging = false;
                GetCursorPos(out _startCursor);
                GetWindowRect(h, out var wr); _startPos = new POINT { X = wr.Left, Y = wr.Top };
                SetCapture(h);
                return IntPtr.Zero;
            case WM_MOUSEMOVE:
                if (!_tracking) { var tme = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 2 /* TME_LEAVE */, hwndTrack = h }; TrackMouseEvent(ref tme); _tracking = true; }
                _hover = true;
                if (_pressed)
                {
                    GetCursorPos(out var c);
                    int dx = c.X - _startCursor.X, dy = c.Y - _startCursor.Y;
                    if (!_dragging && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4)) _dragging = true;
                    if (_dragging) SetWindowPos(h, IntPtr.Zero, _startPos.X + dx, _startPos.Y + dy, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                }
                return IntPtr.Zero;
            case WM_MOUSELEAVE:
                _hover = false; _tracking = false;
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                if (_pressed)
                {
                    _pressed = false; ReleaseCapture();
                    if (_dragging) { _dragging = false; GetWindowRect(h, out var r); _savePosition(r.Left, r.Top); }
                    else Clicked?.Invoke();
                }
                return IntPtr.Zero;
            case WM_RBUTTONUP:
                ShowMenu();
                return IntPtr.Zero;
        }
        return DefWindowProcW(h, m, w, l);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenuW(menu, 0, (UIntPtr)1, Loc.T("Open GameIndex"));
        AppendMenuW(menu, 0, (UIntPtr)2, Loc.T("Scan for games"));
        AppendMenuW(menu, 0, (UIntPtr)3, Loc.T("Hide desktop icon"));
        AppendMenuW(menu, 0x800 /* MF_SEPARATOR */, UIntPtr.Zero, null);
        AppendMenuW(menu, 0, (UIntPtr)4, Loc.T("Exit GameIndex"));
        GetCursorPos(out var p);
        SetForegroundWindow(_hwnd);
        var cmd = TrackPopupMenu(menu, 0x100 | 0x2 /* TPM_RETURNCMD | TPM_RIGHTBUTTON */, p.X, p.Y, 0, _hwnd, IntPtr.Zero);
        PostMessageW(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        switch (cmd) { case 1: Clicked?.Invoke(); break; case 2: ScanRequested?.Invoke(); break; case 3: HideIconRequested?.Invoke(); break; case 4: ExitRequested?.Invoke(); break; }
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;
        KillTimer(_hwnd, (UIntPtr)1);
        Instances.Remove(_hwnd);
        DestroyWindow(_hwnd); _hwnd = IntPtr.Zero;
        FreeSurface();
        if (_base != IntPtr.Zero) { GdipDisposeImage(_base); _base = IntPtr.Zero; }
        if (_ring != IntPtr.Zero) { GdipDisposeImage(_ring); _ring = IntPtr.Zero; }
        IsShown = false;
    }

    public void Close() => Dispose();
}
