using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.App.Controls;
using DarkHaven.Launcher.Models;
using DarkHaven.Launcher.Servers;
using Serilog;

namespace DarkHaven.App.ViewModels;

/// <summary>A region node on the "КАРТА СЕТИ".</summary>
public partial class RegionNodeViewModel(AppServices services, ServerEntry entry, Action<RegionNodeViewModel> select)
    : ViewModelBase, IMapNode
{
    public ServerEntry Entry { get; } = entry;
    public string Name => Entry.DisplayName;
    public string Address => Entry.Address;
    public string Blurb => Entry.RegionBlurb ?? "";
    public bool IsCentral => Entry.RegionCentral;
    public double X => Entry.RegionX;
    public double Y => Entry.RegionY;
    public IReadOnlyList<string> Neighbours => Entry.RegionNeighbours;
    public string? RegionLabel => null;

    public bool IsQuarantine => Entry.RegionQuarantine;
    public bool IsOnline => !IsQuarantine && Entry.Reachability == ServerReachability.Online;
    public bool IsOffline => !IsQuarantine && Entry.Reachability == ServerReachability.Offline;
    public bool IsWaiting => !IsQuarantine && Entry.Reachability == ServerReachability.Unknown;

    public bool IsCurrent => services.CurrentGameAddress is { } a
        && string.Equals(a, Entry.Address, StringComparison.OrdinalIgnoreCase);

    /// <summary>Only an open region that answered its status poll can be joined.</summary>
    public bool CanConnect => IsOnline;

    /// <summary>A real region that's currently down — offer "поднимется — уведомить".</summary>
    public bool CanWatch => !IsQuarantine && !IsOnline && !string.IsNullOrWhiteSpace(Address);

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isWatched;

    public string WatchLabel => IsWatched ? "🔔 Уведомлю, когда поднимется" : "Уведомить, когда поднимется";

    partial void OnIsWatchedChanged(bool value) => OnPropertyChanged(nameof(WatchLabel));

    [RelayCommand]
    private void ToggleWatch()
    {
        IsWatched = !IsWatched;
        services.RegionWatch.SetWatched(Name, IsWatched);
    }

    public string Population => Entry.SoftMaxPlayers > 0 ? $"{Entry.Players}/{Entry.SoftMaxPlayers}" : Entry.Players.ToString();
    public string Ping => Entry.PingMs is { } p ? $"{p} мс" : "—";
    public string StateText => IsQuarantine ? "НА КАРАНТИНЕ" : Entry.Reachability switch
    {
        ServerReachability.Online => "ОНЛАЙН",
        ServerReachability.Offline => "офлайн",
        _ => "…",
    };
    public string RoundText => Entry.RunLevel switch
    {
        RunLevel.InRound => "раунд идёт",
        RunLevel.PreRoundLobby => "лобби",
        RunLevel.PostRound => "конец раунда",
        _ => "",
    };

    [RelayCommand] private void Select() => select(this);
}

public partial class RegionsViewModel(AppServices services, Action<ServerEntry> connect) : ViewModelBase
{
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private RegionNodeViewModel? _selected;
    [ObservableProperty] private int _totalPlayers;

    public ObservableCollection<RegionNodeViewModel> Nodes { get; } = [];
    public ObservableCollection<RegionNodeViewModel> SelectedNeighbours { get; } = [];

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        Error = null;
        try
        {
            await services.Regions.LoadAsync();
            var entries = await services.Regions.PollAsync();

            var keepSelectedName = Selected?.Name;
            Nodes.Clear();
            foreach (var e in entries)
                Nodes.Add(new RegionNodeViewModel(services, e, SelectNode)
                {
                    IsWatched = services.RegionWatch.IsWatched(e.DisplayName),
                });

            // Anything armed from a previous session: make sure the poll loop is running.
            services.RegionWatch.EnsureRunning();

            TotalPlayers = Nodes.Where(n => n.IsOnline).Sum(n => n.Entry.Players);

            SelectNode(Nodes.FirstOrDefault(n => n.Name == keepSelectedName)
                       ?? Nodes.FirstOrDefault(n => n.IsCentral)
                       ?? Nodes.FirstOrDefault(n => !n.IsQuarantine)
                       ?? Nodes.FirstOrDefault());
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to load regions");
            Error = "Не удалось загрузить сектор Frontier 15.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Called by the sector map when a node is clicked.</summary>
    public void SelectFromMap(RegionNodeViewModel node) => SelectNode(node);

    private void SelectNode(RegionNodeViewModel? node)
    {
        foreach (var n in Nodes) n.IsSelected = ReferenceEquals(n, node);
        Selected = node;

        SelectedNeighbours.Clear();
        if (node is null) return;
        foreach (var name in node.Neighbours)
        {
            var match = Nodes.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) SelectedNeighbours.Add(match);
        }
    }

    [RelayCommand]
    private void PlaySelected()
    {
        if (Selected is { CanConnect: true })
            connect(Selected.Entry);
    }
}
