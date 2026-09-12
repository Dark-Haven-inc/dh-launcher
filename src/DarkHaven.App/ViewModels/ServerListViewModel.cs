using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.App.Controls;
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
    [ObservableProperty] private ServerGroupViewModel? _selectedNetwork;

    /// <summary>Flat list — every row, for counts and favourites lookups.</summary>
    public ObservableCollection<ServerRowViewModel> Servers { get; } = [];

    /// <summary>The same servers grouped by network — this IS the "СЕКТОР"-style map's item source
    /// too (each group is one beacon), as well as the grouped list view's sections.</summary>
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

    /// <summary>Called by the map when a network beacon is clicked.</summary>
    public void SelectFromMap(ServerGroupViewModel group) => SelectedNetwork = group;

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
        var keepSelected = SelectedNetwork?.Label;

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

            var rows = members.Select(e => new ServerRowViewModel(services, e, connect)).ToList();
            if (rows.Count == 0) continue;

            Groups.Add(new ServerGroupViewModel(g.Label, rows));
            foreach (var r in rows) Servers.Add(r);
        }

        // The biggest real network anchors the map's centre (like ХЕЙВЕН does for our own sector);
        // everything else — including the "Другие сервера" catch-all — scatters by a stable hash of
        // its own label, so a given network sits in the same spot between refreshes.
        var real = Groups.Where(g => !g.IsMisc).ToList();
        var central = real.OrderByDescending(g => g.TotalPlayers).FirstOrDefault();
        foreach (var g in Groups)
        {
            if (ReferenceEquals(g, central))
            {
                g.IsCentral = true;
                g.X = 0.5;
                g.Y = 0.5;
            }
            else
            {
                (g.X, g.Y) = HashPosition(g.Label);
            }
        }

        SelectedNetwork = Groups.FirstOrDefault(g => g.Label == keepSelected) ?? central ?? Groups.FirstOrDefault();
    }

    private static (double X, double Y) HashPosition(string seed)
    {
        uint h = 2166136261;
        foreach (var c in seed) h = (h ^ c) * 16777619;
        var angle = (h & 0xFFFF) / 65535.0 * Math.Tau;
        var radius = 0.22 + 0.26 * Math.Sqrt(((h >> 16) & 0xFFFF) / 65535.0);
        return (0.5 + Math.Cos(angle) * radius, 0.5 + Math.Sin(angle) * radius);
    }
}

/// <summary>
/// One network — e.g. "Corvax" with its shards underneath. Doubles as a section in the grouped list
/// view AND as a beacon on the map (implements <see cref="IMapNode"/>), so the РУхаб map is built from
/// the exact same control as СЕКТОР FRONTIER 15 instead of a bespoke one: pick a network, see its
/// servers appear in the same "selected node" side panel, same as picking a region.
/// </summary>
public sealed class ServerGroupViewModel(string label, IReadOnlyList<ServerRowViewModel> servers) : IMapNode
{
    public string Label { get; } = label;
    public IReadOnlyList<ServerRowViewModel> Servers { get; } = servers;
    public int Count => Servers.Count;
    public int TotalPlayers => Servers.Sum(s => s.Players);
    public bool IsMisc => Label == "Другие сервера";

    public string SummaryLine => TotalPlayers > 0
        ? $"{Count} · {TotalPlayers} игроков"
        : $"{Count}";

    // --- IMapNode ---
    public string Name => Label;

    /// <summary>Shown both as the map's hover tooltip and atop the side panel — since the tooltip is
    /// the only info a player gets before actually clicking, it lists the busiest servers by name and
    /// player count directly, not just a one-line summary.</summary>
    public string Blurb
    {
        get
        {
            var head = IsMisc
                ? $"{Count} {Decl(Count, "сервер", "сервера", "серверов")} без общей сети."
                : $"{Count} {Decl(Count, "сервер", "сервера", "серверов")} · {TotalPlayers} {Decl(TotalPlayers, "игрок", "игрока", "игроков")} онлайн.";

            const int shown = 5;
            var top = Servers.OrderByDescending(s => s.Players).Take(shown).ToList();
            if (top.Count == 0) return head;

            var lines = top.Select(s => $"{(s.IsOnline ? s.Population : "офлайн")} — {s.Name}");
            var rest = Servers.Count - top.Count;
            var tail = rest > 0 ? $"\n…и ещё {rest} {Decl(rest, "сервер", "сервера", "серверов")}" : "";
            return head + "\n" + string.Join("\n", lines) + tail;
        }
    }

    private static string Decl(int n, string one, string few, string many)
    {
        var m = Math.Abs(n) % 100;
        if (m is >= 11 and <= 14) return many;
        return (m % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }

    public double X { get; set; }
    public double Y { get; set; }
    public bool IsCentral { get; set; }
    public bool IsOnline => Servers.Any(s => s.IsOnline);
    public bool IsOffline => !IsOnline;
    public bool IsQuarantine => false;
    public bool IsCurrent => false;
    public string Population => TotalPlayers.ToString();
    public IReadOnlyList<string> Neighbours => [];
}
