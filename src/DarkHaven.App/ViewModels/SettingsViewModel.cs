using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher;

namespace DarkHaven.App.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppServices _services;

    [ObservableProperty] private bool _compatMode;
    [ObservableProperty] private string _regionsUrl;
    [ObservableProperty] private string? _status;

    public string DataDir => LauncherPaths.DataDir;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        _compatMode = services.Settings.GetConfig("CompatMode") == "true";
        _regionsUrl = services.Settings.GetConfig("RegionsUrl") ?? "";
    }

    partial void OnCompatModeChanged(bool value) =>
        _services.Settings.SetConfig("CompatMode", value ? "true" : "false");

    partial void OnRegionsUrlChanged(string value) =>
        _services.Settings.SetConfig("RegionsUrl", string.IsNullOrWhiteSpace(value) ? null : value.Trim());

    [RelayCommand]
    private void OpenDataDir()
    {
        LauncherPaths.EnsureDirectories();
        Process.Start(new ProcessStartInfo(LauncherPaths.DataDir) { UseShellExecute = true });
    }

    [RelayCommand]
    private void ClearContentCache()
    {
        try
        {
            if (File.Exists(LauncherPaths.ContentDbPath))
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                File.Delete(LauncherPaths.ContentDbPath);
                foreach (var wal in new[] { "-wal", "-shm" })
                    File.Delete(LauncherPaths.ContentDbPath + wal);
            }
            _services.ContentDb.Initialize();
            Status = "Content cache cleared.";
        }
        catch (Exception e)
        {
            Status = "Could not clear cache: " + e.Message;
        }
    }
}
