using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher;
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
public partial class ProfileViewModel(AppServices services, Action openAccounts) : ViewModelBase
{
    private static readonly string[] Frames = ["none", "blue", "gold", "cyan"];

    [ObservableProperty] private Bitmap? _avatar;
    [ObservableProperty] private string _frame = "blue";
    [ObservableProperty] private string _totalPlaytime = "—";
    [ObservableProperty] private string _memberSince = "—";
    [ObservableProperty] private int _favoritesCount;
    [ObservableProperty] private int _recentCount;

    public ObservableCollection<PlaytimeRowViewModel> Servers { get; } = [];

    public string AccountName => services.Accounts.Active?.Username
                                 ?? services.Accounts.Accounts.FirstOrDefault()?.Username
                                 ?? "Гость";

    public bool IsGuest => services.Accounts.Accounts.Count == 0;
    public string AccountStatus => IsGuest ? "гостевой вход" : "аккаунт Space Station 14";
    public bool HasAvatar => Avatar is not null;
    public bool NoPlaytime => Servers.Count == 0;

    public FrameBrushes FrameColors => Frame switch
    {
        "gold" => new(Color.Parse("#F0B454"), Color.Parse("#3A2E12")),
        "cyan" => new(Color.Parse("#7FD4FF"), Color.Parse("#14313A")),
        "none" => new(Color.Parse("#24365A"), Colors.Transparent),
        _ => new(Color.Parse("#5AA0FF"), Color.Parse("#111A2E")),
    };

    public void Reload()
    {
        try
        {
            Frame = services.Settings.GetConfig("ProfileFrame") is { } f && Frames.Contains(f) ? f : "blue";
            LoadAvatar(services.Settings.GetConfig("ProfileAvatarPath"));

            var total = services.Settings.GetTotalPlaytimeSeconds();
            TotalPlaytime = total > 0 ? FormatDuration(total) : "ещё не играл";
            MemberSince = services.Settings.GetFirstPlayed() is { } d
                ? d.ToLocalTime().ToString("d MMMM yyyy", new System.Globalization.CultureInfo("ru-RU"))
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
    }

    /// <summary>Called by the view after the user picks an image.</summary>
    public void SetAvatar(string path)
    {
        try
        {
            var dest = Path.Combine(LauncherPaths.DataDir, "avatar" + Path.GetExtension(path));
            File.Copy(path, dest, overwrite: true);
            services.Settings.SetConfig("ProfileAvatarPath", dest);
            LoadAvatar(dest);
            OnPropertyChanged(nameof(HasAvatar));
        }
        catch { /* ignore bad file */ }
    }

    [RelayCommand]
    private void ClearAvatar()
    {
        services.Settings.SetConfig("ProfileAvatarPath", null);
        Avatar?.Dispose();
        Avatar = null;
        OnPropertyChanged(nameof(HasAvatar));
    }

    [RelayCommand]
    private void SetFrame(string frame)
    {
        Frame = Frames.Contains(frame) ? frame : "blue";
        services.Settings.SetConfig("ProfileFrame", Frame);
        OnPropertyChanged(nameof(FrameColors));
    }

    [RelayCommand] private void ManageAccounts() => openAccounts();

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
