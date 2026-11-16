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
    public static async Task<Windows.UI.Color?> AverageColorAsync(string path)
    {
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
            return sum <= 0 ? null : Windows.UI.Color.FromArgb(255, (byte)(r / sum), (byte)(g / sum), (byte)(b / sum));
        }
        catch { return null; }
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
