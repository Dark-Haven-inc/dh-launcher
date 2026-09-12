using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher.Api;

namespace DarkHaven.App.ViewModels;

public sealed class AdminNewsRowViewModel(PlatformNewsAdmin n)
{
    public int Id => n.Id;
    public string Title => n.Title;
    public string Body => n.BodyMarkdown;
    public bool Draft => n.Draft;
    public string DateText => n.PublishedAt.ToLocalTime().ToString("d MMM yyyy");
}

public sealed class AdminBanRowViewModel(PlatformBan b)
{
    public int Id => b.Id;
    public string Username => b.Username;
    public string Reason => b.Reason;
    public bool Active => b.Active;
    public string ExpiryText => b.ExpiresAt is { } e ? $"до {e.ToLocalTime():d MMM yyyy}" : "навсегда";
    public string IssuedText => $"выдал {b.IssuedByUsername} · {b.IssuedAt.ToLocalTime():d MMM yyyy}";
}

public sealed class AdminRoleRowViewModel(PlatformRole r)
{
    public Guid UserId => r.UserId;
    public string Username => r.Username;
    public string Role => r.Role;
}

public sealed class AdminAuditRowViewModel(PlatformAuditEntry a)
{
    public string ActorUsername => a.ActorUsername;
    public string Action => a.Action;
    public string? TargetUsername => a.TargetUsername;
    public string Details => a.Details;
    public string WhenText => a.At.ToLocalTime().ToString("d MMM yyyy HH:mm");
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

    [ObservableProperty] private string _roleUsername = "";
    [ObservableProperty] private string _roleToGrant = "admin";

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

        var ok = await _services.Platform.IssueLauncherBanAsync(who.UserId, BanReason.Trim(), expires);
        Status = ok ? $"{who.Username} забанен на лаунчере." : "Не удалось выдать бан.";
        if (ok)
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

        var ok = await _services.Platform.SendWarningAsync(who.UserId, WarnText.Trim());
        Status = ok ? $"Предупреждение отправлено {who.Username}." : "Не удалось отправить.";
        if (ok)
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
    }

    // --- Roles (owner only — the view hides this section for plain admins) ---

    [RelayCommand]
    private void SetRoleToGrant(string role) => RoleToGrant = role;

    [RelayCommand]
    private async Task GrantRole()
    {
        if (RoleUsername.Length == 0) return;

        var who = await _services.Platform.GetPlayerAsync(RoleUsername.Trim());
        if (who is null)
        {
            Status = $"Игрок «{RoleUsername}» не найден.";
            return;
        }

        var ok = await _services.Platform.SetRoleAsync(who.UserId, RoleToGrant);
        Status = ok ? $"{who.Username} теперь {RoleToGrant}." : "Не удалось выдать роль.";
        if (ok)
        {
            RoleUsername = "";
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private async Task RevokeRole(AdminRoleRowViewModel row)
    {
        if (await _services.Platform.RemoveRoleAsync(row.UserId))
            Roles.Remove(row);
        else
            Status = "Не удалось снять роль.";
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
