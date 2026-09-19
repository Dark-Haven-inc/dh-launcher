using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher;
using DarkHaven.Launcher.Api;

namespace DarkHaven.App.ViewModels;

/// <summary>One colour swatch in the profile editor.</summary>
public sealed partial class AccentChoice(string hex) : ObservableObject
{
    public string Hex { get; } = hex;
    public IBrush Brush { get; } = new SolidColorBrush(Color.Parse(hex));
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// The Discord-style look of a profile — banner, profile colour, "о себе", avatar, frame — edited
/// with the card itself as the live preview. Nothing is kept until "Сохранить": picked pictures are
/// only staged, so "Отмена" really brings the old ones back. With the platform, "Сохранить" sends it
/// there first (that's what friends see) and keeps a local copy for when the platform is away.
/// </summary>
public partial class ProfileViewModel : IProfileCard
{
    /// <summary>Discord's own limit for "About me"; the platform enforces the same.</summary>
    public const int AboutMaxLength = 190;

    /// <summary>The launcher's accent, Frontier red, cyan, gold, green, violet, steel.</summary>
    public IReadOnlyList<AccentChoice> AccentChoices { get; } =
        new[] { "#3B82F6", "#F0384C", "#22D3EE", "#F0B454", "#4ADE80", "#A78BFA", "#5A6B8A" }
            .Select(h => new AccentChoice(h)).ToArray();

    [ObservableProperty] private Bitmap? _banner;
    [ObservableProperty] private string _accentColor = ProfileLook.DefaultAccent;
    [ObservableProperty] private string _aboutMe = "";
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string? _saveError;

    /// <summary>
    /// Signed in to the platform, but it has no look for this player yet while this machine does —
    /// friends are seeing a blank card until the next "Сохранить".
    /// </summary>
    [ObservableProperty] private bool _needsSync;

    // A picked file waiting for "Сохранить"; "" means "remove it on save"; null means untouched.
    private string? _stagedAvatar;
    private string? _stagedBanner;
    // Slots already sent this save, so a retry after a later failure doesn't upload them twice.
    private readonly HashSet<string> _pushed = [];
    private (string Accent, string About, string Frame) _beforeEdit;

    public bool HasBanner => Banner is not null;
    public string AboutCounter => $"{AboutMe.Length}/{AboutMaxLength}";
    public bool HasSaveError => SaveError is not null;
    public string SaveLabel => IsSaving ? "Сохраняю…" : "Сохранить";

    // --- IProfileCard ---

    public string CardName => AccountName;
    public string CardSubtitle => AccountStatus;
    public string CardMemberSince => MemberSince;
    public string? CardAbout => string.IsNullOrWhiteSpace(AboutMe) ? null : AboutMe.Trim();
    public bool CardHasAbout => CardAbout is not null;
    public Bitmap? CardAvatar => Avatar;
    public bool CardHasAvatar => Avatar is not null;
    public Bitmap? CardBanner => Banner;
    public bool CardHasBanner => Banner is not null;
    public IBrush CardAccentBrush => new SolidColorBrush(ProfileLook.Accent(AccentColor));
    public IBrush CardBannerBrush => ProfileLook.BannerBrush(AccentColor);
    public IBrush CardRingBrush => FrameColors.RingBrush;

    partial void OnAvatarChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(CardAvatar));
        OnPropertyChanged(nameof(CardHasAvatar));
        OnPropertyChanged(nameof(HasAvatar));
    }

    partial void OnBannerChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(CardBanner));
        OnPropertyChanged(nameof(CardHasBanner));
        OnPropertyChanged(nameof(HasBanner));
    }

    partial void OnAccentColorChanged(string value)
    {
        OnPropertyChanged(nameof(CardAccentBrush));
        OnPropertyChanged(nameof(CardBannerBrush));
        MarkSelectedAccent();
    }

    partial void OnAboutMeChanged(string value)
    {
        OnPropertyChanged(nameof(CardAbout));
        OnPropertyChanged(nameof(CardHasAbout));
        OnPropertyChanged(nameof(AboutCounter));
    }

    partial void OnMemberSinceChanged(string value) => OnPropertyChanged(nameof(CardMemberSince));

    partial void OnFrameChanged(string value)
    {
        OnPropertyChanged(nameof(FrameColors));
        OnPropertyChanged(nameof(CardRingBrush));
    }

    partial void OnSaveErrorChanged(string? value) => OnPropertyChanged(nameof(HasSaveError));
    partial void OnIsSavingChanged(bool value) => OnPropertyChanged(nameof(SaveLabel));

    private void MarkSelectedAccent()
    {
        foreach (var choice in AccentChoices)
            choice.IsSelected = string.Equals(choice.Hex, AccentColor, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>This machine's copy — what shows before (or without) the platform.</summary>
    private void LoadLook()
    {
        LoadBanner(Services.Settings.GetConfig("ProfileBannerPath"));
        var accent = Services.Settings.GetConfig("ProfileAccent");
        AccentColor = ProfileLook.IsHex(accent) ? accent! : ProfileLook.DefaultAccent;
        AboutMe = Services.Settings.GetConfig("ProfileAbout") ?? "";
        MarkSelectedAccent(); // setting the same colour again raises no change
        OnPropertyChanged(nameof(CardName));
        OnPropertyChanged(nameof(CardSubtitle));
    }

    /// <summary>
    /// The platform's look wins once it has one — that's the card everyone else sees. If it has none
    /// yet, the local look stays on screen and <see cref="NeedsSync"/> says friends can't see it.
    /// </summary>
    private async Task ApplyPlatformLookAsync(PlatformProfile p)
    {
        if (IsEditing)
            return; // never pull the rug from under the editor

        var platformHasLook = p.AvatarUrl is not null || p.BannerUrl is not null
                              || p.AccentColor is not null || p.AboutMe is not null;
        if (!platformHasLook)
        {
            NeedsSync = HasAvatar || HasBanner || AboutMe.Length > 0
                        || !string.Equals(AccentColor, ProfileLook.DefaultAccent, StringComparison.OrdinalIgnoreCase);
            return;
        }

        NeedsSync = false;
        AccentColor = ProfileLook.IsHex(p.AccentColor) ? p.AccentColor! : ProfileLook.DefaultAccent;
        AboutMe = p.AboutMe ?? "";
        if (ProfileLook.Frames.Contains(p.Frame))
            Frame = p.Frame;

        var avatar = await Services.Images.GetAsync(p.AvatarUrl);
        var banner = await Services.Images.GetAsync(p.BannerUrl);
        if (IsEditing)
            return; // the player opened the editor while the pictures were on their way
        if (avatar is not null || p.AvatarUrl is null) Avatar = avatar;
        if (banner is not null || p.BannerUrl is null) Banner = banner;
    }

    private void LoadBanner(string? path)
    {
        Banner?.Dispose();
        Banner = null;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try { Banner = new Bitmap(path); }
            catch { /* corrupt image */ }
        }
    }

    // --- editing ---

    [RelayCommand]
    private void Edit()
    {
        _beforeEdit = (AccentColor, AboutMe, Frame);
        _stagedAvatar = null;
        _stagedBanner = null;
        _pushed.Clear();
        SaveError = null;

        // The pictures this machine already has never reached the platform — queue them, so the
        // "Сохранить" the NeedsSync hint asks for really does publish them.
        if (NeedsSync)
        {
            _stagedAvatar = ExistingFile(Services.Settings.GetConfig("ProfileAvatarPath"));
            _stagedBanner = ExistingFile(Services.Settings.GetConfig("ProfileBannerPath"));
        }

        IsEditing = true;
    }

    /// <summary>Called by the view with the file the player picked.</summary>
    public void StageAvatar(string path)
    {
        try
        {
            Avatar = new Bitmap(path);
            _stagedAvatar = path;
            _pushed.Remove("avatar");
        }
        catch { /* not an image we can read — leave the current one */ }
    }

    public void StageBanner(string path)
    {
        try
        {
            Banner = new Bitmap(path);
            _stagedBanner = path;
            _pushed.Remove("banner");
        }
        catch { /* not an image we can read */ }
    }

    [RelayCommand]
    private void RemoveAvatar()
    {
        Avatar = null;
        _stagedAvatar = "";
        _pushed.Remove("avatar");
    }

    [RelayCommand]
    private void RemoveBanner()
    {
        Banner = null;
        _stagedBanner = "";
        _pushed.Remove("banner");
    }

    [RelayCommand]
    private void SetAccent(string hex)
    {
        if (ProfileLook.IsHex(hex))
            AccentColor = hex;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        SaveError = null;
        AccentColor = _beforeEdit.Accent;
        AboutMe = _beforeEdit.About;
        Frame = _beforeEdit.Frame;
        LoadAvatar(Services.Settings.GetConfig("ProfileAvatarPath"));
        LoadBanner(Services.Settings.GetConfig("ProfileBannerPath"));
        _stagedAvatar = null;
        _stagedBanner = null;
        // The pictures on screen before editing may have been the platform's, not these local ones.
        _ = LoadFromPlatformAsync();
    }

    [RelayCommand]
    private async Task SaveProfile()
    {
        if (IsSaving)
            return;
        SaveError = null;

        if (!ProfileLook.IsHex(AccentColor))
            AccentColor = ProfileLook.DefaultAccent;
        var about = AboutMe.Trim();
        if (about.Length > AboutMaxLength)
            about = about[..AboutMaxLength];
        AboutMe = about;

        // Platform first: if it refuses, stay in the editor with the reason, and keep nothing.
        if (Services.Platform.IsSignedIn)
        {
            IsSaving = true;
            try
            {
                if (!await PushPictureAsync("avatar", _stagedAvatar) || !await PushPictureAsync("banner", _stagedBanner))
                    return;
                var (ok, error) = await Services.Platform.UpdateLookAsync(Frame, null, AccentColor, about);
                if (!ok)
                {
                    SaveError = error;
                    return;
                }
            }
            finally
            {
                IsSaving = false;
            }
        }

        // This machine's copy.
        var settings = Services.Settings;
        try
        {
            // Skip a picture that's already this machine's copy (queued only to be published).
            if (_stagedAvatar is not null && !IsCurrent("ProfileAvatarPath", _stagedAvatar))
                Replace("ProfileAvatarPath", "avatar", _stagedAvatar);
            if (_stagedBanner is not null && !IsCurrent("ProfileBannerPath", _stagedBanner))
                Replace("ProfileBannerPath", "banner", _stagedBanner);
        }
        catch { /* the picked file vanished meanwhile — the platform already has it */ }

        settings.SetConfig("ProfileAccent", AccentColor);
        settings.SetConfig("ProfileAbout", about.Length == 0 ? null : about);
        settings.SetConfig("ProfileFrame", Frame);

        _stagedAvatar = null;
        _stagedBanner = null;
        NeedsSync = false;
        IsEditing = false;

        // Show what everyone else now sees — the platform crops and re-encodes the pictures.
        if (Services.Platform.IsSignedIn)
            _ = LoadFromPlatformAsync();
    }

    /// <summary>Sends one staged picture (or its removal). False, with <see cref="SaveError"/> set, if it failed.</summary>
    private async Task<bool> PushPictureAsync(string slot, string? staged)
    {
        if (staged is null || _pushed.Contains(slot))
            return true;

        if (staged.Length == 0)
        {
            if (!await Services.Platform.DeletePictureAsync(slot))
            {
                SaveError = "Не получилось убрать картинку на платформе. Попробуйте ещё раз.";
                return false;
            }
            _pushed.Add(slot);
            return true;
        }

        // Big photos are shrunk here rather than refused; off the UI thread, decoding takes a moment.
        var (bytes, prepError) = await Task.Run(() => UploadImage.Prepare(staged, slot));
        if (bytes is null)
        {
            SaveError = prepError;
            return false;
        }

        var (url, error) = await Services.Platform.UploadPictureAsync(slot, bytes);
        if (url is null)
        {
            SaveError = error;
            return false;
        }
        _pushed.Add(slot);
        return true;
    }

    private static string? ExistingFile(string? path) => path is not null && File.Exists(path) ? path : null;

    private bool IsCurrent(string configKey, string path) =>
        path.Length > 0 && string.Equals(Services.Settings.GetConfig(configKey), path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stores a picked picture under the data folder with a fresh name (so the file being shown and
    /// the one being replaced are never the same file), and deletes the one it replaces.
    /// </summary>
    private void Replace(string configKey, string kind, string staged)
    {
        var old = Services.Settings.GetConfig(configKey);
        string? kept = null;
        if (staged.Length > 0)
        {
            kept = Path.Combine(LauncherPaths.DataDir, $"{kind}-{DateTime.UtcNow:yyyyMMddHHmmssfff}{Path.GetExtension(staged)}");
            File.Copy(staged, kept, overwrite: true);
        }
        Services.Settings.SetConfig(configKey, kept);

        if (old is not null && !string.Equals(old, kept, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(old) is { } dir
            && string.Equals(Path.GetFullPath(dir), Path.GetFullPath(LauncherPaths.DataDir), StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(old); } catch { /* still open somewhere — harmless leftover */ }
        }
    }
}
