using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher.Data;
using DarkHaven.Launcher.Servers;
using DarkHaven.Launcher;

namespace DarkHaven.App.ViewModels;

/// <summary>A server on ГЛАВНАЯ (continue, favourites, recent) with whatever live status the region
/// and server lists already know about it. Unknown addresses just show no status.</summary>
public sealed class HomeServerRow
{
    public required string Name { get; init; }
    public required string Address { get; init; }
    public bool IsRegion { get; init; }

    public bool HasStatus { get; init; }
    public bool IsOnline { get; init; }
    public bool IsOffline { get; init; }
    public bool IsFull { get; init; }
    public bool IsQuarantine { get; init; }
    public string? Population { get; init; }
    public string? Round { get; init; }
    public string? Ping { get; init; }

    public bool HasPopulation => IsOnline && !string.IsNullOrEmpty(Population);
    public bool HasRound => IsOnline && !string.IsNullOrEmpty(Round);
    public bool HasPing => !string.IsNullOrEmpty(Ping) && Ping != "—";
}

/// <summary>A region in the strip under the hero; <see cref="IsHero"/> marks the one shown above it.</summary>
public sealed class HomeRegionItem(RegionNodeViewModel node, bool isHero)
{
    public RegionNodeViewModel Node { get; } = node;
    public bool IsHero { get; } = isHero;
}

/// <summary>ГЛАВНАЯ — quick launch: the server to continue on, the sector's regions, favourites, recent.</summary>
public partial class HomeViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly Action<ServerEntry> _connect;
    private readonly RegionsViewModel _regions;
    private readonly ServerListViewModel _servers;
    private readonly Action<RegionNodeViewModel> _showRegion;

    [ObservableProperty] private HomeServerRow? _hero;

    public FriendsViewModel Friends { get; }

    public ObservableCollection<HomeServerRow> Recent { get; } = [];
    public ObservableCollection<HomeServerRow> Favorites { get; } = [];
    public ObservableCollection<HomeRegionItem> RegionStrip { get; } = [];

    public bool HasRecent => Recent.Count > 0;
    public bool HasFavorites => Favorites.Count > 0;
    public bool HasHero => Hero is not null;
    public bool HasRegions => RegionStrip.Count > 0;

    public HomeViewModel(AppServices services, Action<ServerEntry> connect, RegionsViewModel regions,
        ServerListViewModel servers, Action<RegionNodeViewModel> showRegion)
    {
        _services = services;
        _connect = connect;
        _regions = regions;
        _servers = servers;
        _showRegion = showRegion;
        Friends = new FriendsViewModel(services, connect);

        // Live status arrives after the first paint; refresh the rows when it does.
        regions.Nodes.CollectionChanged += (_, _) => Reload();
        servers.Servers.CollectionChanged += (_, _) => Reload();
    }

    /// <summary>Re-read history from the DB and match it against live status. Called each time ГЛАВНАЯ is shown.</summary>
    public void Reload()
    {
        try
        {
            var recent = _services.Settings.GetRecent();
            var heroAddress = recent.FirstOrDefault()?.Address;
            var hero = recent.FirstOrDefault() is { } r0
                ? Row(r0.Name, r0.Address, r0.IsRegion)
                : _regions.Nodes.FirstOrDefault(n => n.IsCentral) is { } c ? Row(c.Name, c.Address, true) : null;
            Hero = hero;

            Recent.Clear();
            foreach (var r in recent.Take(6))
                Recent.Add(Row(r.Name, r.Address, r.IsRegion));

            Favorites.Clear();
            foreach (var f in _services.Settings.GetFavorites())
                Favorites.Add(Row(f.Name, f.Address, false));

            RegionStrip.Clear();
            foreach (var n in _regions.Nodes)
                RegionStrip.Add(new HomeRegionItem(n, hero is not null && Same(n.Address, hero.Address)));
        }
        catch { /* fresh DB / locked — show an empty dashboard */ }

        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(HasHero));
        OnPropertyChanged(nameof(HasRegions));
    }

    private static bool Same(string a, string b) => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private HomeServerRow Row(string name, string address, bool isRegion)
    {
        if (_regions.Nodes.FirstOrDefault(n => Same(n.Address, address)) is { } node)
        {
            return new HomeServerRow
            {
                Name = name, Address = address, IsRegion = true, HasStatus = true,
                IsOnline = node.IsOnline, IsOffline = node.IsOffline, IsFull = node.IsFull, IsQuarantine = node.IsQuarantine,
                Population = node.Population, Round = node.RoundText, Ping = node.Ping,
            };
        }
        if (_servers.Servers.FirstOrDefault(s => Same(s.Address, address)) is { } srv)
        {
            return new HomeServerRow
            {
                Name = name, Address = address, IsRegion = isRegion, HasStatus = true,
                IsOnline = srv.IsOnline, IsOffline = srv.IsOffline, IsFull = srv.IsFull,
                Population = srv.Population, Round = srv.RoundInfo, Ping = srv.PingText,
            };
        }
        return new HomeServerRow { Name = name, Address = address, IsRegion = isRegion };
    }

    partial void OnHeroChanged(HomeServerRow? value) => OnPropertyChanged(nameof(HasHero));

    [RelayCommand]
    private void ConnectHero()
    {
        if (Hero is { } h) _connect(new ServerEntry(h.Address));
    }

    [RelayCommand]
    private void ConnectRow(HomeServerRow? r)
    {
        if (r is not null) _connect(new ServerEntry(r.Address));
    }

    [RelayCommand]
    private void OpenRegion(HomeRegionItem? item)
    {
        if (item is not null) _showRegion(item.Node);
    }
}

/// <summary>One news card.</summary>
public sealed partial class NewsItemViewModel(DhNewsItem item) : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    /// <summary>The body split into paragraphs (blank-line separated).</summary>
    public IReadOnlyList<string> Paragraphs { get; } =
        item.Body.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public string TagCaps => item.Tag?.ToUpperInvariant() ?? "";

    public string Title => item.Title;
    public string Body => item.Body;
    public string? Tag => item.Tag;
    public bool HasTag => !string.IsNullOrWhiteSpace(item.Tag);
    public bool Pinned => item.Pinned;
    public string? Link => item.Link;
    public bool HasLink => !string.IsNullOrWhiteSpace(item.Link);

    public string DateText
    {
        get
        {
            if (item.ParsedDate is not { } d)
                return "";
            var days = DateOnly.FromDateTime(DateTime.Now).DayNumber - d.DayNumber;
            return days switch
            {
                <= 0 => "сегодня",
                1 => "вчера",
                < 7 => $"{days} дн. назад",
                < 31 => $"{days / 7} нед. назад",
                _ => RuText.Date(d),
            };
        }
    }

    [RelayCommand]
    private void Open() => SafeUrl.Open(item.Link);
}

/// <summary>НОВОСТИ — a static feed (bundled news.json + optional remote refresh). No backend.</summary>
public partial class NewsViewModel(AppServices services) : ViewModelBase
{
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _loaded;
    [ObservableProperty] private NewsItemViewModel? _selected;

    public ObservableCollection<NewsItemViewModel> Items { get; } = [];

    [RelayCommand]
    private void Select(NewsItemViewModel? item)
    {
        foreach (var i in Items) i.IsSelected = ReferenceEquals(i, item);
        Selected = item;
    }

    public bool IsEmpty => Loaded && Items.Count == 0;

    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            await services.News.LoadAsync();
            Items.Clear();
            foreach (var i in services.News.Items)
                Items.Add(new NewsItemViewModel(i));
            Select(Items.FirstOrDefault());
        }
        catch { /* keep whatever's already shown */ }
        finally
        {
            IsLoading = false;
            Loaded = true;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }
}
