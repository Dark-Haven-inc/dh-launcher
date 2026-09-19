using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DarkHaven.App.ViewModels;

namespace DarkHaven.App.Views;

public partial class ProfileView : UserControl
{
    private static readonly FilePickerFileType Images =
        new("Изображения") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif"] };

    public ProfileView()
    {
        InitializeComponent();
        PickAvatarButton.Click += async (_, _) => { if (await PickImageAsync("Выберите аватар") is { } p) Vm?.StageAvatar(p); };
        PickBannerButton.Click += async (_, _) => { if (await PickImageAsync("Выберите баннер") is { } p) Vm?.StageBanner(p); };
    }

    private ProfileViewModel? Vm => DataContext as ProfileViewModel;

    private async Task<string?> PickImageAsync(string title)
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return null;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [Images],
        });
        return files is [{ } file] ? file.TryGetLocalPath() : null;
    }
}
