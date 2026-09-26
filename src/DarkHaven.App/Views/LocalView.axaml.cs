using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DarkHaven.App.Views;

public partial class LocalView : UserControl
{
    public LocalView()
    {
        InitializeComponent();

        // The server console follows its newest line, like a terminal — unless the player scrolled up to
        // read something. It scrolls only its own area: moving a caret instead would drag the whole page
        // down to it. The console lives in the selected server's template, so it's found by class.
        AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            if (e.Source is not ScrollViewer { Classes: var classes } sv || !classes.Contains("console") || e.ExtentDelta.Y <= 0)
                return;
            var bottomBefore = sv.Extent.Height - e.ExtentDelta.Y;
            if (sv.Offset.Y + sv.Viewport.Height >= bottomBefore - 30)
                sv.ScrollToEnd();
        }, RoutingStrategies.Bubble);
    }
}
