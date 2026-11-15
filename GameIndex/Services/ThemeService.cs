using GameIndex.Core.Settings;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace GameIndex.Services;

/// <summary>Applies theme, accent, glass transparency and window backdrop. Brushes are shared instances, so mutating Color updates the whole UI live.</summary>
public static class ThemeService
{
    public static Color ParseColor(string? hex, Color fallback)
    {
        try
        {
            var h = (hex ?? "").TrimStart('#');
            if (h.Length == 6) return Color.FromArgb(255, Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..], 16));
        }
        catch { }
        return fallback;
    }

    private static string? _lastBackdrop;

    private static void Set(string key, Color c) => ((SolidColorBrush)Application.Current.Resources[key]).Color = c;

    public static bool IsDark(AppSettings s) => s.Theme switch
    {
        "Light" => false,
        "System" => Application.Current.RequestedTheme == ApplicationTheme.Dark,
        _ => true
    };

    public static void Apply(AppSettings s, Window window, FrameworkElement root)
    {
        var dark = IsDark(s);
        root.RequestedTheme = s.Theme switch { "Light" => ElementTheme.Light, "System" => ElementTheme.Default, _ => ElementTheme.Dark };

        var solid = !s.TransparencyEnabled;
        var a = (byte)(solid ? 255 : Math.Clamp(s.TransparencyPercent * 255 / 100, 0, 255));
        var a2 = (byte)(solid ? 255 : Math.Clamp((s.TransparencyPercent - 7) * 255 / 100, 0, 255));
        var ao = (byte)(solid ? 255 : Math.Clamp(s.OverlayOpacityPercent * 255 / 100, 0, 255));
        Set("OverlayPanelBrush", dark ? Color.FromArgb(ao, 20, 24, 34) : Color.FromArgb(ao, 250, 251, 253));
        var backdrop = solid ? "None" : s.Backdrop;
        if (dark)
        {
            Set("WindowBgBrush", Color.FromArgb(255, 0x0B, 0x0D, 0x12));
            Set("SecondaryBgBrush", Color.FromArgb(255, 0x11, 0x15, 0x1D));
            Set("GlassPanelBrush", Color.FromArgb(a, 25, 30, 42));
            Set("ElevatedPanelBrush", Color.FromArgb(a2, 36, 43, 56));
            Set("BorderSoftBrush", Color.FromArgb(0x17, 255, 255, 255));
            Set("HoverBrush", Color.FromArgb(0x12, 255, 255, 255));
            Set("TextPrimaryBrush", Color.FromArgb(255, 0xF2, 0xF4, 0xF8));
            Set("TextSecondaryBrush", Color.FromArgb(255, 0x92, 0x9B, 0xAA));
        }
        else
        {
            Set("WindowBgBrush", Color.FromArgb(255, 0xF3, 0xF5, 0xF9));
            Set("SecondaryBgBrush", Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
            Set("GlassPanelBrush", Color.FromArgb(a, 255, 255, 255));
            Set("ElevatedPanelBrush", Color.FromArgb(a2, 238, 241, 247));
            Set("BorderSoftBrush", Color.FromArgb(0x1F, 0, 0, 0));
            Set("HoverBrush", Color.FromArgb(0x10, 0, 0, 0));
            Set("TextPrimaryBrush", Color.FromArgb(255, 0x12, 0x16, 0x1F));
            Set("TextSecondaryBrush", Color.FromArgb(255, 0x5B, 0x64, 0x72));
        }
        Set("AccentBrush", ParseColor(s.AccentColor, Color.FromArgb(255, 0x65, 0xD9, 0xFF)));

        if (backdrop != _lastBackdrop)
        {
            _lastBackdrop = backdrop;
            window.SystemBackdrop = backdrop switch
        {
            "Acrylic" => new DesktopAcrylicBackdrop(),
            "MicaAlt" => new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
            "None" => null,
            _ => new MicaBackdrop()
            };
        }
        if (root is Grid g) g.Background = backdrop == "None" ? (Brush)Application.Current.Resources["WindowBgBrush"] : new SolidColorBrush(Colors.Transparent);
    }
}
