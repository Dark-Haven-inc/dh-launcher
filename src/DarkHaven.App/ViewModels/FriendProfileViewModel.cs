using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using DarkHaven.Launcher;

namespace DarkHaven.App.ViewModels;

/// <summary>
/// Someone else's card, fetched from the platform when it's first opened — the same
/// <see cref="Views.ProfileCard"/> the player sees for their own profile.
/// </summary>
public partial class FriendProfileViewModel : ViewModelBase, IProfileCard
{
    [ObservableProperty] private string _cardName;
    [ObservableProperty] private string _cardSubtitle;
    [ObservableProperty] private string _cardMemberSince = "…";
    [ObservableProperty] private string? _cardAbout;
    [ObservableProperty] private Bitmap? _cardAvatar;
    [ObservableProperty] private Bitmap? _cardBanner;
    [ObservableProperty] private string? _accentColor;
    [ObservableProperty] private string _frame = "blue";

    public bool CardHasAbout => CardAbout is not null;
    public bool CardHasAvatar => CardAvatar is not null;
    public bool CardHasBanner => CardBanner is not null;
    public IBrush CardAccentBrush => new SolidColorBrush(ProfileLook.Accent(AccentColor));
    public IBrush CardBannerBrush => ProfileLook.BannerBrush(AccentColor);
    public IBrush CardRingBrush => ProfileLook.Frame(Frame).RingBrush;

    partial void OnCardAboutChanged(string? value) => OnPropertyChanged(nameof(CardHasAbout));
    partial void OnCardAvatarChanged(Bitmap? value) => OnPropertyChanged(nameof(CardHasAvatar));
    partial void OnCardBannerChanged(Bitmap? value) => OnPropertyChanged(nameof(CardHasBanner));

    partial void OnAccentColorChanged(string? value)
    {
        OnPropertyChanged(nameof(CardAccentBrush));
        OnPropertyChanged(nameof(CardBannerBrush));
    }

    partial void OnFrameChanged(string value) => OnPropertyChanged(nameof(CardRingBrush));

    /// <param name="status">Where they are right now ("играет: ХЕЙВЕН") — shown until/unless they set a title.</param>
    public FriendProfileViewModel(AppServices services, Guid userId, string username, string status)
    {
        _cardName = username;
        _cardSubtitle = status;
        _ = LoadAsync(services, userId);
    }

    private async Task LoadAsync(AppServices services, Guid userId)
    {
        var p = await services.Platform.GetPublicProfileAsync(userId);
        if (p is null)
        {
            CardMemberSince = "—";
            return;
        }

        CardName = p.Username;
        if (p.Title is { Length: > 0 } title)
            CardSubtitle = title;
        CardMemberSince = RuText.Date(p.MemberSince.ToLocalTime());
        CardAbout = string.IsNullOrWhiteSpace(p.AboutMe) ? null : p.AboutMe;
        AccentColor = p.AccentColor;
        if (ProfileLook.Frames.Contains(p.Frame))
            Frame = p.Frame;

        CardAvatar = await services.Images.GetAsync(p.AvatarUrl);
        CardBanner = await services.Images.GetAsync(p.BannerUrl);
    }
}
