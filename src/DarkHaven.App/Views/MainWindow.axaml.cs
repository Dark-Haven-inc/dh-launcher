using Avalonia.Controls;
using DarkHaven.App.ViewModels;

namespace DarkHaven.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Kick off first loads once the window is shown.
        Opened += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                await vm.Regions.RefreshAsync();
        };
    }
}
