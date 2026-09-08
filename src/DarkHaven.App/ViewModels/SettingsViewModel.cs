using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Engine;

namespace DarkHaven.App.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppServices _services;

    [ObservableProperty] private bool _autoUpdate;
    [ObservableProperty] private bool _minimizeOnLaunch;
    [ObservableProperty] private bool _verboseLog;
    [ObservableProperty] private bool _compatMode;
    [ObservableProperty] private string _authUrl;
    [ObservableProperty] private string _buildsUrl;
    [ObservableProperty] private string _regionsUrl;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string _cacheSummary = "…";

    public string DataDir => LauncherPaths.DataDir;
    public string VersionLine =>
        $"ЛАУНЧЕР 0.1.0     ·     ДВИЖОК Robust (в комплекте)     ·     КОНТЕНТ —";

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        _autoUpdate = Cfg("AutoUpdate", true);
        _minimizeOnLaunch = Cfg("MinimizeOnLaunch", true);
        _verboseLog = Cfg("VerboseLog", false);
        _compatMode = Cfg("CompatMode", false);
        _authUrl = services.Settings.GetConfig("AuthUrl") ?? AuthApi.DefaultBaseUrl;
        _buildsUrl = services.Settings.GetConfig("EngineBuildsUrl") ?? EngineManager.BuildsManifestUrl;
        _regionsUrl = services.Settings.GetConfig("RegionsUrl") ?? "";
        _ = LoadCacheSummaryAsync();
    }

    private bool Cfg(string key, bool dflt) =>
        _services.Settings.GetConfig(key) is { } v ? v == "true" : dflt;

    partial void OnAutoUpdateChanged(bool v) => _services.Settings.SetConfig("AutoUpdate", v ? "true" : "false");
    partial void OnMinimizeOnLaunchChanged(bool v) => _services.Settings.SetConfig("MinimizeOnLaunch", v ? "true" : "false");
    partial void OnVerboseLogChanged(bool v) => _services.Settings.SetConfig("VerboseLog", v ? "true" : "false");
    partial void OnCompatModeChanged(bool v) => _services.Settings.SetConfig("CompatMode", v ? "true" : "false");
    partial void OnAuthUrlChanged(string v) => _services.Settings.SetConfig("AuthUrl", Trim(v));
    partial void OnBuildsUrlChanged(string v) => _services.Settings.SetConfig("EngineBuildsUrl", Trim(v));
    partial void OnRegionsUrlChanged(string v) => _services.Settings.SetConfig("RegionsUrl", Trim(v));

    private static string? Trim(string v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    [RelayCommand] private void ResetAuthUrl() => AuthUrl = AuthApi.DefaultBaseUrl;
    [RelayCommand] private void ResetBuildsUrl() => BuildsUrl = EngineManager.BuildsManifestUrl;

    [RelayCommand]
    private void OpenDataDir()
    {
        LauncherPaths.EnsureDirectories();
        Process.Start(new ProcessStartInfo(LauncherPaths.DataDir) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task ClearContentCacheAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(LauncherPaths.ContentDbPath + suffix))
                        File.Delete(LauncherPaths.ContentDbPath + suffix);
            });
            _services.ContentDb.Initialize();
            Status = "Кэш контента очищен.";
            await LoadCacheSummaryAsync();
        }
        catch (Exception e)
        {
            Status = "Не удалось очистить кэш: " + e.Message;
        }
    }

    [RelayCommand]
    private void CheckForUpdates() => Status = "Самообновление лаунчера появится ближе к релизу.";

    private async Task LoadCacheSummaryAsync()
    {
        try
        {
            var (bytes, versions, engines) = await Task.Run(() =>
            {
                long b = 0;
                if (File.Exists(LauncherPaths.ContentDbPath)) b += new FileInfo(LauncherPaths.ContentDbPath).Length;
                var eng = Directory.Exists(LauncherPaths.EnginesDir)
                    ? Directory.GetFiles(LauncherPaths.EnginesDir, "*.zip") : [];
                foreach (var f in eng) b += new FileInfo(f).Length;

                var v = 0;
                using var con = _services.ContentDb.Connect();
                using var cmd = con.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM ContentVersion";
                try { v = Convert.ToInt32(cmd.ExecuteScalar()); } catch { /* fresh db */ }
                return (b, v, eng.Length);
            });

            var gb = bytes / 1_000_000_000.0;
            CacheSummary = gb >= 1
                ? $"{gb:0.0} ГБ · {versions} версий контента, {engines} сборок движка"
                : $"{bytes / 1_000_000.0:0} МБ · {versions} версий контента, {engines} сборок движка";
        }
        catch
        {
            CacheSummary = "—";
        }
    }
}
