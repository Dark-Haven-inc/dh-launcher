using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DarkHaven.App.Controls;

/// <summary>
/// The medieval theme's box: a frame ruled twice in ink, a strong outer line and a fine inner one, with a
/// small diamond on each corner — the way a scribe boxed a panel on a page. The child sits inside
/// <see cref="Decorator.Padding"/>.
/// </summary>
public sealed class InkFrame : Decorator
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<InkFrame>();

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<InkFrame, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<bool> OrnamentsProperty =
        AvaloniaProperty.Register<InkFrame, bool>(nameof(Ornaments), true);

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>The corner diamonds.</summary>
    public bool Ornaments
    {
        get => GetValue(OrnamentsProperty);
        set => SetValue(OrnamentsProperty, value);
    }

    static InkFrame()
    {
        AffectsRender<InkFrame>(BackgroundProperty, StrokeProperty, OrnamentsProperty);
    }

    /// <summary>Where the inner rule runs, in from the outer one.</summary>
    private const double Gap = 3.5;

    public override void Render(DrawingContext context)
    {
        var all = new Rect(Bounds.Size);
        if (Background is { } background)
            context.FillRectangle(background, all.Deflate(2));
        if (Stroke is not { } ink || all.Width < 12 || all.Height < 12)
            return;

        var outer = all.Deflate(2.6);
        context.DrawRectangle(new Pen(ink, 1.2), outer);
        context.DrawRectangle(new Pen(ink, 0.6) { DashStyle = null }, outer.Deflate(Gap));

        if (!Ornaments)
            return;
        foreach (var corner in new[] { outer.TopLeft, outer.TopRight, outer.BottomRight, outer.BottomLeft })
        {
            var diamond = new StreamGeometry();
            using (var g = diamond.Open())
            {
                g.BeginFigure(corner + new Vector(0, -2.6), true);
                g.LineTo(corner + new Vector(2.6, 0));
                g.LineTo(corner + new Vector(0, 2.6));
                g.LineTo(corner + new Vector(-2.6, 0));
                g.EndFigure(true);
            }
            context.DrawGeometry(ink, null, diamond);
        }
    }
}
