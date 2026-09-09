using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher.Servers;
using DarkHaven.Launcher.Update;

namespace DarkHaven.App.ViewModels;

public enum NavPage { Home, Regions, Servers, News, Settings, Admin, Account, Profile }

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly AppServices _services;

    [ObservableProperty] private NavPage _page = NavPage.Regions;
    [ObservableProperty] private ViewModelBase _current;
    [ObservableProperty] private ConnectingViewModel? _connecting;

    [ObservableProperty] private UpdatePhase _updatePhase = UpdatePhase.Idle;
    [ObservableProperty] private int _updateProgress;

    public HomeViewModel Home { get; }
    public RegionsViewModel Regions { get; }
    public ServerListViewModel Servers { get; }
    public NewsViewModel News { get; }
    public AccountViewModel Account { get; }
    public ProfileViewModel Profile { get; }
    public SettingsViewModel Settings { get; }
    public AdminViewModel Admin { get; }

    /// <summary>Signed-in account name for the top bar, or null.</summary>
    public string? AccountName => _services.Accounts.Active?.Username ?? _services.Accounts.Accounts.FirstOrDefault()?.Username;

    public string VersionLine =>
        $"ЛАУНЧЕР {DarkHaven.Launcher.LauncherInfo.Version}   ·   ДВИЖОК Robust {EngineVersionHint}";

    private const string EngineVersionHint = "в комплекте";

    public MainWindowViewModel(AppServices services)
    {
        _services = services;
        Home = new HomeViewModel(services, Connect, () => Page = NavPage.Regions, () => Page = NavPage.Servers);
        Regions = new RegionsViewModel(services, Connect);
        Servers = new ServerListViewModel(services, Connect);
        News = new NewsViewModel();
        Account = new AccountViewModel(services);
        Profile = new ProfileViewModel(services, () => Page = NavPage.Account);
        Settings = new SettingsViewModel(services);
        Admin = new AdminViewModel();

        // First run with no saved account: land on the login screen instead of the map.
        if (services.Settings.GetConfig("SeenWelcome") != "true")
        {
            services.Settings.SetConfig("SeenWelcome", "true");
            if (services.Accounts.Accounts.Count == 0)
                _page = NavPage.Account;
        }

        _current = _page == NavPage.Account ? Account : Regions;

        _ = CheckForUpdatesAsync();
    }

    // --- Self-update banner ---

    public bool ShowUpdateBanner =>
        UpdatePhase is UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.ReadyToRestart or UpdatePhase.Failed;

    public string UpdateBannerText => UpdatePhase switch
    {
        UpdatePhase.Available => $"Доступно обновление лаунчера — {_services.Updater.PendingVersion}",
        UpdatePhase.Downloading => $"Загрузка обновления… {UpdateProgress}%",
        UpdatePhase.ReadyToRestart => "Обновление готово. Перезапустите лаунчер, чтобы применить.",
        UpdatePhase.Failed => "Не удалось обновить лаунчер. Попробуйте позже.",
        _ => "",
    };

    public bool CanStartUpdate => UpdatePhase == UpdatePhase.Available;
    public bool CanRestartForUpdate => UpdatePhase == UpdatePhase.ReadyToRestart;

    partial void OnUpdatePhaseChanged(UpdatePhase value)
    {
        OnPropertyChanged(nameof(ShowUpdateBanner));
        OnPropertyChanged(nameof(UpdateBannerText));
        OnPropertyChanged(nameof(CanStartUpdate));
        OnPropertyChanged(nameof(CanRestartForUpdate));
    }

    partial void OnUpdateProgressChanged(int value) => OnPropertyChanged(nameof(UpdateBannerText));

    public async Task CheckForUpdatesAsync()
    {
        if (!_services.Updater.Supported)
            return;

        try
        {
            UpdatePhase = UpdatePhase.Checking;
            UpdatePhase = await _services.Updater.CheckAsync() ? UpdatePhase.Available : UpdatePhase.UpToDate;
        }
        catch
        {
            UpdatePhase = UpdatePhase.Idle;
        }
    }

    [RelayCommand]
    private async Task StartUpdate()
    {
        try
        {
            UpdatePhase = UpdatePhase.Downloading;
            await _services.Updater.DownloadAsync(p => UpdateProgress = p);
            UpdatePhase = UpdatePhase.ReadyToRestart;
        }
        catch
        {
            UpdatePhase = UpdatePhase.Failed;
        }
    }

    [RelayCommand]
    private void RestartForUpdate() => _services.Updater.ApplyAndRestart();

    partial void OnPageChanged(NavPage value)
    {
        if (value == NavPage.Home)
            Home.Reload();
        else if (value == NavPage.Profile)
            Profile.Reload();

        Current = value switch
        {
            NavPage.Home => Home,
            NavPage.Regions => Regions,
            NavPage.Servers => Servers,
            NavPage.News => News,
            NavPage.Settings => Settings,
            NavPage.Admin => Admin,
            NavPage.Account => Account,
            NavPage.Profile => Profile,
            _ => Regions,
        };
    }

    [RelayCommand] private void Navigate(NavPage page) => Page = page;
    [RelayCommand] private void OpenAccount() => Page = NavPage.Profile;
    [RelayCommand] private void OpenDiscord() => OpenUrl("https://discord.gg/");
    [RelayCommand] private void OpenSite() => OpenUrl("https://spacestation14.com/");

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    public void ConnectToAddress(string address) => Connect(new ServerEntry(address));

    private void Connect(ServerEntry server)
    {
        if (Connecting is { IsBusy: true })
            return;

        var vm = new ConnectingViewModel(_services, server);
        vm.Finished += () => Connecting = null;
        Connecting = vm;
        vm.Start();
    }
}
