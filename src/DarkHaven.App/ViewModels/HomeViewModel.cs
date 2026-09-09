using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher.Data;
using DarkHaven.Launcher.Servers;

namespace DarkHaven.App.ViewModels;

/// <summary>ГЛАВНАЯ — quick launch: continue, recent, favourites. Profile/playtime wait on the backend.</summary>
public partial class HomeViewModel(
    AppServices services, Action<ServerEntry> connect, Action goRegions, Action goServers) : ViewModelBase
{
    [ObservableProperty] private RecentServer? _continueServer;

    public ObservableCollection<RecentServer> Recent { get; } = [];
    public ObservableCollection<FavoriteServerEntry> Favorites { get; } = [];

    public string Greeting =>
        services.Accounts.Active is { } a ? $"С возвращением, {a.Username}" : "Dark Haven";

    public bool HasRecent => Recent.Count > 0;
    public bool HasFavorites => Favorites.Count > 0;
    public bool HasContinue => ContinueServer is not null;

    /// <summary>Re-read history from the DB. Called each time ГЛАВНАЯ is shown.</summary>
    public void Reload()
    {
        try
        {
            var recent = services.Settings.GetRecent();
            ContinueServer = recent.FirstOrDefault();

            Recent.Clear();
            foreach (var r in recent.Skip(1).Take(6))
                Recent.Add(r);

            Favorites.Clear();
            foreach (var f in services.Settings.GetFavorites())
                Favorites.Add(f);
        }
        catch { /* fresh DB / locked — show an empty dashboard */ }

        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(HasContinue));
    }

    partial void OnContinueServerChanged(RecentServer? value) => OnPropertyChanged(nameof(HasContinue));

    [RelayCommand] private void OpenRegions() => goRegions();
    [RelayCommand] private void OpenServers() => goServers();

    [RelayCommand]
    private void ConnectContinue()
    {
        if (ContinueServer is { } c) connect(new ServerEntry(c.Address));
    }

    [RelayCommand]
    private void ConnectRecent(RecentServer? r)
    {
        if (r is not null) connect(new ServerEntry(r.Address));
    }

    [RelayCommand]
    private void ConnectFavorite(FavoriteServerEntry? f)
    {
        if (f is not null) connect(new ServerEntry(f.Address));
    }
}

/// <summary>НОВОСТИ — needs a DH news endpoint / CMS (see project-dh-launcher-design).</summary>
public sealed class NewsViewModel : ViewModelBase
{
    public string Message =>
        "Лента новостей появится, когда будет готов бэкенд сообщества Dark Haven.";
}

/// <summary>АДМИН — needs a DH platform API (roles, launcher bans, per-region server control).</summary>
public sealed class AdminViewModel : ViewModelBase
{
    public string Message =>
        "Админ-панель лаунчера (роли, бан на лаунчер, управление регионами) требует API платформы Dark Haven.";
}
