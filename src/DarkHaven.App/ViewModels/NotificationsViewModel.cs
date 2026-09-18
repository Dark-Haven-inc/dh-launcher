using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher.Api;

namespace DarkHaven.App.ViewModels;

public sealed class NotificationItemViewModel(PlatformNotification n) : ViewModelBase
{
    public int Id => n.Id;
    public string Text => n.Text;

    public bool IsBan => n.Kind == "ban";
    public bool IsWarning => n.Kind == "warning";

    public string KindLabel => n.Kind switch
    {
        "ban" => "БАН",
        "warning" => "ПРЕДУПРЕЖДЕНИЕ",
        "news" => "ОБЪЯВЛЕНИЕ",
        _ => "УВЕДОМЛЕНИЕ",
    };

    public string When => Relative(n.CreatedAt, DateTimeOffset.Now);

    internal static string Relative(DateTimeOffset at, DateTimeOffset now)
    {
        var ago = now - at;
        if (ago < TimeSpan.FromMinutes(1)) return "только что";
        if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes} мин назад";
        if (ago < TimeSpan.FromDays(1)) return $"{(int)ago.TotalHours} ч назад";
        if (ago < TimeSpan.FromDays(2)) return "вчера";
        return at.ToLocalTime().ToString("dd.MM.yyyy");
    }
}

/// <summary>
/// The bell in the top bar: unread platform notifications — launcher bans, admin warnings and
/// announcements. Before this, announcements from the admin panel reached nobody: the platform
/// stored them, but nothing in the launcher ever showed them.
/// </summary>
public partial class NotificationsViewModel : ViewModelBase
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    // Every id ever shown this session, so a poll that briefly comes back empty (platform blip)
    // doesn't make old notifications look new and flash the taskbar again.
    private readonly HashSet<int> _everSeen = [];
    private bool _loadedOnce;
    private bool _refreshing;

    public ObservableCollection<NotificationItemViewModel> Items { get; } = [];

    /// <summary>Signed in to the platform — otherwise the bell stays hidden rather than dead.</summary>
    [ObservableProperty] private bool _isAvailable;

    public bool HasUnread => Items.Count > 0;
    public bool IsEmpty => Items.Count == 0;
    public string BadgeText => Items.Count > 9 ? "9+" : Items.Count.ToString();

    public NotificationsViewModel(AppServices services)
    {
        _services = services;

        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasUnread));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(BadgeText));
        };

        _services.PlatformSessionChanged += () => Dispatcher.UIThread.Post(() => _ = RefreshAsync());

        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();

        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            IsAvailable = _services.Platform.IsSignedIn;
            if (!IsAvailable)
            {
                Items.Clear();
                _everSeen.Clear();
                _loadedOnce = false;
                return;
            }

            var fresh = await _services.Platform.GetNotificationsAsync();
            var anyNew = fresh.Any(n => !_everSeen.Contains(n.Id));

            Items.Clear();
            foreach (var n in fresh)
            {
                Items.Add(new NotificationItemViewModel(n));
                _everSeen.Add(n.Id);
            }

            // The first load after sign-in is just "what's waiting", not news — only later arrivals
            // are worth pulling the player's attention to a launcher in the background.
            if (anyNew && _loadedOnce)
                App.AlertUser();
            _loadedOnce = true;
        }
        finally
        {
            _refreshing = false;
        }
    }

    [RelayCommand]
    private async Task MarkRead(NotificationItemViewModel item)
    {
        Items.Remove(item);
        await _services.Platform.MarkNotificationReadAsync(item.Id);
    }

    [RelayCommand]
    private async Task MarkAllRead()
    {
        var all = Items.ToList();
        Items.Clear();
        foreach (var item in all)
            await _services.Platform.MarkNotificationReadAsync(item.Id);
    }
}
