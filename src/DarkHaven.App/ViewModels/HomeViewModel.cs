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
        services.Accounts.Active is { } a ? $"С возвращением, {a.Username}" : "Frontier 15";

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
        OnPropertyChanged(nameof(Greeting));
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

/// <summary>One news card.</summary>
public sealed partial class NewsItemViewModel(DhNewsItem item)
{
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
                _ => d.ToString("d MMMM yyyy"),
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

    public ObservableCollection<NewsItemViewModel> Items { get; } = [];

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
