using System.Collections.ObjectModel;
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
}

/// <summary>АДМИН — news CRUD, launcher bans, warnings. Everything here needs an "admin"/"owner"
/// (or "news" for the news actions) role on DarkHaven.Platform.Api; role management itself
/// ("owner" only) isn't exposed here yet — grant those directly against the platform DB for now.</summary>
public partial class AdminViewModel(AppServices services) : ViewModelBase
{
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _status;

    [ObservableProperty] private string _newsTitle = "";
    [ObservableProperty] private string _newsBody = "";

    [ObservableProperty] private string _banUsername = "";
    [ObservableProperty] private string _banReason = "";
    [ObservableProperty] private string _banDays = "";

    [ObservableProperty] private string _warnUsername = "";
    [ObservableProperty] private string _warnText = "";

    public ObservableCollection<AdminNewsRowViewModel> News { get; } = [];
    public ObservableCollection<AdminBanRowViewModel> Bans { get; } = [];

    public bool NotConnected => !services.Platform.IsConfigured;
    public bool NotSignedIn => services.Platform.IsConfigured && !services.Platform.IsSignedIn;
    public bool CanAdmin => services.Platform.CanAdmin;
    public bool NoAccess => services.Platform.IsSignedIn && !CanAdmin;

    public async Task ReloadAsync()
    {
        OnPropertyChanged(nameof(NotConnected));
        OnPropertyChanged(nameof(NotSignedIn));
        OnPropertyChanged(nameof(CanAdmin));
        OnPropertyChanged(nameof(NoAccess));

        if (!CanAdmin)
            return;

        IsLoading = true;
        try
        {
            var news = await services.Platform.GetAllNewsAsync();
            News.Clear();
            foreach (var n in news)
                News.Add(new AdminNewsRowViewModel(n));

            var bans = await services.Platform.GetLauncherBansAsync();
            Bans.Clear();
            foreach (var b in bans)
                Bans.Add(new AdminBanRowViewModel(b));
        }
        catch { /* Status stays as-is; the page still shows whatever loaded last time */ }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task PostNews()
    {
        if (NewsTitle.Length == 0 || NewsBody.Length == 0) return;
        Status = "Публикую…";
        var ok = await services.Platform.PostNewsAsync(NewsTitle, NewsBody, null, draft: false);
        Status = ok ? "Опубликовано." : "Не удалось опубликовать.";
        if (ok)
        {
            NewsTitle = "";
            NewsBody = "";
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private async Task DeleteNews(AdminNewsRowViewModel row)
    {
        if (await services.Platform.DeleteNewsAsync(row.Id))
            News.Remove(row);
        else
            Status = "Не удалось удалить.";
    }

    [RelayCommand]
    private async Task IssueBan()
    {
        if (BanUsername.Length == 0 || BanReason.Length == 0) return;

        var who = await services.Platform.LookupUserAsync(BanUsername.Trim());
        if (who is null)
        {
            Status = $"Игрок «{BanUsername}» не найден — он ещё ни разу не открывал ПРОФИЛЬ с этим лаунчером.";
            return;
        }

        DateTimeOffset? expires = null;
        if (int.TryParse(BanDays, out var days) && days > 0)
            expires = DateTimeOffset.UtcNow.AddDays(days);

        var ok = await services.Platform.IssueLauncherBanAsync(who.UserId, BanReason.Trim(), expires);
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
        if (await services.Platform.RevokeLauncherBanAsync(row.Id))
            Bans.Remove(row);
        else
            Status = "Не удалось снять бан.";
    }

    [RelayCommand]
    private async Task SendWarning()
    {
        if (WarnUsername.Length == 0 || WarnText.Length == 0) return;

        var who = await services.Platform.LookupUserAsync(WarnUsername.Trim());
        if (who is null)
        {
            Status = $"Игрок «{WarnUsername}» не найден.";
            return;
        }

        var ok = await services.Platform.SendWarningAsync(who.UserId, WarnText.Trim());
        Status = ok ? $"Предупреждение отправлено {who.Username}." : "Не удалось отправить.";
        if (ok)
        {
            WarnUsername = "";
            WarnText = "";
        }
    }
}
