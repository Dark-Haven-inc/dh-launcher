using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DarkHaven.App.ViewModels;

namespace DarkHaven.App.Views;

public partial class ProfileView : UserControl
{
    public ProfileView()
    {
        InitializeComponent();
        PickAvatarButton.Click += PickAvatar;
    }

    private async void PickAvatar(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProfileViewModel vm || TopLevel.GetTopLevel(this) is not { } top)
            return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите изображение",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Изображения") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif"] }],
        });

        if (files is [{ } file] && file.TryGetLocalPath() is { } path)
            vm.SetAvatar(path);
    }
}
