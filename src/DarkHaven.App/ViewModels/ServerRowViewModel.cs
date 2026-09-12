using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.App.Controls;
using DarkHaven.Launcher.Servers;

namespace DarkHaven.App.ViewModels;

/// <summary>
/// One server row — in the plain list, in a network's section, AND (implementing <see cref="IMapNode"/>)
/// as one of the small connected dots inside its network's region on the РУхаб map. Same object, same
/// "click it, see its description, hit Играть" behaviour everywhere it appears.
/// </summary>
public partial class ServerRowViewModel : ViewModelBase, IMapNode
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

    public bool IsOnline => Entry.Reachability == ServerReachability.Online;
    public bool IsOffline => Entry.Reachability == ServerReachability.Offline;
    public int Players => Entry.Players;

    [ObservableProperty] private bool _isFavorite;

    public string Population => Entry.SoftMaxPlayers > 0
        ? $"{Entry.Players} / {Entry.SoftMaxPlayers}"
        : Entry.Players.ToString();

    public string PingText => Entry.PingMs is { } p ? $"{p} мс" : "";

    public string StateText => IsOnline ? "ОНЛАЙН" : "офлайн";

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

    // --- IMapNode — only set/meaningful for the servers chosen to actually appear on the РУхаб map ---

    public double X { get; set; }
    public double Y { get; set; }
    public bool IsCentral { get; set; }
    public bool IsQuarantine => false;
    public bool IsCurrent => _services.CurrentGameAddress is { } a && string.Equals(a, Address, StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<string> Neighbours { get; set; } = [];
    public string? RegionLabel { get; set; }

    /// <summary>The map's hover tooltip and click description — the actual "descriptions when you
    /// click each one" the map is for, not just a name.</summary>
    public string Blurb
    {
        get
        {
            var status = IsOnline ? $"{Population} игроков онлайн." : "Сейчас офлайн.";
            var tags = VisibleTags.ToList();
            return tags.Count == 0 ? status : status + "\n" + string.Join(" · ", tags);
        }
    }
}
