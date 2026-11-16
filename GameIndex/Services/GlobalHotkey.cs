using System.Runtime.InteropServices;

namespace GameIndex.Services;

/// <summary>System-wide hotkey (works while a game is in front). Registered on the main window; WM_HOTKEY is received through a window subclass.</summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312, HotkeyId = 0x4731;
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr h, SubclassProc p, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr h, SubclassProc p, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);

    private readonly IntPtr _hwnd;
    private readonly SubclassProc _proc;
    private bool _hooked, _registered;

    public event Action? Pressed;

    public GlobalHotkey(IntPtr hwnd) { _hwnd = hwnd; _proc = WndProc; }

    /// <summary>"Alt+G", "Ctrl+Alt+G", "Ctrl+Shift+G", "F9" … Returns false when another program already owns the combination.</summary>
    public bool Register(string combo)
    {
        Unregister();
        if (!TryParse(combo, out var mods, out var vk)) return false;
        if (!_hooked) { SetWindowSubclass(_hwnd, _proc, (UIntPtr)2, UIntPtr.Zero); _hooked = true; }
        _registered = RegisterHotKey(_hwnd, HotkeyId, mods | MOD_NOREPEAT, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered) { UnregisterHotKey(_hwnd, HotkeyId); _registered = false; }
    }

    public static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "alt": mods |= MOD_ALT; break;
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                default:
                    if (raw.Length == 1 && char.IsLetterOrDigit(raw[0])) vk = char.ToUpperInvariant(raw[0]);
                    else if (raw.Length >= 2 && (raw[0] is 'F' or 'f') && int.TryParse(raw[1..], out var n) && n is >= 1 and <= 24) vk = (uint)(0x70 + n - 1);
                    else return false;
                    break;
            }
        }
        return vk != 0;
    }

    private IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l, UIntPtr id, UIntPtr data)
    {
        if (msg == WM_HOTKEY && w.ToInt32() == HotkeyId) { Pressed?.Invoke(); return IntPtr.Zero; }
        return DefSubclassProc(h, msg, w, l);
    }

    public void Dispose()
    {
        Unregister();
        if (_hooked) { RemoveWindowSubclass(_hwnd, _proc, (UIntPtr)2); _hooked = false; }
    }
}
