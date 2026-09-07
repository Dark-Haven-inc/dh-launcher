using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher.Servers;

namespace DarkHaven.App.ViewModels;

public partial class ServerRowViewModel(ServerEntry entry, Action<ServerEntry> connect) : ViewModelBase
{
    public ServerEntry Entry { get; } = entry;

    public string Name => Entry.DisplayName;
    public string Address => Entry.Address;
    public string? Blurb => Entry.RegionBlurb;
    public bool IsRegion => Entry.IsDarkHavenRegion;

    public bool IsOnline => Entry.Reachability == ServerReachability.Online;
    public bool IsOffline => Entry.Reachability == ServerReachability.Offline;

    public string Population => Entry.SoftMaxPlayers > 0
        ? $"{Entry.Players} / {Entry.SoftMaxPlayers}"
        : Entry.Players.ToString();

    public string RoundInfo => Entry.RunLevel switch
    {
        Launcher.Models.RunLevel.InRound => Entry.Map is { } m ? $"In round · {m}" : "In round",
        Launcher.Models.RunLevel.PreRoundLobby => "Lobby",
        Launcher.Models.RunLevel.PostRound => "Round ending",
        _ => "",
    };

    public IEnumerable<string> VisibleTags =>
        Entry.Tags.Where(t => !t.StartsWith("region:", StringComparison.OrdinalIgnoreCase)).Take(4);

    [RelayCommand] private void Connect() => connect(Entry);

    public void Refresh()
    {
        OnPropertyChanged(string.Empty); // refresh all bindings
    }
}
