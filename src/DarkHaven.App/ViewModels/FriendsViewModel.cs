using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Models;
using DarkHaven.Launcher.Servers;

namespace DarkHaven.App.ViewModels;

public partial class FriendItemViewModel(PlatformFriend f, AppServices services) : ViewModelBase
{
    private FriendProfileViewModel? _card;

    [ObservableProperty] private Bitmap? _avatar;
    public bool HasAvatar => Avatar is not null;
    partial void OnAvatarChanged(Bitmap? value) => OnPropertyChanged(nameof(HasAvatar));

    /// <summary>Their profile card — fetched the first time someone opens it.</summary>
    public FriendProfileViewModel Card => _card ??= new FriendProfileViewModel(services, f.UserId, f.Username, Status);

    public async Task LoadAvatarAsync() => Avatar = await services.Images.GetAsync(f.AvatarUrl);

    public int Id => f.Id;
    public string Username => f.Username;
    public string Initial => f.Username.Length > 0 ? f.Username[..1].ToUpperInvariant() : "?";
    public bool Online => f.Online;
    public string? ServerAddress => f.ServerAddress;
    public string? ServerName => f.ServerName;
    public bool IsPlaying => f.ServerAddress is not null;

    public string Status => IsPlaying
        ? $"играет: {f.ServerName ?? f.ServerAddress}"
        : f.Online ? "в лаунчере" : "не в сети";

    /// <summary>First click on ✕ arms it, the second one within a few seconds unfriends.</summary>
    [ObservableProperty] private bool _confirmingRemove;
    public string RemoveLabel => ConfirmingRemove ? "Удалить?" : "✕";
    partial void OnConfirmingRemoveChanged(bool value) => OnPropertyChanged(nameof(RemoveLabel));
}

/// <summary>
/// Friends on ГЛАВНАЯ: who's online, where they're playing, one click to join them. Lives entirely
/// on the platform (friendships + the presence heartbeat AppServices sends); hidden without it.
/// </summary>
public partial class FriendsViewModel : ViewModelBase
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly AppServices _services;
    private readonly Action<ServerEntry> _connect;
    private bool _refreshing;
    private bool _refreshAgain;

    public ObservableCollection<FriendItemViewModel> Friends { get; } = [];
    public ObservableCollection<FriendItemViewModel> Incoming { get; } = [];
    public ObservableCollection<FriendItemViewModel> Outgoing { get; } = [];

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private string _addName = "";
    [ObservableProperty] private string? _addStatus;
    [ObservableProperty] private bool _addFailed;

    public bool HasFriends => Friends.Count > 0;
    public bool HasIncoming => Incoming.Count > 0;
    public bool HasOutgoing => Outgoing.Count > 0;
    public bool IsEmpty => !HasFriends && !HasIncoming && !HasOutgoing;
    public bool HasAddStatus => AddStatus is not null;
    public string OnlineSummary => Friends.Count(f => f.Online) is var n and > 0 ? $"{n} в сети" : "";

    partial void OnAddStatusChanged(string? value) => OnPropertyChanged(nameof(HasAddStatus));

    public FriendsViewModel(AppServices services, Action<ServerEntry> connect)
    {
        _services = services;
        _connect = connect;

        _services.PlatformSessionChanged += () => Dispatcher.UIThread.Post(() => _ = RefreshAsync());

        var timer = new DispatcherTimer { Interval = PollInterval };
        timer.Tick += (_, _) => _ = RefreshAsync();
        timer.Start();

        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_refreshing)
        {
            // Something changed mid-poll (a request just sent) — run once more when this one ends,
            // instead of leaving the list stale until the next tick.
            _refreshAgain = true;
            return;
        }
        _refreshing = true;
        try
        {
            IsAvailable = _services.Platform.IsSignedIn;
            var list = IsAvailable ? await _services.Platform.GetFriendsAsync() : null;

            // A failed poll keeps what's on screen rather than blanking the list for a minute.
            if (IsAvailable && list is null)
                return;

            Fill(Friends, list?.Friends);
            Fill(Incoming, list?.IncomingRequests);
            Fill(Outgoing, list?.OutgoingRequests);

            OnPropertyChanged(nameof(HasFriends));
            OnPropertyChanged(nameof(HasIncoming));
            OnPropertyChanged(nameof(HasOutgoing));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(OnlineSummary));
        }
        finally
        {
            _refreshing = false;
        }

        if (_refreshAgain)
        {
            _refreshAgain = false;
            await RefreshAsync();
        }
    }

    private void Fill(ObservableCollection<FriendItemViewModel> target, PlatformFriend[]? source)
    {
        target.Clear();
        foreach (var f in source ?? [])
        {
            var item = new FriendItemViewModel(f, _services);
            target.Add(item);
            _ = item.LoadAvatarAsync();
        }
    }

    [RelayCommand]
    private async Task SendRequest()
    {
        var name = AddName.Trim();
        if (name.Length == 0)
            return;

        var (ok, message) = await _services.Platform.SendFriendRequestAsync(name);
        AddFailed = !ok;
        AddStatus = message;
        if (ok)
        {
            AddName = "";
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task Accept(FriendItemViewModel item)
    {
        if (await _services.Platform.AcceptFriendAsync(item.Id))
            await RefreshAsync();
    }

    /// <summary>Decline an incoming request or cancel an outgoing one — no confirmation, easy to redo.</summary>
    [RelayCommand]
    private async Task Dismiss(FriendItemViewModel item)
    {
        if (await _services.Platform.RemoveFriendAsync(item.Id))
            await RefreshAsync();
    }

    // Concurrent on purpose: the confirming click arrives while the first one is still waiting.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Unfriend(FriendItemViewModel item)
    {
        if (!item.ConfirmingRemove)
        {
            item.ConfirmingRemove = true;
            await Task.Delay(TimeSpan.FromSeconds(4));
            item.ConfirmingRemove = false;
            return;
        }

        if (await _services.Platform.RemoveFriendAsync(item.Id))
            await RefreshAsync();
    }

    [RelayCommand]
    private void Join(FriendItemViewModel item)
    {
        // The platform only stores ss14:// addresses, but this came over the network — check again
        // before handing it to the connect flow.
        if (item.ServerAddress is { } address && Ss14Address.TryParse(address, out _))
            _connect(new ServerEntry(address) { Name = item.ServerName });
    }
}
