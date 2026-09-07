using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher.Servers;

namespace DarkHaven.App.ViewModels;

public enum NavPage { Regions, AllServers, Account, Settings }

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly AppServices _services;

    [ObservableProperty] private NavPage _page = NavPage.Regions;
    [ObservableProperty] private ViewModelBase _current;
    [ObservableProperty] private ConnectingViewModel? _connecting;

    public RegionsViewModel Regions { get; }
    public ServerListViewModel AllServers { get; }
    public AccountViewModel Account { get; }
    public SettingsViewModel Settings { get; }

    public MainWindowViewModel(AppServices services)
    {
        _services = services;
        Regions = new RegionsViewModel(services, Connect);
        AllServers = new ServerListViewModel(services, Connect);
        Account = new AccountViewModel(services);
        Settings = new SettingsViewModel(services);
        _current = Regions;
    }

    partial void OnPageChanged(NavPage value)
    {
        Current = value switch
        {
            NavPage.Regions => Regions,
            NavPage.AllServers => AllServers,
            NavPage.Account => Account,
            NavPage.Settings => Settings,
            _ => Regions,
        };
    }

    [RelayCommand] private void Navigate(NavPage page) => Page = page;

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
