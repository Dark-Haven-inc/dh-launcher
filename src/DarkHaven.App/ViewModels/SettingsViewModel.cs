using System.Diagnostics;
using System.IO.Compression;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.App.Themes;
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
    [ObservableProperty] private string _downloadLimit;
    [ObservableProperty] private string _authUrl;
    [ObservableProperty] private string _buildsUrl;
    [ObservableProperty] private string _regionsUrl;
    [ObservableProperty] private string _platformUrl;
    [ObservableProperty] private string _discordAppId;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string _cacheSummary = "…";
    [ObservableProperty] private Theme _selectedTheme = Theme.Current;

    public IReadOnlyList<Theme> ThemeChoices => Theme.All;

    /// <summary>The retro theme has its tube effects instead of the color editor (its greens are the look).</summary>
    public bool IsRetro => Theme.Current.Layout == ThemeLayout.Retro;
    [ObservableProperty] private bool _retroScanlines = RetroEffects.Scanlines;
    [ObservableProperty] private bool _retroGlow = RetroEffects.Glow;
    [ObservableProperty] private bool _retroNoise = RetroEffects.Noise;
    [ObservableProperty] private bool _retroBand = RetroEffects.Band;
    [ObservableProperty] private bool _retroFlicker = RetroEffects.Flicker;

    /// <summary>The theme's colors, next to the theme picker: the background every gray follows, and active buttons.</summary>
    public HsvSetting BaseColor { get; } = new("ОБЩИЙ", shown: Theme.BackgroundFor);
    public HsvSetting ActiveColor { get; } = new("АКТИВНЫЕ КНОПКИ", shown: Theme.AccentFill);

    // A drag moves the sliders many times a second: recolor on each, write to the DB once it settles.
    private readonly DispatcherTimer _saveColorsTimer;
    private (Theme Theme, ThemeColors Colors)? _unsavedColors;

    public string DataDir => LauncherPaths.DataDir;

    /// <summary>The ПОДКЛЮЧЕНИЕ addresses: developers only (<see cref="AppServices.DevOverrides"/>).</summary>
    public bool ShowConnection => AppServices.DevOverrides;
    public string VersionLine =>
        $"ЛАУНЧЕР {LauncherInfo.Version}     ·     ДВИЖОК Robust (в комплекте)";

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        _autoUpdate = Cfg("AutoUpdate", true);
        _minimizeOnLaunch = Cfg("MinimizeOnLaunch", true);
        _verboseLog = Cfg("VerboseLog", false);
        _compatMode = Cfg("CompatMode", false);
        _downloadLimit = services.Settings.GetConfig("DownloadLimitKbps") ?? "";
        _authUrl = services.Settings.GetConfig("AuthUrl") ?? AuthApi.DefaultBaseUrl;
        _buildsUrl = services.Settings.GetConfig("EngineBuildsUrl") ?? EngineManager.BuildsManifestUrl;
        _regionsUrl = services.Settings.GetConfig("RegionsUrl") ?? "";
        _platformUrl = services.Settings.GetConfig("PlatformApiUrl") ?? PlatformApi.DefaultBaseUrl;
        _discordAppId = services.Settings.GetConfig("DiscordAppId") ?? "";
        ApplyVerboseLog(_verboseLog);

        _saveColorsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => SaveColors());
        ShowThemeColors();
        BaseColor.Changed += () => EditColors(Theme.CurrentColors with { Base = BaseColor.Hsv });
        ActiveColor.Changed += () => EditColors(Theme.CurrentColors with { Active = ActiveColor.Hsv });
        _ = LoadCacheSummaryAsync();
    }

    private bool Cfg(string key, bool dflt) =>
        _services.Settings.GetConfig(key) is { } v ? v == "true" : dflt;

    // The setting was previously persisted but never actually read — the logger ran at Debug
    // regardless of this checkbox. Apply it both at startup (here) and on every change (below).
    private static void ApplyVerboseLog(bool verbose) =>
        Program.LevelSwitch.MinimumLevel = verbose ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information;

    partial void OnAutoUpdateChanged(bool v) => _services.Settings.SetConfig("AutoUpdate", v ? "true" : "false");
    partial void OnMinimizeOnLaunchChanged(bool v) => _services.Settings.SetConfig("MinimizeOnLaunch", v ? "true" : "false");
    partial void OnVerboseLogChanged(bool v) { _services.Settings.SetConfig("VerboseLog", v ? "true" : "false"); ApplyVerboseLog(v); }
    partial void OnCompatModeChanged(bool v) => _services.Settings.SetConfig("CompatMode", v ? "true" : "false");

    partial void OnDownloadLimitChanged(string v)
    {
        var kbps = int.TryParse(v?.Trim(), out var n) && n > 0 ? n : 0;
        _services.Settings.SetConfig("DownloadLimitKbps", kbps > 0 ? kbps.ToString() : null);
        DarkHaven.Launcher.Content.DownloadThrottle.SetKbps(kbps);
    }
    partial void OnAuthUrlChanged(string v) => _services.Settings.SetConfig("AuthUrl", Trim(v));
    partial void OnBuildsUrlChanged(string v) => _services.Settings.SetConfig("EngineBuildsUrl", Trim(v));
    partial void OnRegionsUrlChanged(string v) => _services.Settings.SetConfig("RegionsUrl", Trim(v));
    partial void OnPlatformUrlChanged(string v) => _services.Settings.SetConfig("PlatformApiUrl", Trim(v));
    partial void OnDiscordAppIdChanged(string v) => _services.Settings.SetConfig("DiscordAppId", Trim(v));

    partial void OnSelectedThemeChanged(Theme value)
    {
        _services.Settings.SetConfig("Theme", value.Id);
        // Once the picker's own input is done with: the switch closes the window it sits in.
        Dispatcher.UIThread.Post(() =>
        {
            SaveColors();
            App.SwitchTheme(value);
            ShowThemeColors();
            OnPropertyChanged(nameof(IsRetro));
        });
    }

    partial void OnRetroScanlinesChanged(bool value) => SaveRetroEffects();
    partial void OnRetroGlowChanged(bool value) => SaveRetroEffects();
    partial void OnRetroNoiseChanged(bool value) => SaveRetroEffects();
    partial void OnRetroBandChanged(bool value) => SaveRetroEffects();
    partial void OnRetroFlickerChanged(bool value) => SaveRetroEffects();
    private void SaveRetroEffects() => RetroEffects.Set(_services.Settings, RetroScanlines, RetroGlow, RetroNoise, RetroBand, RetroFlicker);

    /// <summary>The editor shows the player's colors on the current theme, or its palette's own.</summary>
    private void ShowThemeColors()
    {
        BaseColor.Set(Theme.CurrentColors.Base ?? Theme.PaletteBase);
        ActiveColor.Set(Theme.CurrentColors.Active ?? Theme.PaletteActive);
    }

    private void EditColors(ThemeColors colors)
    {
        App.Recolor(colors);
        ActiveColor.RefreshShown(); // a new background can move the accent more, or less
        _unsavedColors = (Theme.Current, colors);
        _saveColorsTimer.Stop();
        _saveColorsTimer.Start();
    }

    private void SaveColors()
    {
        _saveColorsTimer.Stop();
        if (_unsavedColors is not { } u)
            return;
        _unsavedColors = null;
        u.Theme.SaveColors(_services.Settings, u.Colors);
    }

    [RelayCommand]
    private void ResetColors()
    {
        EditColors(ThemeColors.None);
        ShowThemeColors();
    }

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
    private async Task CollectLogsAsync()
    {
        try
        {
            // Not every Linux setup has a desktop folder.
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
                desktop = LauncherPaths.DataDir;
            var target = Path.Combine(desktop, $"dh-launcher-logs-{DateTime.Now:yyyyMMdd-HHmm}.zip");

            await Task.Run(() =>
            {
                using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
                if (Directory.Exists(LauncherPaths.LogsDir))
                    foreach (var f in Directory.GetFiles(LauncherPaths.LogsDir, "*.log"))
                        zip.CreateEntryFromFile(f, Path.GetFileName(f));
            });

            Status = $"Логи собраны: {target}";
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(desktop) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Status = "Не удалось собрать логи: " + e.Message;
        }
    }

    [RelayCommand]
    private async Task VerifyContentAsync()
    {
        Status = "Проверяю целостность кэша…";
        var problem = await Task.Run(_services.ContentDb.CheckIntegrity);
        Status = problem is null
            ? "Кэш контента цел."
            : "Кэш повреждён — нажмите «Очистить», он скачается заново. " + problem;
    }

    [RelayCommand]
    private async Task CullEnginesAsync()
    {
        try
        {
            var freed = await Task.Run(() => _services.Engines.CullEngines());
            Status = freed > 0
                ? $"Удалено неиспользуемых сборок движка на {freed / 1_000_000.0:0} МБ."
                : "Лишних сборок движка нет.";
            await LoadCacheSummaryAsync();
        }
        catch (Exception e)
        {
            Status = "Не удалось: " + e.Message;
        }
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
    private async Task CheckForUpdatesAsync()
    {
        if (!_services.Updater.Supported)
        {
            Status = "Самообновление доступно только в установленной версии лаунчера.";
            return;
        }

        Status = "Проверяю обновления…";
        try
        {
            if (await _services.Updater.CheckAsync())
                Status = $"Доступно обновление {_services.Updater.PendingVersion}";
            else
                Status = $"Установлена последняя версия ({_services.Updater.CurrentVersion}).";
        }
        catch (Exception e)
        {
            Status = "Не удалось проверить обновления: " + e.Message;
        }
    }

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
