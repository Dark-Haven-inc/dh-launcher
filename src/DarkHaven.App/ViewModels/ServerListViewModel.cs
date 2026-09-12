using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher.Servers;
using Serilog;

namespace DarkHaven.App.ViewModels;

public partial class ServerListViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly Action<ServerEntry> _connect;
    private readonly ServerFilter _filter = new();
    private HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);

    // Typing re-filters the whole list and rebuilds the map layout — worth waiting for a short
    // pause in typing instead of doing that on every keystroke (same idea as the reference SS14
    // launcher's own search throttle).
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(200) };

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _hideEmpty;
    [ObservableProperty] private bool _hideFull;
    [ObservableProperty] private bool _hide18Plus;
    [ObservableProperty] private bool _favoritesOnly;
    [ObservableProperty] private bool _sortByName;
    [ObservableProperty] private bool _rpNone;
    [ObservableProperty] private bool _rpLow;
    [ObservableProperty] private bool _rpMed;
    [ObservableProperty] private bool _rpHigh;
    [ObservableProperty] private bool _mapView;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string _directAddress = "";
    [ObservableProperty] private ServerRowViewModel? _selectedServer;

    /// <summary>Flat list — every row, for counts and favourites lookups, and the grouped list view.</summary>
    public ObservableCollection<ServerRowViewModel> Servers { get; } = [];

    /// <summary>The same servers grouped by network — sections in the grouped list view.</summary>
    public ObservableCollection<ServerGroupViewModel> Groups { get; } = [];

    /// <summary>Only the servers actually placed on the map (a big network is capped — see
    /// <see cref="MaxDotsPerRegion"/>) — this is the "СЕКТОР"-style map's item source: one dot per
    /// server, connected to its siblings, clustered under its network's floating name.</summary>
    public ObservableCollection<ServerRowViewModel> MapNodes { get; } = [];

    public ServerListViewModel(AppServices services, Action<ServerEntry> connect)
    {
        _services = services;
        _connect = connect;

        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); ApplyFilter(); };

        // Every filter below sticks across launches — a player's own choices shouldn't reset every
        // time they reopen the launcher. Hide18Plus is the one with a non-off default: new/regular
        // players shouldn't see 18+ tags before they've actively chosen to, but if someone explicitly
        // turns it off, that choice is remembered too, same as all the others.
        _hideEmpty = Cfg("HideEmpty", false);
        _hideFull = Cfg("HideFull", false);
        _hide18Plus = Cfg("Hide18Plus", true);
        _favoritesOnly = Cfg("FavoritesOnly", false);
        _sortByName = Cfg("SortByName", false);
        _rpNone = Cfg("RpNone", false);
        _rpLow = Cfg("RpLow", false);
        _rpMed = Cfg("RpMed", false);
        _rpHigh = Cfg("RpHigh", false);

        _filter.HideEmpty = _hideEmpty;
        _filter.HideFull = _hideFull;
        _filter.Hide18Plus = _hide18Plus;
        _filter.Sort = _sortByName ? ServerSort.Name : ServerSort.Players;
        SyncRolePlaySet();
    }

    private bool Cfg(string key, bool dflt) => _services.Settings.GetConfig(key) is { } v ? v == "true" : dflt;
    private void SetCfg(string key, bool v) => _services.Settings.SetConfig(key, v ? "true" : "false");

    partial void OnSearchChanged(string value) { _searchDebounce.Stop(); _searchDebounce.Start(); }
    partial void OnHideEmptyChanged(bool value) { _filter.HideEmpty = value; SetCfg("HideEmpty", value); ApplyFilter(); }
    partial void OnHideFullChanged(bool value) { _filter.HideFull = value; SetCfg("HideFull", value); ApplyFilter(); }
    partial void OnHide18PlusChanged(bool value) { _filter.Hide18Plus = value; SetCfg("Hide18Plus", value); ApplyFilter(); }
    partial void OnFavoritesOnlyChanged(bool value) { SetCfg("FavoritesOnly", value); ApplyFilter(); }
    partial void OnSortByNameChanged(bool value)
    {
        _filter.Sort = value ? ServerSort.Name : ServerSort.Players;
        SetCfg("SortByName", value);
        ApplyFilter();
    }
    partial void OnRpNoneChanged(bool value) { SetCfg("RpNone", value); SyncRolePlaySet(); }
    partial void OnRpLowChanged(bool value) { SetCfg("RpLow", value); SyncRolePlaySet(); }
    partial void OnRpMedChanged(bool value) { SetCfg("RpMed", value); SyncRolePlaySet(); }
    partial void OnRpHighChanged(bool value) { SetCfg("RpHigh", value); SyncRolePlaySet(); }

    /// <summary>Empty = no RP filtering at all (show every level) — same "off by default, only
    /// narrows once picked" shape as every other filter here. ServerFilter.RolePlay already existed
    /// and already matched against each server's "rp:none/low/med/high" tag; only the UI to drive it
    /// was missing.</summary>
    private void SyncRolePlaySet()
    {
        _filter.RolePlay.Clear();
        if (RpNone) _filter.RolePlay.Add("none");
        if (RpLow) _filter.RolePlay.Add("low");
        if (RpMed) _filter.RolePlay.Add("med");
        if (RpHigh) _filter.RolePlay.Add("high");
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
            await _services.ServerList.RefreshAsync();
            TotalCount = _services.ServerList.Servers.Count;
            Error = _services.ServerList.StaleSince is { } t
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
            _connect(new ServerEntry(addr));
    }

    /// <summary>Called by the map when a server dot is clicked.</summary>
    public void SelectFromMap(ServerRowViewModel row) => SelectedServer = row;

    private void LoadFavorites()
    {
        try { _favorites = _services.Settings.GetFavorites().Select(f => f.Address).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch { _favorites = new(StringComparer.OrdinalIgnoreCase); }
    }

    private void ApplyFilter()
    {
        var hardFiltered = _filter.Apply(_services.ServerList.Servers);
        if (FavoritesOnly)
            hardFiltered = hardFiltered.Where(s => _favorites.Contains(s.Address));

        var groups = NetworkGrouping.Group(hardFiltered);
        var search = Search.Trim();
        var keepSelected = SelectedServer?.Address;

        Servers.Clear();
        Groups.Clear();
        MapNodes.Clear();

        var regions = new List<(string Label, List<ServerRowViewModel> MapRows)>();
        foreach (var g in groups)
        {
            // typing a network's name (e.g. "corvax") keeps the whole network; typing a server's own
            // name/address narrows down to just that server, same as before grouping existed.
            IEnumerable<ServerEntry> members = g.Servers;
            if (search.Length > 0 && !g.Label.Contains(search, StringComparison.OrdinalIgnoreCase))
                members = members.Where(s => s.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                                             || s.Address.Contains(search, StringComparison.OrdinalIgnoreCase));

            var rows = members.Select(e => new ServerRowViewModel(_services, e, _connect)).ToList();
            if (rows.Count == 0) continue;

            Groups.Add(new ServerGroupViewModel(g.Label, rows));
            foreach (var r in rows) Servers.Add(r);

            // A network with dozens of members (mainly "Другие сервера") would just be noise as dots —
            // cap what actually gets placed on the map to its busiest few; every member still shows up
            // in the plain list regardless.
            var mapRows = rows.OrderByDescending(r => r.Players).Take(MaxDotsPerRegion).ToList();
            foreach (var r in mapRows) r.RegionLabel = g.Label;
            regions.Add((g.Label, mapRows));
        }

        LayoutMap(regions.OrderBy(r => r.Label, StringComparer.OrdinalIgnoreCase).ToList());

        SelectedServer = MapNodes.FirstOrDefault(s => s.Address == keepSelected) ?? MapNodes.FirstOrDefault();
    }

    private const int MaxDotsPerRegion = 9;

    /// <summary>Spreads each network's centre across the whole disk (sunflower spiral + a relaxation
    /// pass enforcing a real minimum gap — see the earlier "далековато" pass), then arranges that
    /// network's own servers as a small connected loop around its centre, like the systems inside one
    /// region of a galaxy map.</summary>
    private void LayoutMap(IReadOnlyList<(string Label, List<ServerRowViewModel> MapRows)> regions)
    {
        if (regions.Count == 0) return;

        const double outerR = 0.46;
        const double goldenAngle = 2.39996323; // ~137.5°, the phyllotaxis constant
        var centers = new (double X, double Y)[regions.Count];
        for (var i = 0; i < regions.Count; i++)
        {
            var r = regions.Count == 1 ? 0 : outerR * Math.Sqrt((i + 0.5) / regions.Count);
            var angle = i * goldenAngle;
            centers[i] = (0.5 + Math.Cos(angle) * r, 0.5 + Math.Sin(angle) * r);
        }

        const double minGap = 0.40;
        const double maxR = 0.48;
        for (var pass = 0; pass < 60; pass++)
        {
            var moved = false;
            for (var i = 0; i < centers.Length; i++)
            for (var j = i + 1; j < centers.Length; j++)
            {
                var dx = centers[j].X - centers[i].X;
                var dy = centers[j].Y - centers[i].Y;
                var d = Math.Sqrt(dx * dx + dy * dy);
                if (d >= minGap || d < 1e-6) continue;
                var push = (minGap - d) / 2;
                var ux = dx / d;
                var uy = dy / d;
                centers[i] = (centers[i].X - ux * push, centers[i].Y - uy * push);
                centers[j] = (centers[j].X + ux * push, centers[j].Y + uy * push);
                moved = true;
            }
            if (!moved) break;
        }
        for (var i = 0; i < centers.Length; i++)
        {
            var dx = centers[i].X - 0.5;
            var dy = centers[i].Y - 0.5;
            var r = Math.Sqrt(dx * dx + dy * dy);
            if (r <= maxR) continue;
            centers[i] = (0.5 + dx / r * maxR, 0.5 + dy / r * maxR);
        }

        for (var i = 0; i < regions.Count; i++)
        {
            var (cx, cy) = centers[i];
            var rows = regions[i].MapRows;
            var localR = rows.Count <= 1 ? 0 : 0.05 + 0.007 * rows.Count;
            for (var k = 0; k < rows.Count; k++)
            {
                var angle = rows.Count == 1 ? 0 : k / (double)rows.Count * Math.Tau;
                rows[k].X = cx + Math.Cos(angle) * localR;
                rows[k].Y = cy + Math.Sin(angle) * localR;
                rows[k].Neighbours = rows.Count <= 1
                    ? []
                    : [rows[(k - 1 + rows.Count) % rows.Count].Name, rows[(k + 1) % rows.Count].Name];
                MapNodes.Add(rows[k]);
            }
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
    public bool IsMisc => Label == NetworkGrouping.MiscLabel;

    public string SummaryLine => TotalPlayers > 0
        ? $"{Count} · {TotalPlayers} игроков"
        : $"{Count}";
}
