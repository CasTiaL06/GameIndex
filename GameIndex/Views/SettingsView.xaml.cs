using GameIndex.Services;
using GameIndex.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameIndex.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsViewModel Vm { get; private set; } = null!;
    private Window _window = null!;
    private MainViewModel _main = null!;
    private Action _close = () => { };

    public SettingsView() { }
    public void Initialize(SettingsViewModel vm, Window window, MainViewModel main, Action close) { Vm = vm; _window = window; _main = main; _close = close; InitializeComponent(); }

    private void Close_Click(object sender, RoutedEventArgs e) => _close();
    private void ResetIcon_Click(object sender, RoutedEventArgs e) => Vm.RequestResetIcon();
    private void TestSound_Click(object sender, RoutedEventArgs e) => Vm.PreviewStartupSound();

    private void AccentPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args) =>
        Vm.AccentHex = $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";

    private async void BrowseBackground_Click(object sender, RoutedEventArgs e)
    {
        var f = await Platform.PickFileAsync(_window, ".jpg", ".jpeg", ".png", ".webp", ".bmp");
        if (f is not null) Vm.CustomBackground = f;
    }

    private async void AddScanDir_Click(object sender, RoutedEventArgs e)
    {
        var d = await Platform.PickFolderAsync(_window);
        if (d is null) return;
        Vm.AddScanDirectory(d);
        Vm.Message = $"Added {d}. Scanning…";
        await _main.ScanNowAsync();
        Vm.RefreshIntegrations();
    }
    private void RemoveScanDir_Click(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is string d) Vm.RemoveScanDirectory(d); }

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SteamKeyBox.Password)) { Vm.Message = "Enter a key first."; return; }
        Vm.SaveSteamKey(SteamKeyBox.Password);
        SteamKeyBox.Password = "";
        _ = RefreshAllSteam();
    }
    private void RemoveKey_Click(object sender, RoutedEventArgs e) { Vm.SaveSteamKey(""); Vm.Message = "Steam API key removed."; }

    private async Task RefreshAllSteam()
    {
        Vm.Message = "Key saved. Loading Steam achievements and playtime in the background…";
        await AppHost.Instance.Info.RefreshStaleAsync();
    }

    private async void BrowseBackup_Click(object sender, RoutedEventArgs e)
    {
        var d = await Platform.PickFolderAsync(_window);
        if (d is not null) Vm.BackupDirectory = d;
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var file = await Task.Run(() => AppHost.Instance.Db.Backup(string.IsNullOrEmpty(Vm.BackupDirectory) ? null : Vm.BackupDirectory));
            Vm.Message = "Backup created: " + file;
        }
        catch (Exception ex) { Vm.Message = "Backup failed: " + ex.Message; }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var f = await Platform.PickFileAsync(_window, ".db");
        if (f is null) return;
        var ask = new ContentDialog { XamlRoot = XamlRoot, Title = "Restore database?", Content = "Your current database is backed up first, then replaced by the selected file.", PrimaryButtonText = "Restore", CloseButtonText = "Cancel" };
        if (await ask.ShowAsync() != ContentDialogResult.Primary) return;
        try { await Task.Run(() => AppHost.Instance.Db.Restore(f)); await _main.ReloadAsync(); AppHost.Instance.Tracker.InvalidateWatchList(); Vm.Message = "Database restored."; }
        catch (Exception ex) { Vm.Message = "Restore failed: " + ex.Message; }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var f = await Platform.SaveFileAsync(_window, "GameIndex-library", "GameIndex library", ".json");
        if (f is null) return;
        try { await Task.Run(() => AppHost.Instance.Db.ExportLibrary(f)); Vm.Message = "Library exported to " + f; }
        catch (Exception ex) { Vm.Message = "Export failed: " + ex.Message; }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var f = await Platform.PickFileAsync(_window, ".json");
        if (f is null) return;
        try { var n = await Task.Run(() => AppHost.Instance.Db.ImportLibrary(f)); await _main.ReloadAsync(); Vm.Message = $"Import finished. {n} game(s) added; existing data was kept."; }
        catch (Exception ex) { Vm.Message = "Import failed: " + ex.Message; }
    }

    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        await Task.Run(() => AppHost.Instance.Artwork.ClearAutomatic());
        await _main.ReloadAsync();
        _ = AppHost.Instance.Info.RefreshStaleAsync();
        Vm.Message = "Cache cleared. Automatic artwork is being downloaded again; custom artwork was kept.";
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppHost.Instance.Paths.Root}\"") { UseShellExecute = true });
}
