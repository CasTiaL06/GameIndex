using System.Diagnostics;

namespace GameIndex.Core.Launching;

public sealed record LaunchResult(bool Success, string Message);

public sealed class GameLauncher
{
    /// <summary>Launches through the platform's protocol when one exists, otherwise starts the executable directly.</summary>
    public LaunchResult Launch(Game g)
    {
        try
        {
            // Platform games always launch through their platform (login, DRM, cloud saves); manual games launch their EXE directly.
            var useUri = !string.IsNullOrWhiteSpace(g.LaunchUri) && !g.IsManual;
            if (useUri)
            {
                if (g.LaunchUri!.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                    Process.Start(new ProcessStartInfo("explorer.exe", g.LaunchUri) { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo(g.LaunchUri) { UseShellExecute = true });
                return new LaunchResult(true, $"Launching {g.Title} via {g.PlatformName}…");
            }
            if (string.IsNullOrWhiteSpace(g.ExePath)) return new LaunchResult(false, "No executable is set for this game. Use Edit Game to choose one.");
            if (!File.Exists(g.ExePath)) return new LaunchResult(false, $"Executable not found: {g.ExePath}");
            var psi = new ProcessStartInfo(g.ExePath)
            {
                UseShellExecute = true,
                WorkingDirectory = !string.IsNullOrWhiteSpace(g.WorkingDir) && Directory.Exists(g.WorkingDir) ? g.WorkingDir : Path.GetDirectoryName(g.ExePath)!,
                Arguments = g.LaunchArgs ?? ""
            };
            Process.Start(psi);
            return new LaunchResult(true, $"Launching {g.Title}…");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new LaunchResult(false, "Launch was cancelled (administrator permission denied).");
        }
        catch (Exception ex)
        {
            Log.Error($"Launch failed for {g.Title}", ex);
            return new LaunchResult(false, $"Could not launch {g.Title}: {ex.Message}");
        }
    }

    public LaunchResult OpenInstallFolder(Game g)
    {
        var dir = g.InstallDir ?? (g.ExePath is null ? null : Path.GetDirectoryName(g.ExePath));
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return new LaunchResult(false, "Installation folder not found.");
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        return new LaunchResult(true, "Opened installation folder.");
    }
}
