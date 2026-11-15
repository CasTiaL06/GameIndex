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
