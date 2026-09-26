using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DarkHaven.App.Views;

public partial class LocalView : UserControl
{
    public LocalView()
    {
        InitializeComponent();

        // The server console follows its newest line, like a terminal. The box lives inside the selected
        // server's template, so it's caught by its class as the event bubbles up rather than by name.
        AddHandler(TextBox.TextChangedEvent, (_, e) =>
        {
            if (e.Source is TextBox { Classes: var classes } box && classes.Contains("console"))
                box.CaretIndex = box.Text?.Length ?? 0;
        }, RoutingStrategies.Bubble);
    }
}
