using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher.Servers;
using DarkHaven.Launcher.Update;

namespace DarkHaven.App.ViewModels;

public enum NavPage { Home, Regions, Servers, Monitoring, News, Settings, Admin, Account, Profile, Bans }

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly AppServices _services;

    [ObservableProperty] private NavPage _page = NavPage.Regions;
    [ObservableProperty] private ViewModelBase _current;
    [ObservableProperty] private ConnectingViewModel? _connecting;

    [ObservableProperty] private UpdatePhase _updatePhase = UpdatePhase.Idle;
    [ObservableProperty] private int _updateProgress;

    [ObservableProperty] private string? _regionOnlineName;
    private string? _regionOnlineAddress;
    // The same banner also says "a slot opened" when that happens mid-game (see SlotFreed below).
    private bool _regionBannerIsSlot;

    [ObservableProperty] private string? _slotWaitName;

    public HomeViewModel Home { get; }
    public RegionsViewModel Regions { get; }
    public ServerListViewModel Servers { get; }
    public NewsViewModel News { get; }
    public AccountViewModel Account { get; }
    public ProfileViewModel Profile { get; }
    public SettingsViewModel Settings { get; }
    public AdminViewModel Admin { get; }
    public NotificationsViewModel Notifications { get; }
    public MonitoringViewModel Monitoring { get; }
    /// <summary>The public ban list, opened from a region's card (not a bottom tab).</summary>
    public BanListViewModel Bans { get; }

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
        News = new NewsViewModel(services);
        Account = new AccountViewModel(services);
        Profile = new ProfileViewModel(services, () => Page = NavPage.Account);
        Settings = new SettingsViewModel(services);
        Admin = new AdminViewModel(services);
        Notifications = new NotificationsViewModel(services);
        Monitoring = new MonitoringViewModel(services, Connect);
        Bans = new BanListViewModel(services, back: () => Page = NavPage.Regions);
        Regions.OpenBans = title =>
        {
            Page = NavPage.Bans;
            Bans.Open(title);
        };

        _services.RegionWatch.CameOnline += (name, address) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _regionOnlineAddress = address;
                _regionBannerIsSlot = false;
                RegionOnlineName = name;
                App.AlertUser();
            });

        // "Ждать свободного места": keep the banner and the button on whichever screen is open in step.
        _services.SlotWatch.Changed += () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                SlotWaitName = _services.SlotWatch.IsWatching ? _services.SlotWatch.Name : null;
                Regions.Selected?.RefreshSlot();
                Servers.SelectedServer?.RefreshSlot();
            });

        // A slot opened. Take it straight away — on a busy server it's gone again in seconds, long
        // before anyone notices a blinking taskbar. Unless the player is already in a game or halfway
        // into another connect: then just say so, and let them decide.
        _services.SlotWatch.SlotFreed += (name, address) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                App.AlertUser();
                if (_services.CurrentGameAddress is null && Connecting is null)
                {
                    Connect(new ServerEntry(address) { Name = name });
                }
                else
                {
                    _regionOnlineAddress = address;
                    _regionBannerIsSlot = true;
                    RegionOnlineName = name;
                    OnPropertyChanged(nameof(RegionBannerText));
                }
            });

        // AccountName is a plain computed getter (Accounts.Active isn't observable on its own), so the
        // top bar would otherwise keep showing whatever it first bound to (usually "Гость") forever —
        // even after a real login — unless something explicitly tells the binding to re-read it.
        // PlatformSessionChanged already fires on every login/logout/account-switch (see
        // AppServices.SignInToPlatformAsync), so it doubles as "the active account may have changed".
        _services.PlatformSessionChanged += () => OnPropertyChanged(nameof(AccountName));

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

    // --- "watched region is back online" banner ---

    public bool ShowRegionBanner => RegionOnlineName is not null;
    public string RegionBannerText => _regionBannerIsSlot
        ? $"🟢  На {RegionOnlineName} освободилось место"
        : $"🟢  {RegionOnlineName} снова онлайн";

    // RegionBannerText too: it's computed, and without this the banner kept whatever it first read —
    // "🟢   снова онлайн", with no region name in it.
    partial void OnRegionOnlineNameChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowRegionBanner));
        OnPropertyChanged(nameof(RegionBannerText));
    }

    // --- "waiting for a free slot" banner ---

    public bool ShowSlotWaitBanner => SlotWaitName is not null;
    public string SlotWaitBannerText => $"⏳  Ждём место на {SlotWaitName} — подключим сами, как только освободится";

    partial void OnSlotWaitNameChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowSlotWaitBanner));
        OnPropertyChanged(nameof(SlotWaitBannerText));
    }

    [RelayCommand]
    private void CancelSlotWait() => _services.SlotWatch.Stop();

    [RelayCommand]
    private void ConnectOnlineRegion()
    {
        var addr = _regionOnlineAddress;
        RegionOnlineName = null;
        if (addr is not null)
            ConnectToAddress(addr);
    }

    [RelayCommand]
    private void DismissRegionBanner() => RegionOnlineName = null;

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
        // МОНИТОРИНГ polls servers every few seconds — only while it's on screen.
        if (value == NavPage.Monitoring)
            Monitoring.Activate();
        else
            Monitoring.Deactivate();

        if (value == NavPage.Home)
            Home.Reload();
        else if (value == NavPage.Profile)
            Profile.Reload();
        else if (value == NavPage.News)
            _ = News.LoadAsync();
        else if (value == NavPage.Admin)
            _ = Admin.ReloadAsync();

        Current = value switch
        {
            NavPage.Home => Home,
            NavPage.Regions => Regions,
            NavPage.Servers => Servers,
            NavPage.Monitoring => Monitoring,
            NavPage.News => News,
            NavPage.Settings => Settings,
            NavPage.Admin => Admin,
            NavPage.Account => Account,
            NavPage.Profile => Profile,
            NavPage.Bans => Bans,
            _ => Regions,
        };
    }

    [RelayCommand] private void Navigate(NavPage page) => Page = page;
    [RelayCommand] private void OpenAccount() => Page = NavPage.Profile;
    [RelayCommand] private void OpenDiscord() => SafeUrl.Open("https://discord.gg/");
    [RelayCommand] private void OpenSite() => SafeUrl.Open("https://spacestation14.com/");

    public void ConnectToAddress(string address) => Connect(new ServerEntry(address));

    private void Connect(ServerEntry server)
    {
        if (Connecting is { IsBusy: true })
            return;

        var vm = new ConnectingViewModel(_services, server);
        // Guarded: a card that closes late must not take down a newer connection's overlay.
        vm.Finished += () => { if (Connecting == vm) Connecting = null; };
        // The game died right after launch, after this card had already stepped aside — bring it
        // back with the reason, unless the player is already busy connecting somewhere else.
        vm.Reopen += () =>
        {
            if (Connecting is null)
                Connecting = vm;
            App.AlertUser();
        };
        Connecting = vm;
        vm.Start();
    }
}
