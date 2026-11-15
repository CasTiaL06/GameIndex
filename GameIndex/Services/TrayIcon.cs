using System.Runtime.InteropServices;

namespace GameIndex.Services;

/// <summary>Notification-area icon via Shell_NotifyIcon (WinUI 3 has no built-in tray API). Subclasses the main window to receive callbacks.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint WM_TRAY = 0x8001, NIM_ADD = 0, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const uint WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 2, MF_STRING = 0, MF_SEPARATOR = 0x800;

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr h, SubclassProc p, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr h, SubclassProc p, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATA d);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint n);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr m, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr m, uint flags, int x, int y, int r, IntPtr h, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr m);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    private readonly IntPtr _hwnd;
    private readonly SubclassProc _proc; // keep alive
    private bool _added;

    public event Action? OpenRequested, ScanRequested, ExitRequested;

    public TrayIcon(IntPtr hwnd) { _hwnd = hwnd; _proc = WndProc; }

    public void Show()
    {
        if (_added) return;
        SetWindowSubclass(_hwnd, _proc, (UIntPtr)1, UIntPtr.Zero);
        var small = new IntPtr[1];
        ExtractIconEx(Environment.ProcessPath ?? "", 0, null, small, 1);
        var d = new NOTIFYICONDATA { cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = 1, uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP, uCallbackMessage = WM_TRAY, hIcon = small[0], szTip = "GameIndex", szInfo = "", szInfoTitle = "" };
        _added = Shell_NotifyIconW(NIM_ADD, ref d);
    }

    public void Hide()
    {
        if (!_added) return;
        var d = new NOTIFYICONDATA { cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = 1, szTip = "", szInfo = "", szInfoTitle = "" };
        Shell_NotifyIconW(NIM_DELETE, ref d);
        RemoveWindowSubclass(_hwnd, _proc, (UIntPtr)1);
        _added = false;
    }

    private IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l, UIntPtr id, UIntPtr data)
    {
        if (msg == WM_TRAY)
        {
            var ev = (uint)l.ToInt64() & 0xFFFF;
            if (ev == WM_LBUTTONUP) OpenRequested?.Invoke();
            else if (ev == WM_RBUTTONUP)
            {
                var menu = CreatePopupMenu();
                AppendMenu(menu, MF_STRING, 1, "Open GameIndex");
                AppendMenu(menu, MF_STRING, 2, "Scan for games");
                AppendMenu(menu, MF_SEPARATOR, 0, null);
                AppendMenu(menu, MF_STRING, 3, "Exit");
                GetCursorPos(out var p);
                SetForegroundWindow(_hwnd);
                var cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.X, p.Y, 0, _hwnd, IntPtr.Zero);
                DestroyMenu(menu);
                if (cmd == 1) OpenRequested?.Invoke(); else if (cmd == 2) ScanRequested?.Invoke(); else if (cmd == 3) ExitRequested?.Invoke();
            }
            return IntPtr.Zero;
        }
        return DefSubclassProc(h, msg, w, l);
    }

    public void Dispose() => Hide();
}
