using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher.Servers;

namespace DarkHaven.App.ViewModels;

public enum NavPage { Home, Regions, Servers, News, Settings, Admin, Account }

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly AppServices _services;

    [ObservableProperty] private NavPage _page = NavPage.Regions;
    [ObservableProperty] private ViewModelBase _current;
    [ObservableProperty] private ConnectingViewModel? _connecting;

    public HomeViewModel Home { get; }
    public RegionsViewModel Regions { get; }
    public ServerListViewModel Servers { get; }
    public NewsViewModel News { get; }
    public AccountViewModel Account { get; }
    public SettingsViewModel Settings { get; }
    public AdminViewModel Admin { get; }

    /// <summary>Signed-in account name for the top bar, or null.</summary>
    public string? AccountName => _services.Accounts.Active?.Username ?? _services.Accounts.Accounts.FirstOrDefault()?.Username;

    public string VersionLine =>
        $"ЛАУНЧЕР {LauncherVersion}   ·   ДВИЖОК Robust {EngineVersionHint}";

    private const string LauncherVersion = "0.1.0";
    private const string EngineVersionHint = "bundled";

    public MainWindowViewModel(AppServices services)
    {
        _services = services;
        Home = new HomeViewModel(services, Connect, () => Page = NavPage.Regions, () => Page = NavPage.Servers);
        Regions = new RegionsViewModel(services, Connect);
        Servers = new ServerListViewModel(services, Connect);
        News = new NewsViewModel();
        Account = new AccountViewModel(services);
        Settings = new SettingsViewModel(services);
        Admin = new AdminViewModel();
        _current = Regions;
    }

    partial void OnPageChanged(NavPage value)
    {
        Current = value switch
        {
            NavPage.Home => Home,
            NavPage.Regions => Regions,
            NavPage.Servers => Servers,
            NavPage.News => News,
            NavPage.Settings => Settings,
            NavPage.Admin => Admin,
            NavPage.Account => Account,
            _ => Regions,
        };
    }

    [RelayCommand] private void Navigate(NavPage page) => Page = page;
    [RelayCommand] private void OpenAccount() => Page = NavPage.Account;
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
