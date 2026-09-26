using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DarkHaven.App.ViewModels;

namespace DarkHaven.App.Views;

public partial class PaletteView : UserControl
{
    public PaletteView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Input.Focus();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not PaletteViewModel vm)
            return;

        switch (e.Key)
        {
            case Key.Down: vm.Move(1); break;
            case Key.Up: vm.Move(-1); break;
            case Key.Enter: vm.Pick(); break;
            case Key.Escape: vm.Close(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnHitClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PaletteViewModel vm && sender is Button { DataContext: PaletteItem item })
            vm.Pick(item);
    }
}
