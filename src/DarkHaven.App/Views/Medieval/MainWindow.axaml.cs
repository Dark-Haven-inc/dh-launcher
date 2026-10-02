using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DarkHaven.App.ViewModels;

namespace DarkHaven.App.Views.Medieval;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // No OS frame (SystemDecorations="None"): the top bar moves the window, the edge grips resize it.
        WindowChrome.Attach(this, TitleBar);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnPaletteBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only a click on the dimmed backdrop itself, not inside the switcher.
        if (e.Source == sender && DataContext is MainWindowViewModel vm)
            vm.ClosePalette();
    }
}
