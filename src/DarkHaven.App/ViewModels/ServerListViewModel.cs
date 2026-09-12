using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher.Servers;
using Serilog;

namespace DarkHaven.App.ViewModels;

public partial class ServerListViewModel(AppServices services, Action<ServerEntry> connect) : ViewModelBase
{
    private readonly ServerFilter _filter = new();
    private HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _hideEmpty;
    [ObservableProperty] private bool _hideFull;
    [ObservableProperty] private bool _hide18Plus;
    [ObservableProperty] private bool _favoritesOnly;
    [ObservableProperty] private bool _sortByName;
    [ObservableProperty] private bool _mapView;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string _directAddress = "";

    /// <summary>Flat list — used as the galaxy map's ItemsSource (it clusters by <c>NetworkLabel</c> itself).</summary>
    public ObservableCollection<ServerRowViewModel> Servers { get; } = [];

    /// <summary>The same servers, grouped by network for the list view — see <see cref="NetworkGrouping"/>.</summary>
    public ObservableCollection<ServerGroupViewModel> Groups { get; } = [];

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnHideEmptyChanged(bool value) { _filter.HideEmpty = value; ApplyFilter(); }
    partial void OnHideFullChanged(bool value) { _filter.HideFull = value; ApplyFilter(); }
    partial void OnHide18PlusChanged(bool value) { _filter.Hide18Plus = value; ApplyFilter(); }
    partial void OnFavoritesOnlyChanged(bool value) => ApplyFilter();
    partial void OnSortByNameChanged(bool value)
    {
        _filter.Sort = value ? ServerSort.Name : ServerSort.Players;
        ApplyFilter();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        Error = null;
        try
        {
            LoadFavorites();
            await services.ServerList.RefreshAsync();
            TotalCount = services.ServerList.Servers.Count;
            Error = services.ServerList.StaleSince is { } t
                ? $"Хаб недоступен — список от {t.ToLocalTime():dd.MM HH:mm}."
                : null;
            ApplyFilter();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to refresh server list");
            Error = "Не удалось связаться с хабом.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ConnectDirect()
    {
        var addr = DirectAddress.Trim();
        if (addr.Length > 0)
            connect(new ServerEntry(addr));
    }

    /// <summary>Called by the galaxy map when a star is clicked.</summary>
    public void ConnectRow(ServerRowViewModel row) => connect(row.Entry);

    private void LoadFavorites()
    {
        try { _favorites = services.Settings.GetFavorites().Select(f => f.Address).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch { _favorites = new(StringComparer.OrdinalIgnoreCase); }
    }

    private void ApplyFilter()
    {
        var hardFiltered = _filter.Apply(services.ServerList.Servers);
        if (FavoritesOnly)
            hardFiltered = hardFiltered.Where(s => _favorites.Contains(s.Address));

        var groups = NetworkGrouping.Group(hardFiltered);
        var search = Search.Trim();

        Servers.Clear();
        Groups.Clear();
        foreach (var g in groups)
        {
            // typing a network's name (e.g. "corvax") keeps the whole network; typing a server's own
            // name/address narrows down to just that server, same as before grouping existed.
            IEnumerable<ServerEntry> members = g.Servers;
            if (search.Length > 0 && !g.Label.Contains(search, StringComparison.OrdinalIgnoreCase))
                members = members.Where(s => s.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                                             || s.Address.Contains(search, StringComparison.OrdinalIgnoreCase));

            var rows = members.Select(e => new ServerRowViewModel(services, e, connect, g.Label)).ToList();
            if (rows.Count == 0) continue;

            Groups.Add(new ServerGroupViewModel(g.Label, rows));
            foreach (var r in rows) Servers.Add(r);
        }
    }
}

/// <summary>One network section in the grouped РУхаб list — e.g. "Corvax" with its shards underneath.</summary>
public sealed class ServerGroupViewModel(string label, IReadOnlyList<ServerRowViewModel> servers)
{
    public string Label { get; } = label;
    public IReadOnlyList<ServerRowViewModel> Servers { get; } = servers;
    public int Count => Servers.Count;
    public int TotalPlayers => Servers.Sum(s => s.Players);
    public bool IsMisc => Label == "Другие сервера";

    public string SummaryLine => TotalPlayers > 0
        ? $"{Count} · {TotalPlayers} игроков"
        : $"{Count}";
}
