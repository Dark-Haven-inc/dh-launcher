using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DarkHaven.App.Controls;

/// <summary>
/// The medieval theme's window border, the neatline of an old map: a strong outer rule and a fine inner
/// one, the band between them cut into cells inked in turn (the degree scale of a map's margin), with
/// solid squares at the corners. It only draws, along the edges of its own bounds; whatever sits inside
/// keeps <see cref="Inside"/> clear of it.
/// </summary>
public sealed class MapFrame : Control
{
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<MapFrame, IBrush?>(nameof(Stroke));

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    static MapFrame()
    {
        AffectsRender<MapFrame>(StrokeProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<MapFrame>(false);
    }

    /// <summary>Outer rule, band, inner rule — how far in from the edge each starts.</summary>
    private const double Outer = 4, Band = 6, Inner = 10;

    /// <summary>How much of the window's edge the frame takes, for the content's margin.</summary>
    public static readonly Thickness Inside = new(Inner + 4);

    /// <summary>About this long, each cell of the band; sides divide into a whole number of them.</summary>
    private const double Cell = 26;

    public override void Render(DrawingContext context)
    {
        if (Stroke is not { } ink || Bounds.Width < Inner * 3 || Bounds.Height < Inner * 3)
            return;

        var (w, h) = (Bounds.Width, Bounds.Height);
        context.DrawRectangle(null, new Pen(ink, 1.5), new Rect(Outer + 0.75, Outer + 0.75, w - 2 * Outer - 1.5, h - 2 * Outer - 1.5));
        context.DrawRectangle(null, new Pen(ink, 1), new Rect(Inner + 0.5, Inner + 0.5, w - 2 * Inner - 1, h - 2 * Inner - 1));

        var band = Inner - Band;
        // The corners: a solid square where the bands meet.
        foreach (var (x, y) in new[] { (Band, Band), (w - Inner, Band), (Band, h - Inner), (w - Inner, h - Inner) })
            context.FillRectangle(ink, new Rect(x, y, band, band));

        // Along each side, every other cell inked; opposite sides mirror each other.
        Cells(w - 2 * Inner, (from, length) =>
        {
            context.FillRectangle(ink, new Rect(Inner + from, Band, length, band));
            context.FillRectangle(ink, new Rect(Inner + from, h - Inner, length, band));
        });
        Cells(h - 2 * Inner, (from, length) =>
        {
            context.FillRectangle(ink, new Rect(Band, Inner + from, band, length));
            context.FillRectangle(ink, new Rect(w - Inner, Inner + from, band, length));
        });
    }

    private static void Cells(double side, Action<double, double> inked)
    {
        var count = Math.Max(3, (int)Math.Round(side / Cell));
        if (count % 2 == 0)
            count++; // odd, so both ends of a side are blank, next to the inked corners
        var length = side / count;
        for (var i = 1; i < count; i += 2)
            inked(i * length, length);
    }
}
