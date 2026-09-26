using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using DarkHaven.App.ViewModels;

namespace DarkHaven.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // No OS frame (SystemDecorations="None"): the top bar moves the window, the edge grips resize it.
        TitleBar.PointerPressed += OnTitleBarPressed;
        foreach (var grip in this.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("grip")))
        {
            grip.Background = Brushes.Transparent;
            grip.PointerPressed += OnGripPressed;
        }

        // Kick off first loads once the window is shown.
        Opened += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                await vm.Regions.RefreshAsync();
        };
    }

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            BeginMoveDrag(e);
    }

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal || sender is not Border { Tag: string tag }
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var edge = tag switch
        {
            "N" => WindowEdge.North,
            "S" => WindowEdge.South,
            "W" => WindowEdge.West,
            "E" => WindowEdge.East,
            "NW" => WindowEdge.NorthWest,
            "NE" => WindowEdge.NorthEast,
            "SW" => WindowEdge.SouthWest,
            _ => WindowEdge.SouthEast,
        };
        BeginResizeDrag(edge, e);
        e.Handled = true;
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
