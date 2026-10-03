using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DarkHaven.App.Controls;

/// <summary>
/// The medieval theme's main button face: a blob of sealing wax — a round that isn't quite round, a
/// pressed ring inside it, a little light on its upper left. It only draws, filling its bounds; the
/// button's content goes on top of it (Themes/Styles/Medieval.axaml, Button.seal).
/// </summary>
public sealed class WaxSeal : Control
{
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<WaxSeal, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> RingProperty =
        AvaloniaProperty.Register<WaxSeal, IBrush?>(nameof(Ring));

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>The pressed ring and the shadow under the edge, darker than the wax.</summary>
    public IBrush? Ring
    {
        get => GetValue(RingProperty);
        set => SetValue(RingProperty, value);
    }

    static WaxSeal()
    {
        AffectsRender<WaxSeal>(FillProperty, RingProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<WaxSeal>(false);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < 8 || Fill is not { } wax)
            return;

        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var r = size / 2 - 1.5;

        // The blob: a radius that swells and dips a little all the way round.
        var blob = new StreamGeometry();
        using (var g = blob.Open())
        {
            const int steps = 72;
            for (var i = 0; i <= steps; i++)
            {
                var a = i * Math.PI * 2 / steps;
                var k = 1 + 0.035 * Math.Sin(a * 5 + 0.7) + 0.025 * Math.Sin(a * 9 + 2.1) + 0.015 * Math.Sin(a * 14);
                var at = c + new Vector(Math.Cos(a), Math.Sin(a)) * r * k * 0.95;
                if (i == 0) g.BeginFigure(at, true); else g.LineTo(at);
            }
            g.EndFigure(true);
        }

        if (Ring is { } ring)
        {
            using (context.PushTransform(Matrix.CreateTranslation(1.2, 1.8)))
            using (context.PushOpacity(0.45))
                context.DrawGeometry(ring, null, blob);
        }
        context.DrawGeometry(wax, null, blob);

        if (Ring is { } pressed)
        {
            context.DrawEllipse(null, new Pen(pressed, 1.6), c, r * 0.72, r * 0.72);
            context.DrawEllipse(null, new Pen(pressed, 0.7) { DashStyle = new DashStyle([1, 2.5], 0) }, c, r * 0.62, r * 0.62);
        }

        // Light on the wax, upper left.
        var shine = new RadialGradientBrush
        {
            Center = new RelativePoint(0.32, 0.28, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.32, 0.28, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.45, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.45, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x38, 0xFF, 0xF4, 0xE0), 0),
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xF4, 0xE0), 1),
            },
        };
        context.DrawGeometry(shine, null, blob);
    }
}
