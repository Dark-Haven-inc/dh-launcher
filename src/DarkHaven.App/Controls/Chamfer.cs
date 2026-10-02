using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DarkHaven.App.Controls;

/// <summary>Which corners a <see cref="Chamfer"/> cuts.</summary>
[Flags]
public enum ChamferCorners
{
    None = 0,
    TopLeft = 1,
    TopRight = 2,
    BottomRight = 4,
    BottomLeft = 8,
}

/// <summary>
/// The cyberpunk theme's box: a <see cref="Border"/> with corners cut off at 45° instead of rounded —
/// the window's frame, its buttons. Fill and outline follow the cut; the child sits inside
/// <see cref="Decorator.Padding"/> as in any decorator.
/// </summary>
public sealed class Chamfer : Decorator
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<Chamfer>();

    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        Border.BorderBrushProperty.AddOwner<Chamfer>();

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Chamfer, double>(nameof(StrokeThickness), 1);

    public static readonly StyledProperty<double> CutProperty =
        AvaloniaProperty.Register<Chamfer, double>(nameof(Cut), 8);

    public static readonly StyledProperty<ChamferCorners> CornersProperty =
        AvaloniaProperty.Register<Chamfer, ChamferCorners>(nameof(Corners), ChamferCorners.TopLeft | ChamferCorners.BottomRight);

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? BorderBrush
    {
        get => GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>How far along each edge a cut corner starts.</summary>
    public double Cut
    {
        get => GetValue(CutProperty);
        set => SetValue(CutProperty, value);
    }

    public ChamferCorners Corners
    {
        get => GetValue(CornersProperty);
        set => SetValue(CornersProperty, value);
    }

    static Chamfer()
    {
        AffectsRender<Chamfer>(BackgroundProperty, BorderBrushProperty, StrokeThicknessProperty, CutProperty, CornersProperty);
    }

    public override void Render(DrawingContext context)
    {
        var stroke = BorderBrush is { } b && StrokeThickness > 0 ? new Pen(b, StrokeThickness) : null;
        if (Background is null && stroke is null)
            return;

        // The outline is centred on the edge of the area, half a stroke in.
        var half = stroke is null ? 0 : StrokeThickness / 2;
        var area = new Rect(Bounds.Size).Deflate(half);
        if (area.Width <= 0 || area.Height <= 0)
            return;

        context.DrawGeometry(Background, stroke, Outline(area, Math.Min(Cut, Math.Min(area.Width, area.Height) / 2), Corners));
    }

    private static StreamGeometry Outline(Rect r, double cut, ChamferCorners corners)
    {
        double At(ChamferCorners c) => corners.HasFlag(c) ? cut : 0;
        var (tl, tr, br, bl) = (At(ChamferCorners.TopLeft), At(ChamferCorners.TopRight), At(ChamferCorners.BottomRight), At(ChamferCorners.BottomLeft));

        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(new Point(r.Left + tl, r.Top), isFilled: true);
        g.LineTo(new Point(r.Right - tr, r.Top));
        g.LineTo(new Point(r.Right, r.Top + tr));
        g.LineTo(new Point(r.Right, r.Bottom - br));
        g.LineTo(new Point(r.Right - br, r.Bottom));
        g.LineTo(new Point(r.Left + bl, r.Bottom));
        g.LineTo(new Point(r.Left, r.Bottom - bl));
        g.LineTo(new Point(r.Left, r.Top + tl));
        g.EndFigure(isClosed: true);
        return geometry;
    }
}
