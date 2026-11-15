using GameIndex.Core;
using GameIndex.Core.Models;
using Microsoft.Win32;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace GameIndex.Services;

public static class Platform
{
    /// <summary>Extracts the shell icon of the game's EXE to Assets/Icons (used as fallback artwork).</summary>
    public static async Task<string?> ExtractExeIconAsync(Game g, string exePath, CancellationToken ct)
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
        catch (Exception ex) { Log.Warn($"Icon extraction failed for {g.Title}: {ex.Message}"); return null; }
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
