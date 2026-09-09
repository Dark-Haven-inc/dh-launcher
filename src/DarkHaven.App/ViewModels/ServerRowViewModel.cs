using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.App.Controls;
using DarkHaven.Launcher.Servers;

namespace DarkHaven.App.ViewModels;

public partial class ServerRowViewModel : ViewModelBase, IGalaxyNode
{
    private readonly AppServices _services;
    private readonly Action<ServerEntry> _connect;

    public ServerRowViewModel(AppServices services, ServerEntry entry, Action<ServerEntry> connect)
    {
        _services = services;
        _connect = connect;
        Entry = entry;
        _isFavorite = TryIsFavorite(services, entry.Address);
    }

    public ServerEntry Entry { get; }

    public string Name => Entry.DisplayName;
    public string Address => Entry.Address;
    public string? Blurb => Entry.RegionBlurb;
    public bool IsRegion => Entry.IsDarkHavenRegion;

    public bool IsOnline => Entry.Reachability == ServerReachability.Online;
    public bool IsOffline => Entry.Reachability == ServerReachability.Offline;
    public int Players => Entry.Players;

    [ObservableProperty] private bool _isFavorite;

    public string Population => Entry.SoftMaxPlayers > 0
        ? $"{Entry.Players} / {Entry.SoftMaxPlayers}"
        : Entry.Players.ToString();

    public string PingText => Entry.PingMs is { } p ? $"{p} мс" : "";

    public string RoundInfo => Entry.RunLevel switch
    {
        Launcher.Models.RunLevel.InRound => Entry.Map is { } m ? $"В раунде · {m}" : "В раунде",
        Launcher.Models.RunLevel.PreRoundLobby => "Лобби",
        Launcher.Models.RunLevel.PostRound => "Конец раунда",
        _ => "",
    };

    public IEnumerable<string> VisibleTags =>
        Entry.Tags.Where(t => !t.StartsWith("region:", StringComparison.OrdinalIgnoreCase)).Take(4);

    [RelayCommand] private void Connect() => _connect(Entry);

    [RelayCommand]
    private void ToggleFavorite()
    {
        try { IsFavorite = _services.Settings.ToggleFavorite(Entry.Address, Entry.DisplayName); }
        catch { /* non-fatal */ }
    }

    public void Refresh() => OnPropertyChanged(string.Empty);

    private static bool TryIsFavorite(AppServices services, string address)
    {
        try { return services.Settings.IsFavorite(address); }
        catch { return false; }
    }
}
