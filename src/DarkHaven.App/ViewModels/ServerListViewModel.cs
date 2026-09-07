using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher.Servers;
using Serilog;

namespace DarkHaven.App.ViewModels;

public partial class ServerListViewModel(AppServices services, Action<ServerEntry> connect) : ViewModelBase
{
    private readonly ServerFilter _filter = new();

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _hideEmpty;
    [ObservableProperty] private bool _hide18Plus;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string _directAddress = "";

    public ObservableCollection<ServerRowViewModel> Servers { get; } = [];

    partial void OnSearchChanged(string value) { _filter.Search = value; ApplyFilter(); }
    partial void OnHideEmptyChanged(bool value) { _filter.HideEmpty = value; ApplyFilter(); }
    partial void OnHide18PlusChanged(bool value) { _filter.Hide18Plus = value; ApplyFilter(); }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        Error = null;
        try
        {
            await services.ServerList.RefreshAsync();
            TotalCount = services.ServerList.Servers.Count;
            ApplyFilter();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to refresh server list");
            Error = "Could not reach the hub.";
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

    private void ApplyFilter()
    {
        Servers.Clear();
        foreach (var e in _filter.Apply(services.ServerList.Servers))
            Servers.Add(new ServerRowViewModel(e, connect));
    }
}
