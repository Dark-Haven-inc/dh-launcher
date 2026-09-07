using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher.Servers;
using Serilog;

namespace DarkHaven.App.ViewModels;

public partial class RegionsViewModel(AppServices services, Action<ServerEntry> connect) : ViewModelBase
{
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;

    public ObservableCollection<ServerRowViewModel> Regions { get; } = [];

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
            Regions.Clear();
            foreach (var e in entries)
                Regions.Add(new ServerRowViewModel(e, connect));
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to load regions");
            Error = "Could not load the Dark Haven sector.";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
