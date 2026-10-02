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
        "launch" => "ЗАПУСК",
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
    // The launcher's own notices (a favourite server about to start), not the platform's: negative ids, kept until the
    // player closes them, shown signed in or not.
    private readonly List<PlatformNotification> _local = [];
    private int _nextLocalId = -1;
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
            var signedIn = _services.Platform.IsSignedIn;
            IsAvailable = signedIn || _local.Count > 0;
            if (!signedIn)
            {
                Items.Clear();
                AddLocalItems();
                _everSeen.Clear();
                _loadedOnce = false;
                return;
            }

            var fresh = await _services.Platform.GetNotificationsAsync();
            var anyNew = fresh.Any(n => !_everSeen.Contains(n.Id));

            Items.Clear();
            AddLocalItems();
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

    /// <summary>A notice from the launcher itself, on top of the bell until the player closes it.</summary>
    public void AddLocal(string kind, string text)
    {
        var n = new PlatformNotification(_nextLocalId--, Guid.Empty, kind, text, DateTimeOffset.UtcNow, false);
        _local.Add(n);
        Items.Insert(0, new NotificationItemViewModel(n));
        IsAvailable = true;
        App.AlertUser();
    }

    private void AddLocalItems()
    {
        foreach (var n in Enumerable.Reverse(_local))
            Items.Add(new NotificationItemViewModel(n));
    }

    [RelayCommand]
    private async Task MarkRead(NotificationItemViewModel item)
    {
        Items.Remove(item);
        if (item.Id < 0)
        {
            _local.RemoveAll(n => n.Id == item.Id);
            IsAvailable = _services.Platform.IsSignedIn || _local.Count > 0;
            return;
        }
        await _services.Platform.MarkNotificationReadAsync(item.Id);
    }

    [RelayCommand]
    private async Task MarkAllRead()
    {
        var all = Items.ToList();
        Items.Clear();
        _local.Clear();
        IsAvailable = _services.Platform.IsSignedIn;
        foreach (var item in all.Where(i => i.Id > 0))
            await _services.Platform.MarkNotificationReadAsync(item.Id);
    }
}
