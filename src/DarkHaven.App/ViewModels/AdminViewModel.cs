using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher;

namespace DarkHaven.App.ViewModels;

public sealed class AdminNewsRowViewModel(PlatformNewsAdmin n)
{
    public int Id => n.Id;
    public string Title => n.Title;
    public string Body => n.BodyMarkdown;
    public bool Draft => n.Draft;
    public string DateText => RuText.ShortDate(n.PublishedAt.ToLocalTime());
}

public sealed class AdminBanRowViewModel(PlatformBan b)
{
    public int Id => b.Id;
    public string Username => b.Username;
    public string Reason => b.Reason;
    public bool Active => b.Active;
    public string ExpiryText => b.ExpiresAt is { } e ? $"до {RuText.ShortDate(e.ToLocalTime())}" : "навсегда";
    public string IssuedText => $"выдал {b.IssuedByUsername} · {RuText.ShortDate(b.IssuedAt.ToLocalTime())}";
}

public sealed class AdminRoleRowViewModel(PlatformRole r)
{
    public Guid UserId => r.UserId;
    public string Username => r.Username;
    public string Role => r.Role;
    public string RoleTitle => RoleInfo.Title(r.Role);
    public bool IsPrimary => r.IsPrimary;
    public bool CanRevoke => !r.IsPrimary;
    public string GrantedText => r.IsPrimary
        ? "главный владелец — задан в настройках платформы"
        : r.GrantedByName is { } by && r.GrantedAt is { } at
            ? $"выдал {by} · {RuText.ShortDate(at.ToLocalTime())}"
            : r.GrantedAt is { } auto ? $"с {RuText.ShortDate(auto.ToLocalTime())}" : "";
}

/// <summary>What each launcher role means, in the words the РОЛИ section shows. Mirrors the
/// platform's RoleRules — the platform is what actually enforces it.</summary>
public static class RoleInfo
{
    public static readonly string[] All = ["news", "moderator", "admin", "owner"];

    public static string Title(string? role) => role switch
    {
        "owner" => "владелец",
        "admin" => "администратор",
        "moderator" => "модератор",
        "news" => "редактор новостей",
        _ => "игрок",
    };

    public static string Describe(string? role) => role switch
    {
        "news" => "Пишет и правит новости. Больше ничего.",
        "moderator" => "Ищет игроков, банит на лаунчере, выносит предупреждения, убирает чужие аватары и «о себе», читает и пишет в чат сервера и ahelp.",
        "admin" => "Всё, что модератор, плюс новости, журнал действий, объявления всем и выдача прав администратора в игре.",
        "owner" => "Всё, плюс раздача ролей. Нового владельца назначает только главный владелец; снять владельца может только другой владелец, себя — нельзя.",
        _ => "",
    };
}

/// <summary>Someone who has admin rights on the game server itself (not a platform role).</summary>
public sealed class AdminGameAdminRowViewModel(PlatformGameAdmin a)
{
    public Guid UserId => a.UserId;
    public string Username => a.Username ?? "неизвестный игрок";
    public string RankText => a.RankName ?? "без ранга";
    public string StateText => a.Suspended ? " · отстранён" : a.Deadminned ? " · снял права сам" : "";
    public string TitleText => string.IsNullOrWhiteSpace(a.Title) ? "" : $" · {a.Title}";
}

/// <summary>A ban the game server itself issued, as shown on a player's card.</summary>
public sealed class AdminGameBanRowViewModel(PlatformGameBan b)
{
    public string Reason => b.Reason;
    public string WhenText => RuText.ShortDate(b.At.ToLocalTime());
    public string KindText => b.IsRoleBan ? "бан на роль" : "бан на сервер";
    public string StateText => b.Lifted ? "снят" : b.ExpiresAt is { } e ? $"до {RuText.ShortDate(e.ToLocalTime())}" : "навсегда";
}

public sealed class AdminAuditRowViewModel(PlatformAuditEntry a)
{
    public string ActorUsername => a.ActorUsername;
    public string Action => a.Action;
    public string? TargetUsername => a.TargetUsername;
    public string Details => a.Details;
    public string WhenText => RuText.ShortDateTime(a.At.ToLocalTime());
}

public sealed class AdminChatMessageViewModel(PlatformChatMessage m)
{
    public string Sender => m.Sender;
    public string Text => m.Text;
    public DateTimeOffset AtUtc => m.AtUtc;
    public string TimeText => m.AtUtc.ToLocalTime().ToString("HH:mm:ss");
}

/// <summary>АДМИН — player lookup, launcher bans, warnings, live game chat/ahelp (moderator+); news
/// CRUD, audit log, announcements (admin/owner); role management (owner-only). The nav tab itself is
/// hidden for anyone without at least "moderator" on DarkHaven.Platform.Api — see
/// MainWindowViewModel/MainWindow.axaml binding to <see cref="CanModerate"/> — this VM's own gating
/// is defence in depth (and covers the moment between window-open and the platform sign-in landing).</summary>
public partial class AdminViewModel : ViewModelBase
{
    private readonly AppServices _services;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _status;

    [ObservableProperty] private string _newsTitle = "";
    [ObservableProperty] private string _newsBody = "";
    [ObservableProperty] private AdminNewsRowViewModel? _editingNews;

    [ObservableProperty] private string _banUsername = "";
    [ObservableProperty] private string _banReason = "";
    [ObservableProperty] private string _banDays = "";

    [ObservableProperty] private string _warnUsername = "";
    [ObservableProperty] private string _warnText = "";

    [ObservableProperty] private string _searchUsername = "";
    [ObservableProperty] private PlatformPlayerInfo? _searchResult;
    [ObservableProperty] private bool _searchNotFound;

    /// <summary>The found player's profile card — so a moderator sees what they'd be taking down.</summary>
    [ObservableProperty] private FriendProfileViewModel? _searchCard;
    [ObservableProperty] private string? _lookStatus;

    partial void OnSearchResultChanged(PlatformPlayerInfo? value)
    {
        LookStatus = null;
        SearchCard = value is null ? null : new FriendProfileViewModel(_services, value.UserId, value.Username, "игрок Frontier 15");
    }

    [ObservableProperty] private bool _gameAccessEnabled;
    [ObservableProperty] private bool _hasGameBans;
    [ObservableProperty] private PlatformGameRank? _selectedGameRank;
    [ObservableProperty] private string _gameAdminTitle = "";
    [ObservableProperty] private string? _gameAccessStatus;

    [ObservableProperty] private string _roleUsername = "";
    [ObservableProperty] private string _roleToGrant = "moderator";
    [ObservableProperty] private string? _roleStatus;

    [ObservableProperty] private string _selectedRegion = "";
    [ObservableProperty] private string _chatChannel = "ooc";
    [ObservableProperty] private string _chatText = "";

    [ObservableProperty] private string _ahelpUsername = "";
    [ObservableProperty] private string _ahelpText = "";
    [ObservableProperty] private bool _ahelpAdminOnly;

    [ObservableProperty] private string _announcementText = "";

    public ObservableCollection<AdminNewsRowViewModel> News { get; } = [];
    public ObservableCollection<AdminBanRowViewModel> Bans { get; } = [];
    public ObservableCollection<AdminRoleRowViewModel> Roles { get; } = [];
    public ObservableCollection<AdminAuditRowViewModel> AuditLog { get; } = [];
    public ObservableCollection<string> GameServers { get; } = [];
    public ObservableCollection<AdminChatMessageViewModel> ChatMessages { get; } = [];
    public ObservableCollection<AdminGameAdminRowViewModel> GameAdmins { get; } = [];
    public ObservableCollection<PlatformGameRank> GameRanks { get; } = [];
    /// <summary>The game server's own bans on the player currently shown in ИГРОК.</summary>
    public ObservableCollection<AdminGameBanRowViewModel> SearchGameBans { get; } = [];

    private DateTimeOffset? _chatCursor;

    public bool NotConnected => !_services.Platform.IsConfigured;
    public bool NotSignedIn => _services.Platform.IsConfigured && !_services.Platform.IsSignedIn;
    public bool CanModerate => _services.Platform.CanModerate;
    public bool CanAdmin => _services.Platform.CanAdmin;
    public bool IsOwner => _services.Platform.Roles.Contains("owner");
    public bool NoAccess => _services.Platform.IsSignedIn && !CanModerate;
    public bool IsEditingNews => EditingNews is not null;
    public string NewsSubmitLabel => IsEditingNews ? "Сохранить" : "Опубликовать";

    public AdminViewModel(AppServices services)
    {
        _services = services;
        // The platform sign-in resolves async after the window opens — re-check gating (and, via
        // MainWindow's binding to CanModerate, show/hide the nav tab itself) whenever it changes.
        services.PlatformSessionChanged += OnPlatformSessionChanged;

        // Writing into a channel with no way to see the reply isn't very useful — poll for new
        // messages on whatever region/channel is currently selected. Cheap enough (one small GET
        // every few seconds) to just run for the app's lifetime rather than start/stop it with
        // page navigation, matching RegionWatcher's existing always-on polling pattern.
        _ = ChatPollLoopAsync();
    }

    partial void OnSelectedRegionChanged(string value) => ResetChatFeed();
    partial void OnChatChannelChanged(string value) => ResetChatFeed();

    private void ResetChatFeed()
    {
        ChatMessages.Clear();
        _chatCursor = null;
    }

    private async Task ChatPollLoopAsync()
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (!CanModerate || SelectedRegion.Length == 0 || ChatChannel.Length == 0)
                continue;

            try
            {
                var fresh = await _services.Platform.GetRecentGameChatAsync(SelectedRegion, ChatChannel, _chatCursor);
                foreach (var m in fresh)
                {
                    ChatMessages.Add(new AdminChatMessageViewModel(m));
                    if (_chatCursor is null || m.AtUtc > _chatCursor)
                        _chatCursor = m.AtUtc;
                }
                while (ChatMessages.Count > 200)
                    ChatMessages.RemoveAt(0);
            }
            catch { /* next poll tries again */ }
        }
    }

    private void OnPlatformSessionChanged()
    {
        OnPropertyChanged(nameof(NotConnected));
        OnPropertyChanged(nameof(NotSignedIn));
        OnPropertyChanged(nameof(CanModerate));
        OnPropertyChanged(nameof(CanAdmin));
        OnPropertyChanged(nameof(IsOwner));
        OnPropertyChanged(nameof(NoAccess));
    }

    public async Task ReloadAsync()
    {
        OnPlatformSessionChanged();
        if (!CanModerate)
            return;

        IsLoading = true;
        try
        {
            var bans = await _services.Platform.GetLauncherBansAsync();
            Bans.Clear();
            foreach (var b in bans)
                Bans.Add(new AdminBanRowViewModel(b));

            var servers = await _services.Platform.GetGameServersAsync();
            GameServers.Clear();
            foreach (var s in servers)
                GameServers.Add(s);
            if (SelectedRegion.Length == 0 || !GameServers.Contains(SelectedRegion))
                SelectedRegion = GameServers.FirstOrDefault() ?? "";

            if (CanAdmin)
            {
                var news = await _services.Platform.GetAllNewsAsync();
                News.Clear();
                foreach (var n in news)
                    News.Add(new AdminNewsRowViewModel(n));

                var audit = await _services.Platform.GetAuditLogAsync();
                AuditLog.Clear();
                foreach (var a in audit)
                    AuditLog.Add(new AdminAuditRowViewModel(a));

                await ReloadGameAccessAsync();
            }

            if (IsOwner)
            {
                var roles = await _services.Platform.GetRolesAsync();
                Roles.Clear();
                foreach (var r in roles)
                    Roles.Add(new AdminRoleRowViewModel(r));
            }
        }
        catch { /* Status stays as-is; the page still shows whatever loaded last time */ }
        finally
        {
            IsLoading = false;
        }
    }

    // --- Доступ на игровой сервер (admin/owner) ---

    /// <summary>Who currently has in-game admin rights, and which ranks the server offers.</summary>
    public async Task ReloadGameAccessAsync()
    {
        var access = await _services.Platform.GetGameAccessAsync();
        GameAccessEnabled = access?.Enabled ?? false;

        GameRanks.Clear();
        foreach (var r in access?.Ranks ?? [])
            GameRanks.Add(r);
        SelectedGameRank ??= GameRanks.FirstOrDefault();

        GameAdmins.Clear();
        foreach (var a in access?.Admins ?? [])
            GameAdmins.Add(new AdminGameAdminRowViewModel(a));
    }

    /// <summary>Gives the player found in ИГРОК the selected rank on the game server.</summary>
    [RelayCommand]
    private async Task GrantGameAccess()
    {
        if (!CanAdmin || SearchResult is not { } who) return;
        if (SelectedGameRank is not { } rank)
        {
            GameAccessStatus = "Сначала выберите ранг.";
            return;
        }

        GameAccessStatus = await _services.Platform.GrantGameAdminAsync(who.UserId, rank.Id, GameAdminTitle)
                           ?? $"{who.Username} получил ранг {rank.Name}. Права появятся при следующем заходе в игру.";
        GameAdminTitle = "";
        await ReloadGameAccessAsync();
        await SearchPlayer();
    }

    [RelayCommand]
    private async Task RevokeGameAccess(AdminGameAdminRowViewModel? row)
    {
        if (!CanAdmin || row is null) return;

        GameAccessStatus = await _services.Platform.RevokeGameAdminAsync(row.UserId)
                           ?? $"{row.Username} больше не администратор на сервере.";
        await ReloadGameAccessAsync();
    }

    // --- News ---

    [RelayCommand]
    private async Task PostNews()
    {
        if (NewsTitle.Length == 0 || NewsBody.Length == 0) return;
        Status = IsEditingNews ? "Сохраняю…" : "Публикую…";

        var ok = IsEditingNews
            ? await _services.Platform.EditNewsAsync(EditingNews!.Id, NewsTitle, NewsBody, null, draft: false)
            : await _services.Platform.PostNewsAsync(NewsTitle, NewsBody, null, draft: false);

        Status = ok ? "Готово." : "Не удалось сохранить.";
        if (ok)
        {
            NewsTitle = "";
            NewsBody = "";
            EditingNews = null;
            OnPropertyChanged(nameof(IsEditingNews));
            OnPropertyChanged(nameof(NewsSubmitLabel));
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private void EditNews(AdminNewsRowViewModel row)
    {
        EditingNews = row;
        NewsTitle = row.Title;
        NewsBody = row.Body;
        OnPropertyChanged(nameof(IsEditingNews));
        OnPropertyChanged(nameof(NewsSubmitLabel));
    }

    [RelayCommand]
    private void CancelEditNews()
    {
        EditingNews = null;
        NewsTitle = "";
        NewsBody = "";
        OnPropertyChanged(nameof(IsEditingNews));
        OnPropertyChanged(nameof(NewsSubmitLabel));
    }

    [RelayCommand]
    private async Task DeleteNews(AdminNewsRowViewModel row)
    {
        if (await _services.Platform.DeleteNewsAsync(row.Id))
            News.Remove(row);
        else
            Status = "Не удалось удалить.";
    }

    // --- Bans ---

    [RelayCommand]
    private async Task IssueBan()
    {
        if (BanUsername.Length == 0 || BanReason.Length == 0) return;

        var who = await _services.Platform.GetPlayerAsync(BanUsername.Trim());
        if (who is null)
        {
            Status = $"Игрок «{BanUsername}» не найден — он ещё ни разу не открывал ПРОФИЛЬ с этим лаунчером.";
            return;
        }

        DateTimeOffset? expires = null;
        if (int.TryParse(BanDays, out var days) && days > 0)
            expires = DateTimeOffset.UtcNow.AddDays(days);

        var error = await _services.Platform.IssueLauncherBanAsync(who.UserId, BanReason.Trim(), expires);
        Status = error ?? $"{who.Username} забанен на лаунчере.";
        if (error is null)
        {
            BanUsername = "";
            BanReason = "";
            BanDays = "";
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private async Task RevokeBan(AdminBanRowViewModel row)
    {
        if (await _services.Platform.RevokeLauncherBanAsync(row.Id))
            Bans.Remove(row);
        else
            Status = "Не удалось снять бан.";
    }

    // --- Warnings ---

    [RelayCommand]
    private async Task SendWarning()
    {
        if (WarnUsername.Length == 0 || WarnText.Length == 0) return;

        var who = await _services.Platform.GetPlayerAsync(WarnUsername.Trim());
        if (who is null)
        {
            Status = $"Игрок «{WarnUsername}» не найден.";
            return;
        }

        var error = await _services.Platform.SendWarningAsync(who.UserId, WarnText.Trim());
        Status = error ?? $"Предупреждение отправлено {who.Username}.";
        if (error is null)
        {
            WarnUsername = "";
            WarnText = "";
        }
    }

    // --- Player search: look someone up before deciding what to do about them ---

    [RelayCommand]
    private async Task SearchPlayer()
    {
        if (SearchUsername.Length == 0) return;
        SearchResult = null;
        SearchNotFound = false;

        var who = await _services.Platform.GetPlayerAsync(SearchUsername.Trim());
        if (who is null)
            SearchNotFound = true;
        else
            SearchResult = who;

        // The game server's own bans, which the platform reads straight from its database.
        SearchGameBans.Clear();
        foreach (var b in who?.GameBans ?? [])
            SearchGameBans.Add(new AdminGameBanRowViewModel(b));
        HasGameBans = SearchGameBans.Count > 0;
    }

    /// <summary>Take down the found player's avatar, banner or "о себе" ("avatar"/"banner"/"about").</summary>
    [RelayCommand]
    private async Task ClearLook(string part)
    {
        if (SearchResult is not { } who)
            return;

        var what = part switch { "avatar" => "аватар", "banner" => "баннер", _ => "«О себе»" };
        if (await _services.Platform.ClearLookAsync(who.UserId, part) is { } error)
        {
            LookStatus = error;
        }
        else
        {
            LookStatus = $"Убрано: {what}. Игрок получил уведомление, запись — в журнале.";
            SearchCard = new FriendProfileViewModel(_services, who.UserId, who.Username, "игрок Frontier 15");
        }
    }

    // --- Roles (owner only — the view hides this section for plain admins) ---

    [RelayCommand]
    private void SetRoleToGrant(string role) => RoleToGrant = role;

    public string RoleToGrantTitle => RoleInfo.Title(RoleToGrant);
    public string RoleToGrantDescription => RoleInfo.Describe(RoleToGrant);
    partial void OnRoleToGrantChanged(string value)
    {
        OnPropertyChanged(nameof(RoleToGrantTitle));
        OnPropertyChanged(nameof(RoleToGrantDescription));
    }

    [RelayCommand]
    private async Task GrantRole()
    {
        if (RoleUsername.Length == 0) return;

        var who = await _services.Platform.GetPlayerAsync(RoleUsername.Trim());
        if (who is null)
        {
            RoleStatus = $"Игрок «{RoleUsername}» не найден — он должен хотя бы раз войти в лаунчер под своим аккаунтом.";
            return;
        }

        var error = await _services.Platform.SetRoleAsync(who.UserId, RoleToGrant);
        RoleStatus = error ?? $"{who.Username} теперь {RoleInfo.Title(RoleToGrant)}. Он получил уведомление, вкладка у него обновится в течение пары минут.";
        if (error is null)
        {
            RoleUsername = "";
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private async Task RevokeRole(AdminRoleRowViewModel row)
    {
        if (await _services.Platform.RemoveRoleAsync(row.UserId) is { } error)
        {
            RoleStatus = error;
            return;
        }
        Roles.Remove(row);
        RoleStatus = $"{row.Username} больше не {row.RoleTitle}. Права пропали сразу.";
    }

    // --- Live game chat (moderator+): OOC/AdminChat/DeadChat on a chosen region's server ---

    [RelayCommand]
    private void SetChatChannel(string channel) => ChatChannel = channel;

    [RelayCommand]
    private async Task SendGameChat()
    {
        if (ChatText.Length == 0 || SelectedRegion.Length == 0) return;

        var ok = await _services.Platform.SendGameChatAsync(SelectedRegion, ChatChannel, ChatText.Trim());
        Status = ok ? "Отправлено в игру." : "Не удалось отправить — сервер недоступен или нет прав.";
        if (ok)
            ChatText = "";
    }

    // --- AHelp reply (moderator+): resolve a typed username to a UserId, then reply to their ticket ---

    [RelayCommand]
    private async Task ReplyAhelp()
    {
        if (AhelpUsername.Length == 0 || AhelpText.Length == 0 || SelectedRegion.Length == 0) return;

        var who = await _services.Platform.GetPlayerAsync(AhelpUsername.Trim());
        if (who is null)
        {
            Status = $"Игрок «{AhelpUsername}» не найден.";
            return;
        }

        var ok = await _services.Platform.ReplyAhelpAsync(SelectedRegion, who.UserId, AhelpText.Trim(), AhelpAdminOnly);
        Status = ok ? $"Ответ отправлен {who.Username}." : "Не удалось отправить — игрок не в сети или сервер недоступен.";
        if (ok)
            AhelpText = "";
    }

    // --- Announcements (admin/owner only) ---

    [RelayCommand]
    private async Task PostAnnouncement()
    {
        if (AnnouncementText.Length == 0) return;

        var ok = await _services.Platform.PostAnnouncementAsync(AnnouncementText.Trim());
        Status = ok ? "Объявление отправлено." : "Не удалось отправить объявление.";
        if (ok)
            AnnouncementText = "";
    }
}
