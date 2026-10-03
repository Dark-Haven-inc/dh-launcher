using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DarkHaven.App.Controls;

/// <summary>
/// The cyberpunk theme's ping: four rising bars, as many lit as the connection deserves (under 80 ms all
/// four, then three, two, one), cyan while it's good, yellow as it gets slow, red when it's bad. No ping,
/// all four dark.
/// </summary>
public sealed class SignalBars : Control
{
    public static readonly StyledProperty<int?> PingProperty =
        AvaloniaProperty.Register<SignalBars, int?>(nameof(Ping));

    public int? Ping
    {
        get => GetValue(PingProperty);
        set => SetValue(PingProperty, value);
    }

    static SignalBars()
    {
        AffectsRender<SignalBars>(PingProperty);
        WidthProperty.OverrideDefaultValue<SignalBars>(18);
        HeightProperty.OverrideDefaultValue<SignalBars>(11);
        VerticalAlignmentProperty.OverrideDefaultValue<SignalBars>(Avalonia.Layout.VerticalAlignment.Center);
    }

    /// <summary>How many bars a ping lights.</summary>
    public static int Lit(int? ping) => ping switch
    {
        null => 0,
        < 80 => 4,
        < 150 => 3,
        < 250 => 2,
        _ => 1,
    };

    private Color ColorOf(string key, string fallback) =>
        this.TryFindResource(key, out var v) && v is Color c ? c : Color.Parse(fallback);

    public override void Render(DrawingContext context)
    {
        var lit = Lit(Ping);
        var on = new SolidColorBrush(lit switch
        {
            >= 3 => ColorOf("DhCyan", "#05D9E8"),
            2 => ColorOf("DhWarn", "#FCEE0A"),
            _ => ColorOf("DhDanger", "#FF4545"),
        });
        var off = new SolidColorBrush(ColorOf("DhBorderStrong", "#3B3B66"));

        var (w, h) = (Bounds.Width, Bounds.Height);
        var bar = (w - 3 * 1.5) / 4;
        for (var i = 0; i < 4; i++)
        {
            var height = h * (i + 1) / 4;
            context.FillRectangle(i < lit ? on : off, new Rect(i * (bar + 1.5), h - height, bar, height));
        }
    }
}
