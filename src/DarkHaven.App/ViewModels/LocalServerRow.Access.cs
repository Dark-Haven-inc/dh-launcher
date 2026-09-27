using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Local;

namespace DarkHaven.App.ViewModels;

/// <summary>Someone the owner let into their server, or who asked.</summary>
public sealed class LocalMemberRow(PlatformLocalMember m)
{
    public Guid UserId => m.UserId;
    public string Username => m.Username;
    public bool IsInvited => m.Status == "invited";
    public bool IsRequest => m.Status == "requested";
    public string StatusText => IsInvited ? "может зайти" : "просится";
    public string RemoveText => IsInvited ? "Убрать" : "Отказать";
}

/// <summary>
/// "Пускать приглашённых": while an open server runs, the platform hears about it every couple of
/// minutes, and the server's whitelist follows what the owner does here — the owner is on it from the
/// start, an invite or an accepted request adds a player, withdrawing one takes them off.
/// </summary>
public sealed partial class LocalServerRow
{
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMinutes(2);

    private DispatcherTimer? _report;
    private readonly HashSet<string> _allowed = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<LocalMemberRow> Members { get; } = [];

    [ObservableProperty] private bool _shared;
    [ObservableProperty] private string? _publicAddress;
    [ObservableProperty] private bool? _reachable;
    [ObservableProperty] private string? _accessError;
    [ObservableProperty] private string _inviteName = "";

    public bool ShowAccess => LocalServers.SharingEnabled && IsRunning && Profile.Shared;
    public bool SharingAvailable => LocalServers.SharingEnabled;
    public bool HasMembers => Members.Count > 0;
    public string SharedText => LocalServers.SharingEnabled && Profile.Shared ? " · открыт для приглашённых" : "";

    /// <summary>What the router and the outside world said, in one line for the owner.</summary>
    public string ReachText => (Host.Upnp, Reachable) switch
    {
        (_, true) => "✔ Сервер виден из интернета — приглашённые смогут зайти.",
        (UpnpState.Failed, _) => "✖ Роутер не открыл порт (UPnP выключен или не поддерживается). Откройте порт " +
                                 $"{Profile.Port} (TCP и UDP) в настройках роутера вручную.",
        (_, false) => "✖ Снаружи до сервера не достучаться. Возможно, у провайдера нет внешнего IP для вас или порт закрыт " +
                      "ещё одним роутером. Зайти смогут только игроки из вашей локальной сети.",
        (UpnpState.Forwarded, null) => "Роутер открыл порт, проверяем, видно ли сервер снаружи…",
        _ => "Проверяем, открыт ли порт…",
    };

    partial void OnSharedChanged(bool value)
    {
        SaveIf(() => Profile.Shared = value);
        OnPropertyChanged(nameof(SharedText));
    }

    partial void OnReachableChanged(bool? value) => OnPropertyChanged(nameof(ReachText));

    /// <summary>Called on every state change of the server: opens access when it comes up, closes it when it goes.</summary>
    private void AccessFollowState()
    {
        if (LocalServers.SharingEnabled && IsRunning && Profile.Shared && _report is null)
            BeginAccess();
        else if (!IsRunning && _report is not null)
            EndAccess();
        OnPropertyChanged(nameof(ShowAccess));
        OnPropertyChanged(nameof(ReachText));
    }

    private void BeginAccess()
    {
        _report = new DispatcherTimer { Interval = ReportEvery };
        _report.Tick += async (_, _) => await ReportAsync();
        _report.Start();

        // The whitelist has no exception for the host: without this the owner couldn't get into their own server.
        if (_services.Accounts.Active?.Username is { } me)
            AllowOnServer(me);
        _ = ReportAsync();
    }

    private void EndAccess()
    {
        _report?.Stop();
        _report = null;
        _allowed.Clear();
        Members.Clear();
        PublicAddress = null;
        Reachable = null;
        OnPropertyChanged(nameof(HasMembers));
        _ = _services.Platform.CloseLocalShareAsync(Profile.Id);
    }

    private async Task ReportAsync()
    {
        var players = await Host.PlayersAsync();
        var mode = Profile.Mode == LocalServerMode.Develop ? "develop" : "play";
        Show(await _services.Platform.ReportLocalShareAsync(Profile.Id, Profile.Name, Profile.Port, mode, players));
    }

    private void Show((PlatformLocalShare? Share, string? Error) result)
    {
        AccessError = result.Error;
        if (result.Share is not { } share)
            return;

        PublicAddress = share.Address;
        Reachable = share.Reachable;
        Members.Clear();
        foreach (var m in share.Members.OrderBy(m => m.Status == "invited").ThenBy(m => m.Username, StringComparer.OrdinalIgnoreCase))
        {
            Members.Add(new LocalMemberRow(m));
            if (m.Status == "invited")
                AllowOnServer(m.Username);
        }
        OnPropertyChanged(nameof(HasMembers));
    }

    private void AllowOnServer(string username)
    {
        if (_allowed.Add(username))
            Host.Allow(username);
    }

    [RelayCommand]
    private async Task Invite()
    {
        var name = InviteName.Trim();
        if (name.Length == 0)
            return;
        var result = await _services.Platform.InviteToLocalAsync(Profile.Id, name);
        Show(result);
        if (result.Error is null)
            InviteName = "";
    }

    [RelayCommand]
    private async Task AcceptMember(LocalMemberRow member) =>
        Show(await _services.Platform.AcceptLocalMemberAsync(Profile.Id, member.UserId));

    [RelayCommand]
    private async Task RemoveMember(LocalMemberRow member)
    {
        var result = await _services.Platform.RemoveLocalMemberAsync(Profile.Id, member.UserId);
        if (result.Error is null && member.IsInvited && _allowed.Remove(member.Username))
            Host.Disallow(member.Username);
        Show(result);
    }
}
