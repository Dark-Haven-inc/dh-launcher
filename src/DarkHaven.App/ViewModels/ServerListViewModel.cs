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
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string _directAddress = "";

    public ObservableCollection<ServerRowViewModel> Servers { get; } = [];

    partial void OnSearchChanged(string value) { _filter.Search = value; ApplyFilter(); }
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

    private void LoadFavorites()
    {
        try { _favorites = services.Settings.GetFavorites().Select(f => f.Address).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch { _favorites = new(StringComparer.OrdinalIgnoreCase); }
    }

    private void ApplyFilter()
    {
        var rows = _filter.Apply(services.ServerList.Servers);
        if (FavoritesOnly)
            rows = rows.Where(s => _favorites.Contains(s.Address));

        Servers.Clear();
        foreach (var e in rows)
            Servers.Add(new ServerRowViewModel(services, e, connect));
    }
}
