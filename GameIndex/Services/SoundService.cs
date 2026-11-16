using System.Runtime.InteropServices;

namespace GameIndex.Services;

/// <summary>Plays the short UI sounds in Assets/Sounds (startup, open, close) through winmm PlaySound (no extra dependencies).</summary>
public static class SoundService
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern bool PlaySound(string? sound, IntPtr module, uint flags);
    private const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_FILENAME = 0x20000;

    public static void Play(string name, bool enabled = true)
    {
        if (!enabled) return;
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", name + ".wav");
            if (File.Exists(file)) PlaySound(file, IntPtr.Zero, SND_ASYNC | SND_NODEFAULT | SND_FILENAME);
        }
        catch { /* sound is a nicety, never a failure */ }
    }
}
