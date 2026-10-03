using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DarkHaven.App.Controls;

/// <summary>
/// The medieval theme's banner: a ribbon across the child, its two tails folded back behind it and cut
/// in a swallowtail, as a title is lettered on an old map. The child sits on the band, inside
/// <see cref="Decorator.Padding"/> (which should leave room for the tails at the sides).
/// </summary>
public sealed class Ribbon : Decorator
{
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<Ribbon, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> ShadeProperty =
        AvaloniaProperty.Register<Ribbon, IBrush?>(nameof(Shade));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Ribbon, IBrush?>(nameof(Stroke));

    /// <summary>The band.</summary>
    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>The tails and the folds, darker than the band.</summary>
    public IBrush? Shade
    {
        get => GetValue(ShadeProperty);
        set => SetValue(ShadeProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    static Ribbon()
    {
        AffectsRender<Ribbon>(FillProperty, ShadeProperty, StrokeProperty);
    }

    /// <summary>How far each tail reaches out past the band, and how much lower it hangs.</summary>
    private const double Tail = 22, Drop = 7;

    public override void Render(DrawingContext context)
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (w < Tail * 3 || h < Drop * 3)
            return;

        var pen = Stroke is { } s ? new Pen(s, 1) { LineJoin = PenLineJoin.Round } : null;
        var band = new Rect(Tail, 0, w - 2 * Tail, h - Drop);

        // The tails, behind the band: down and out, a swallowtail cut at the end.
        foreach (var side in new[] { -1, 1 })
        {
            var inner = side < 0 ? band.Left + 8 : band.Right - 8;
            var outer = side < 0 ? 0.0 : w;
            var tail = new StreamGeometry();
            using (var g = tail.Open())
            {
                g.BeginFigure(new Point(inner, Drop), true);
                g.LineTo(new Point(outer, Drop));
                g.LineTo(new Point(outer - side * 9, Drop + (h - Drop) / 2));
                g.LineTo(new Point(outer, h));
                g.LineTo(new Point(inner, h));
                g.EndFigure(true);
            }
            context.DrawGeometry(Shade, pen, tail);

            // The fold where the band turns back into the tail.
            var fold = new StreamGeometry();
            using (var g = fold.Open())
            {
                var edge = side < 0 ? band.Left : band.Right;
                g.BeginFigure(new Point(edge, band.Bottom), true);
                g.LineTo(new Point(edge + side * 8, h));
                g.LineTo(new Point(edge + side * 8, band.Bottom));
                g.EndFigure(true);
            }
            context.DrawGeometry(Stroke ?? Shade, null, fold);
        }

        context.DrawRectangle(Fill, pen, band);
    }
}
