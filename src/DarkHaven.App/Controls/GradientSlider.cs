using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace DarkHaven.App.Controls;

/// <summary>
/// A value picked along a gradient: <see cref="Track"/> fills the bar, a thin mark stands at the
/// value. Click or drag to set it; arrow keys nudge it by 1% of the range (Shift: 10%).
/// </summary>
public sealed class GradientSlider : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<GradientSlider, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<GradientSlider, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<GradientSlider, double>(nameof(Maximum), 1);

    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<GradientSlider, IBrush?>(nameof(Track));

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    private bool _dragging;

    static GradientSlider()
    {
        AffectsRender<GradientSlider>(ValueProperty, MinimumProperty, MaximumProperty, TrackProperty, IsFocusedProperty);
        FocusableProperty.OverrideDefaultValue<GradientSlider>(true);
        HeightProperty.OverrideDefaultValue<GradientSlider>(12);
    }

    public GradientSlider()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        ResourcesChanged += (_, _) => InvalidateVisual();
    }

    private double Range => Math.Max(1e-9, Maximum - Minimum);

    public override void Render(DrawingContext ctx)
    {
        var bar = new Rect(Bounds.Size).Deflate(new Thickness(0, 2));
        if (Track is { } track)
            ctx.FillRectangle(track, bar);
        ctx.DrawRectangle(null, new Pen(Brush(IsFocused ? "DhTextDimBrush" : "DhBorderStrongBrush"), 1), bar.Deflate(0.5));

        // The mark: light with a dark rim, so it reads on either end of any gradient.
        var x = Math.Round(bar.Left + bar.Width * Math.Clamp((Value - Minimum) / Range, 0, 1));
        var mark = new Rect(Math.Clamp(x - 2, 0, Bounds.Width - 4), 0, 4, Bounds.Height);
        ctx.DrawRectangle(Brush("DhTextBrush"), new Pen(Brush("DhBgBrush"), 1), mark.Deflate(0.5));
    }

    private IBrush Brush(string key) => this.TryFindResource(key, out var v) && v is IBrush b ? b : Brushes.Gray;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _dragging = true;
        e.Pointer.Capture(this);
        Focus();
        SetFrom(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
            SetFrom(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = Range / 100 * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1);
        double? to = e.Key switch
        {
            Key.Left or Key.Down => Value - step,
            Key.Right or Key.Up => Value + step,
            Key.Home => Minimum,
            Key.End => Maximum,
            _ => null,
        };
        if (to is { } v)
        {
            Set(v);
            e.Handled = true;
        }
    }

    private void SetFrom(Point p) => Set(Minimum + Range * Math.Clamp(p.X / Math.Max(1, Bounds.Width), 0, 1));

    // SetCurrentValue keeps a two-way binding in place and pushes the value through it.
    private void Set(double v) => SetCurrentValue(ValueProperty, Math.Clamp(v, Minimum, Maximum));
}
