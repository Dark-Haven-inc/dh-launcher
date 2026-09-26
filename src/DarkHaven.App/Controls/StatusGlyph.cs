using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace DarkHaven.App.Controls;

/// <summary>
/// The launcher's one status mark. Color never carries state, the shape does:
/// ■ online / done / in game, □ quarantine / waiting / idle, ◧ full / in progress, ✕ offline / failed.
/// Set <see cref="Kind"/> directly, or bind the flags and let it pick (quarantine, then offline, then
/// full, then online; none set draws □).
/// </summary>
public sealed class StatusGlyph : Control
{
    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<StatusGlyph, string?>(nameof(Kind));

    public static readonly StyledProperty<bool> IsOnlineProperty =
        AvaloniaProperty.Register<StatusGlyph, bool>(nameof(IsOnline));

    public static readonly StyledProperty<bool> IsFullProperty =
        AvaloniaProperty.Register<StatusGlyph, bool>(nameof(IsFull));

    public static readonly StyledProperty<bool> IsQuarantineProperty =
        AvaloniaProperty.Register<StatusGlyph, bool>(nameof(IsQuarantine));

    public static readonly StyledProperty<bool> IsOfflineProperty =
        AvaloniaProperty.Register<StatusGlyph, bool>(nameof(IsOffline));

    /// <summary>Inherited like text color, so the mark dims with the row it sits in.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<StatusGlyph>();

    /// <summary>"on", "q", "full" or "off"; wins over the flags.</summary>
    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public bool IsOnline
    {
        get => GetValue(IsOnlineProperty);
        set => SetValue(IsOnlineProperty, value);
    }

    public bool IsFull
    {
        get => GetValue(IsFullProperty);
        set => SetValue(IsFullProperty, value);
    }

    public bool IsQuarantine
    {
        get => GetValue(IsQuarantineProperty);
        set => SetValue(IsQuarantineProperty, value);
    }

    public bool IsOffline
    {
        get => GetValue(IsOfflineProperty);
        set => SetValue(IsOfflineProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    static StatusGlyph()
    {
        AffectsRender<StatusGlyph>(KindProperty, IsOnlineProperty, IsFullProperty, IsQuarantineProperty,
            IsOfflineProperty, ForegroundProperty);
        WidthProperty.OverrideDefaultValue<StatusGlyph>(6);
        HeightProperty.OverrideDefaultValue<StatusGlyph>(6);
        VerticalAlignmentProperty.OverrideDefaultValue<StatusGlyph>(Avalonia.Layout.VerticalAlignment.Center);
    }

    private string Resolved => Kind ?? (IsQuarantine ? "q" : IsOffline ? "off" : IsFull ? "full" : IsOnline ? "on" : "q");

    public override void Render(DrawingContext ctx)
    {
        var brush = Foreground ?? Brushes.White;
        var s = Math.Min(Bounds.Width, Bounds.Height);
        var box = new Rect(0, 0, s, s);
        var pen = new Pen(brush, 1);
        var inner = box.Deflate(0.5);

        switch (Resolved)
        {
            case "on":
                ctx.FillRectangle(brush, box);
                break;
            case "full":
                ctx.DrawRectangle(null, pen, inner);
                ctx.FillRectangle(brush, new Rect(0, 0, s / 2, s));
                break;
            case "off":
                ctx.DrawLine(pen, new Point(0, 0), new Point(s, s));
                ctx.DrawLine(pen, new Point(s, 0), new Point(0, s));
                break;
            default:
                ctx.DrawRectangle(null, pen, inner);
                break;
        }
    }
}
