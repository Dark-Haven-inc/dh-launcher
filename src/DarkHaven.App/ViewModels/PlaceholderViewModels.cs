using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher.Servers;

namespace DarkHaven.App.ViewModels;

/// <summary>ГЛАВНАЯ — a light landing page until there's a real dashboard / profile backend.</summary>
public partial class HomeViewModel(
    AppServices services, Action<ServerEntry> connect, Action goRegions, Action goServers) : ViewModelBase
{
    public string Greeting =>
        services.Accounts.Active is { } a ? $"С возвращением, {a.Username}" : "Dark Haven";

    [RelayCommand] private void OpenRegions() => goRegions();
    [RelayCommand] private void OpenServers() => goServers();
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
