using GameIndex.Core.Models;
using GameIndex.Services;
using GameIndex.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameIndex.Views;

/// <summary>Add a manual game (existing == null) or edit any game. Editing a platform game marks it user-edited so scans stop overwriting these fields.</summary>
public sealed partial class GameEditDialog : ContentDialog
{
    private readonly Window _window;
    private readonly Game? _existing;
    public Game? Result { get; private set; }
    public string? CoverFile { get; private set; }
    public string? BackgroundFile { get; private set; }

    public GameEditDialog(Window window, Game? existing, string? prefillExe, IEnumerable<string> categories)
    {
        InitializeComponent();
        _window = window; _existing = existing;
        Title = Loc.T(existing is null ? "Add game" : "Edit game");
        Loaded += (_, _) => Loc.Apply(this);
        foreach (var c in categories) CategoryBox.Items.Add(c);
        if (existing is not null)
        {
            NameBox.Text = existing.Title; ExeBox.Text = existing.ExePath ?? ""; ArgsBox.Text = existing.LaunchArgs ?? ""; WorkBox.Text = existing.WorkingDir ?? "";
            PlatformBox.Text = existing.CustomPlatformName ?? ""; CategoryBox.Text = existing.Category ?? ""; TrackFolderBox.IsChecked = existing.TrackFolder;
            if (existing.IsManual is false && existing.LaunchUri is not null)
                ErrorText.Text = Loc.T("This game launches through its platform (login, DRM, cloud saves). Title, category and artwork edits apply; the executable is only used for games without a platform launch method.");
        }
        else if (prefillExe is not null)
        {
            ExeBox.Text = prefillExe;
            NameBox.Text = ProductName(prefillExe);
        }
        Field_Changed(null!, null!);
    }

    private static string ProductName(string exe)
    {
        try { var n = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductName; if (!string.IsNullOrWhiteSpace(n)) return n.Trim(); } catch { }
        return Path.GetFileNameWithoutExtension(exe);
    }

    private void Field_Changed(object? sender, object? e)
    {
        var needExe = _existing is null || _existing.IsManual || !string.IsNullOrWhiteSpace(ExeBox.Text);
        IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(NameBox.Text) && (!needExe || !string.IsNullOrWhiteSpace(ExeBox.Text) || (_existing is not null && !_existing.IsManual));
    }

    private async void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var f = await Platform.PickFileAsync(_window, ".exe");
        if (f is null) return;
        ExeBox.Text = f;
        if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = ProductName(f);
    }
    private async void BrowseWork_Click(object sender, RoutedEventArgs e) { var d = await Platform.PickFolderAsync(_window); if (d is not null) WorkBox.Text = d; }
    private async void BrowseCover_Click(object sender, RoutedEventArgs e) { var f = await Platform.PickFileAsync(_window, ".jpg", ".jpeg", ".png", ".webp", ".bmp"); if (f is not null) { CoverFile = f; CoverBox.Text = f; } }
    private async void BrowseBg_Click(object sender, RoutedEventArgs e) { var f = await Platform.PickFileAsync(_window, ".jpg", ".jpeg", ".png", ".webp", ".bmp"); if (f is not null) { BackgroundFile = f; BgBox.Text = f; } }

    private void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var exe = ExeBox.Text.Trim().Trim('"');
        if (exe.Length > 0 && !File.Exists(exe)) { ErrorText.Text = Loc.T("That executable does not exist."); args.Cancel = true; return; }
        var g = _existing is null ? new Game() : new Game
        {
            Id = _existing.Id, PlatformId = _existing.PlatformId, IsManual = _existing.IsManual, InstallDir = _existing.InstallDir, IsFavorite = _existing.IsFavorite
        };
        g.Title = NameBox.Text.Trim();
        g.ExePath = exe.Length == 0 ? null : exe;
        g.LaunchArgs = string.IsNullOrWhiteSpace(ArgsBox.Text) ? null : ArgsBox.Text.Trim();
        g.WorkingDir = string.IsNullOrWhiteSpace(WorkBox.Text) ? null : WorkBox.Text.Trim();
        g.CustomPlatformName = string.IsNullOrWhiteSpace(PlatformBox.Text) ? null : PlatformBox.Text.Trim();
        g.Category = string.IsNullOrWhiteSpace(CategoryBox.Text) ? null : CategoryBox.Text.Trim();
        g.TrackFolder = TrackFolderBox.IsChecked == true;
        if (_existing is null) g.InstallDir = g.ExePath is null ? null : Path.GetDirectoryName(g.ExePath);
        Result = g;
    }
}
