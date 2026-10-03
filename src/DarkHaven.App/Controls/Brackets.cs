using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DarkHaven.App.Controls;

/// <summary>
/// The cyberpunk theme's targeting corners: an L at each corner of its bounds, nothing between them.
/// Laid over a panel (it only draws; the pointer goes through) to mark it as the one in focus.
/// </summary>
public sealed class Brackets : Control
{
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Brackets, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<double> ArmProperty =
        AvaloniaProperty.Register<Brackets, double>(nameof(Arm), 10);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Brackets, double>(nameof(StrokeThickness), 1.5);

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>How long each arm of an L is.</summary>
    public double Arm
    {
        get => GetValue(ArmProperty);
        set => SetValue(ArmProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    static Brackets()
    {
        AffectsRender<Brackets>(StrokeProperty, ArmProperty, StrokeThicknessProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<Brackets>(false);
    }

    public override void Render(DrawingContext context)
    {
        if (Stroke is not { } brush)
            return;
        var half = StrokeThickness / 2;
        var r = new Rect(Bounds.Size).Deflate(half);
        var pen = new Pen(brush, StrokeThickness) { LineCap = PenLineCap.Square };
        var arm = Math.Min(Arm, Math.Min(r.Width, r.Height) / 3);
        foreach (var (corner, sx, sy) in new[] { (r.TopLeft, 1, 1), (r.TopRight, -1, 1), (r.BottomRight, -1, -1), (r.BottomLeft, 1, -1) })
        {
            context.DrawLine(pen, corner, corner + new Vector(sx * arm, 0));
            context.DrawLine(pen, corner, corner + new Vector(0, sy * arm));
        }
    }
}
