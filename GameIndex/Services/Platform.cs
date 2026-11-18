using GameIndex.Core;
using GameIndex.Core.Models;
using Microsoft.Win32;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace GameIndex.Services;

public static class Platform
{
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    /// <summary>True when the EXE really contains an icon resource (so the shell will not hand us its generic "blank app" icon).</summary>
    private static bool HasEmbeddedIcon(string exe)
    {
        try { return ExtractIconEx(exe, -1, null, null, 0) > 0; } catch { return true; }
    }

    /// <summary>Tries each candidate EXE until one yields a real icon; saves it as PNG under Assets/Icons.</summary>
    public static async Task<string?> ExtractExeIconAsync(Game g, IReadOnlyList<string> exes, CancellationToken ct)
    {
        foreach (var exe in exes)
        {
            ct.ThrowIfCancellationRequested();
            if (!HasEmbeddedIcon(exe)) continue;
            var r = await ExtractOneAsync(g, exe);
            if (r is not null) return r;
        }
        return null;
    }

    private static async Task<string?> ExtractOneAsync(Game g, string exePath)
    {
        var native = await Task.Run(() => ExtractNativeIcon(g, exePath));
        if (native is not null) return native;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(exePath);
            var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 256, ThumbnailOptions.UseCurrentScale);
            if (thumb is null || thumb.Size == 0) thumb = await file.GetThumbnailAsync(ThumbnailMode.ListView, 256, ThumbnailOptions.None);
            if (thumb is null || thumb.Size == 0) return null;
            using var _ = thumb;
            var dest = Path.Combine(AppHost.Instance.Paths.IconsDir, $"exe_{g.Id}.png");
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(thumb);
            var pixels = await decoder.GetPixelDataAsync();
            using var fs = new FileStream(dest, FileMode.Create);
            var enc = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, fs.AsRandomAccessStream());
            enc.SetPixelData(decoder.BitmapPixelFormat, decoder.BitmapAlphaMode, decoder.PixelWidth, decoder.PixelHeight, 96, 96, pixels.DetachPixelData());
            await enc.FlushAsync();
            return dest;
        }
        catch (Exception ex) { Log.Warn($"Icon extraction failed for {g.Title} ({Path.GetFileName(exePath)}): {ex.Message}"); return null; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    public static void Foreground(Microsoft.UI.Xaml.Window w) => SetForegroundWindow(Hwnd(w));
    public static bool IsForeground(Microsoft.UI.Xaml.Window w) => GetForegroundWindow() == Hwnd(w);

    /// <summary>Saturation-weighted average colour of an image (16x16 sample) – used to tint the background around icon/cover art.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Windows.UI.Color> TintCache = new();

    public static async Task<Windows.UI.Color?> AverageColorAsync(string path)
    {
        if (TintCache.TryGetValue(path, out var cached)) return cached;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var dec = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            var tr = new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = 16, ScaledHeight = 16, InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant };
            var px = await dec.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Straight, tr,
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            var d = px.DetachPixelData();
            double r = 0, g = 0, b = 0, sum = 0;
            for (var i = 0; i + 3 < d.Length; i += 4)
            {
                var a = d[i + 3] / 255.0;
                if (a < 0.25) continue;
                double B = d[i], G = d[i + 1], R = d[i + 2];
                double mx = Math.Max(R, Math.Max(G, B)), mn = Math.Min(R, Math.Min(G, B));
                var sat = mx < 1 ? 0 : (mx - mn) / mx;
                var w = a * (0.15 + sat);
                r += R * w; g += G * w; b += B * w; sum += w;
            }
            if (sum <= 0) return null;
            var result = Windows.UI.Color.FromArgb(255, (byte)(r / sum), (byte)(g / sum), (byte)(b / sum));
            TintCache[path] = result;
            return result;
        }
        catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attribute, ref int value, int size);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr h, IntPtr region, bool redraw);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>
    /// Rounded window corners. Windows 11: ask DWM for round corners (smooth, anti-aliased, keeps the shadow).
    /// Windows 10 has no such API, so the window is clipped with a rounded region (not anti-aliased, but still rounded).
    /// Maximized windows are always square.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);

    /// <summary>
    /// Window corners: 0 = square, 1 = slightly rounded, 2 = rounded (Windows 11 default). Uses only DWM's own anti-aliased rounding;
    /// clipping the window with a region caused white corner artifacts, so no region is ever applied (any old one is cleared).
    /// Windows 10 cannot round windows at all.
    /// </summary>
    public static void RoundWindow(Microsoft.UI.Xaml.Window w, int level = 2)
    {
        try
        {
            var h = Hwnd(w);
            SetWindowRgn(h, IntPtr.Zero, true);
            if (!IsWindows11) return;
            var pref = level <= 0 ? 1 /* DWMWCP_DONOTROUND */ : level == 1 ? 3 /* DWMWCP_ROUNDSMALL */ : 2 /* DWMWCP_ROUND */;
            DwmSetWindowAttribute(h, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
        }
        catch { /* purely cosmetic */ }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint colorKey, byte alpha, uint flags);

    /// <summary>Whole-window opacity (0-255) for the open/close fade; avoids resizing the window while animating.</summary>
    public static void SetWindowAlpha(Microsoft.UI.Xaml.Window w, byte alpha)
    {
        try
        {
            var h = Hwnd(w);
            var ex = GetWindowLongPtr(h, -20 /* GWL_EXSTYLE */).ToInt64();
            if ((ex & 0x80000) == 0) SetWindowLongPtr(h, -20, (IntPtr)(ex | 0x80000 /* WS_EX_LAYERED */));
            SetLayeredWindowAttributes(h, 0, alpha, 2 /* LWA_ALPHA */);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint PrivateExtractIconsW(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint count, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    /// <summary>Extracts the icon resource at the largest available size (alpha preserved) - sharper than a shell thumbnail, which upscales small icons.</summary>
    private static string? ExtractNativeIcon(Game g, string exe)
    {
        var dest = Path.Combine(AppHost.Instance.Paths.IconsDir, $"exe_{g.Id}.png");
        foreach (var size in new[] { 256, 128, 64, 48 })
        {
            var icons = new IntPtr[1]; var ids = new int[1];
            uint n;
            try { n = PrivateExtractIconsW(exe, 0, size, size, icons, ids, 1, 0); } catch { return null; }
            if (n == 0 || n == 0xFFFFFFFF || icons[0] == IntPtr.Zero) continue;
            try
            {
                using var icon = System.Drawing.Icon.FromHandle(icons[0]);
                using var bmp = icon.ToBitmap();
                if (bmp.Width < 32) continue;
                bmp.Save(dest, System.Drawing.Imaging.ImageFormat.Png);
                return dest;
            }
            catch { }
            finally { DestroyIcon(icons[0]); }
        }
        return null;
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>HKCU only, so no administrator rights are needed. In portable mode this points at the current folder.</summary>
    public static void SetStartWithWindows(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k is null) return;
            if (on) k.SetValue("GameIndex", $"\"{Environment.ProcessPath}\" --minimized");
            else k.DeleteValue("GameIndex", false);
        }
        catch (Exception ex) { Log.Warn("Start with Windows failed: " + ex.Message); }
    }

    public static IntPtr Hwnd(Microsoft.UI.Xaml.Window w) => WinRT.Interop.WindowNative.GetWindowHandle(w);

    public static async Task<string?> PickFileAsync(Microsoft.UI.Xaml.Window w, params string[] extensions)
    {
        var p = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(p, Hwnd(w));
        foreach (var e in extensions) p.FileTypeFilter.Add(e);
        return (await p.PickSingleFileAsync())?.Path;
    }

    public static async Task<string?> PickFolderAsync(Microsoft.UI.Xaml.Window w)
    {
        var p = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(p, Hwnd(w));
        p.FileTypeFilter.Add("*");
        return (await p.PickSingleFolderAsync())?.Path;
    }

    public static async Task<string?> SaveFileAsync(Microsoft.UI.Xaml.Window w, string suggested, string label, string ext)
    {
        var p = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = suggested };
        p.FileTypeChoices.Add(label, new List<string> { ext });
        WinRT.Interop.InitializeWithWindow.Initialize(p, Hwnd(w));
        return (await p.PickSaveFileAsync())?.Path;
    }
}
