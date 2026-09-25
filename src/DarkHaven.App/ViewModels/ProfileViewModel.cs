using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Data;

namespace DarkHaven.App.ViewModels;

public sealed class PlaytimeRowViewModel(PlaytimeEntry e, long max)
{
    public string Name => e.Name;
    public bool IsRegion => e.IsRegion;
    public string Hours => ProfileViewModel.FormatDuration(e.TotalSeconds);
    public string Sessions => e.Sessions == 1 ? "1 заход" : $"{e.Sessions} заходов";
    public double BarFraction => max > 0 ? Math.Max(0.02, (double)e.TotalSeconds / max) : 0;
}

/// <summary>ПРОФИЛЬ — a local player card: avatar/frame, total + per-server playtime, favourites.
/// Everything here is tracked by the launcher itself; server-synced data waits on the DH backend.</summary>
public sealed class CharacterRowViewModel(PlatformCharacter c)
{
    public string Name => c.Name;
    public string Slot => $"слот {c.Slot + 1}";
    public bool Selected => c.Selected;
    /// <summary>Frontier's spesos, written the way the game's own bank UI does: a dollar sign.</summary>
    public string Balance => "$" + RuText.Number(c.BankBalance);
}

public partial class ProfileViewModel(AppServices services, Action openAccounts) : ViewModelBase
{
    private static readonly string[] Frames = ProfileLook.Frames;

    [ObservableProperty] private Bitmap? _avatar;
    [ObservableProperty] private string _frame = "blue";
    [ObservableProperty] private string _totalPlaytime = "—";
    [ObservableProperty] private string _memberSince = "—";
    [ObservableProperty] private int _favoritesCount;
    [ObservableProperty] private int _recentCount;

    /// <summary>True once <see cref="LoadFromPlatformAsync"/> has actually pulled a profile from
    /// DarkHaven.Platform.Api — playtime/ban fields below are only server-authoritative when this is set.</summary>
    [ObservableProperty] private bool _isPlatformConnected;
    [ObservableProperty] private bool _launcherBanned;
    [ObservableProperty] private string? _launcherBanText;

    /// <summary>The code to type as <c>!link &lt;код&gt;</c> to the "Дозорный" Discord bot, once requested.</summary>
    [ObservableProperty] private string? _discordLinkCode;
    /// <summary>The linked Discord account's name, or null while nothing is linked.</summary>
    [ObservableProperty] private string? _discordLinkedName;
    [ObservableProperty] private string? _discordStatus;
    [ObservableProperty] private bool _discordLinkRequesting;

    public ObservableCollection<PlaytimeRowViewModel> Servers { get; } = [];

    /// <summary>The player's characters from the game DB, with bank balance — platform only.</summary>
    public ObservableCollection<CharacterRowViewModel> Characters { get; } = [];
    [ObservableProperty] private string? _charactersNote;
    public bool HasCharacters => Characters.Count > 0;

    public string AccountName => services.Accounts.Active?.Username
                                 ?? services.Accounts.Accounts.FirstOrDefault()?.Username
                                 ?? "Гость";

    private AppServices Services => services;

    public bool IsGuest => services.Accounts.Accounts.Count == 0;
    public string AccountStatus => IsGuest ? "гостевой вход" : "аккаунт Space Station 14";
    public bool HasAvatar => Avatar is not null;
    public bool NoPlaytime => Servers.Count == 0;

    public FrameBrushes FrameColors => ProfileLook.Frame(Frame);

    public void Reload()
    {
        // Leaving the page mid-edit throws the unsaved edit away rather than half-keeping it.
        if (IsEditing)
            CancelEdit();

        try
        {
            Frame = services.Settings.GetConfig("ProfileFrame") is { } f && Frames.Contains(f) ? f : "blue";
            LoadAvatar(services.Settings.GetConfig("ProfileAvatarPath"));
            LoadLook();

            var total = services.Settings.GetTotalPlaytimeSeconds();
            TotalPlaytime = total > 0 ? FormatDuration(total) : "ещё не играл";
            MemberSince = services.Settings.GetFirstPlayed() is { } d
                ? RuText.Date(d.ToLocalTime())
                : "—";

            var by = services.Settings.GetPlaytimeByServer();
            var max = by.Count > 0 ? by.Max(x => x.TotalSeconds) : 0;
            Servers.Clear();
            foreach (var e in by.Take(12))
                Servers.Add(new PlaytimeRowViewModel(e, max));

            FavoritesCount = services.Settings.GetFavorites().Count;
            RecentCount = services.Settings.GetRecent().Count;
        }
        catch { /* fresh db */ }

        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(IsGuest));
        OnPropertyChanged(nameof(AccountStatus));
        OnPropertyChanged(nameof(NoPlaytime));
        OnPropertyChanged(nameof(FrameColors));
        OnPropertyChanged(nameof(HasAvatar));

        IsPlatformConnected = false;
        LauncherBanned = false;
        LauncherBanText = null;
        _ = LoadFromPlatformAsync();
    }

    /// <summary>Overlays server-authoritative fields once DarkHaven.Platform.Api is configured and
    /// signed in — real playtime across DH servers, launcher-ban status. Local data above stays as
    /// the fallback (and is what's shown while this is still in flight, or if the platform is
    /// unreachable/not configured).</summary>
    private async Task LoadFromPlatformAsync()
    {
        if (!services.Platform.IsSignedIn)
            return;

        try
        {
            var p = await services.Platform.GetProfileAsync();
            if (p is null) return;

            IsPlatformConnected = true;
            DiscordLinkedName = p.DiscordId is null ? null : p.DiscordName ?? $"ID {p.DiscordId}";
            if (DiscordLinkedName is not null)
                DiscordLinkCode = null; // the code did its job

            if (p.PlaytimeSource == "game-server")
                TotalPlaytime = p.TotalPlaytimeSeconds > 0 ? FormatDuration(p.TotalPlaytimeSeconds) : "ещё не играл";

            MemberSince = RuText.Date(p.MemberSince.ToLocalTime());

            LauncherBanned = p.LauncherBanned;
            LauncherBanText = !p.LauncherBanned ? null : p.LauncherBanExpires is { } exp
                ? $"Бан на лаунчере до {RuText.Date(exp.ToLocalTime())} — {p.LauncherBanReason}"
                : $"Бан на лаунчере навсегда — {p.LauncherBanReason}";

            var chars = await services.Platform.GetCharactersAsync();
            Characters.Clear();
            if (chars is { Source: "game-server" })
            {
                foreach (var c in chars.Characters)
                    Characters.Add(new CharacterRowViewModel(c));
                CharactersNote = Characters.Count == 0 ? "На сервере у вас пока нет персонажей." : null;
            }
            else
            {
                CharactersNote = "Персонажи и их баланс появятся, когда платформа подключится к игровой базе.";
            }
            OnPropertyChanged(nameof(HasCharacters));

            await ApplyPlatformLookAsync(p);
        }
        catch { /* platform unreachable — local data already shown, nothing more to do */ }
    }

    /// <summary>Only in the editor now — kept on "Сохранить" (see ProfileViewModel.Look.cs).</summary>
    [RelayCommand]
    private void SetFrame(string frame) => Frame = Frames.Contains(frame) ? frame : "blue";

    [RelayCommand] private void ManageAccounts() => openAccounts();

    [RelayCommand]
    private async Task RequestDiscordLink()
    {
        if (!services.Platform.IsSignedIn) return;

        DiscordLinkRequesting = true;
        DiscordLinkCode = null;
        try
        {
            var result = await services.Platform.StartDiscordLinkAsync();
            DiscordLinkCode = result?.Code;
        }
        finally
        {
            DiscordLinkRequesting = false;
        }
    }

    /// <summary>Cut the Discord link. A launcher role that came from Discord goes with it.</summary>
    [RelayCommand]
    private async Task UnlinkDiscord()
    {
        if (await services.Platform.UnlinkDiscordAsync() is { } error)
        {
            DiscordStatus = error;
            return;
        }
        DiscordLinkedName = null;
        DiscordStatus = "Discord отвязан. Если роль в лаунчере давал Discord, она снята.";
    }

    private void LoadAvatar(string? path)
    {
        Avatar?.Dispose();
        Avatar = null;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try { Avatar = new Bitmap(path); }
            catch { /* corrupt image */ }
        }
    }

    public static string FormatDuration(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours} ч {t.Minutes} мин";
        if (t.TotalMinutes >= 1)
            return $"{(int)t.TotalMinutes} мин";
        return $"{(int)t.TotalSeconds} с";
    }
}

public readonly record struct FrameBrushes(Color Ring, Color Fill)
{
    public IBrush RingBrush => new SolidColorBrush(Ring);
    public IBrush FillBrush => new SolidColorBrush(Fill);
}
