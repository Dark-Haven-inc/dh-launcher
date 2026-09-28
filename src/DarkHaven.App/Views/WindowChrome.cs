using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace DarkHaven.App.Views;

/// <summary>
/// Moving and resizing a window with no OS frame (<c>SystemDecorations="None"</c>): its title bar drags it
/// (a double click maximizes), and the Borders of class "grip" along its edges, tagged N, S, W, E, NW, NE,
/// SW or SE, resize it.
/// </summary>
internal static class WindowChrome
{
    public static void Attach(Window window, Control titleBar)
    {
        titleBar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
                return;

            if (e.ClickCount == 2)
                window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else
                window.BeginMoveDrag(e);
        };

        foreach (var grip in window.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("grip")))
        {
            grip.Background = Brushes.Transparent;
            grip.PointerPressed += (_, e) => OnGripPressed(window, grip, e);
        }
    }

    private static void OnGripPressed(Window window, Border grip, PointerPressedEventArgs e)
    {
        if (window.WindowState != WindowState.Normal || grip.Tag is not string tag
            || !e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
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
        window.BeginResizeDrag(edge, e);
        e.Handled = true;
    }
}
