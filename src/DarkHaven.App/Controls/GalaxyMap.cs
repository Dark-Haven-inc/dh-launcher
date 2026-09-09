using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace DarkHaven.App.Controls;

/// <summary>What <see cref="GalaxyMap"/> needs from each server.</summary>
public interface IGalaxyNode
{
    string Name { get; }
    string Address { get; }
    int Players { get; }
    bool IsOnline { get; }
    bool IsFavorite { get; }
}

/// <summary>
/// The public SS14 hub as a galaxy: every server a star, placed by a stable hash of its address so
/// positions don't jump between refreshes, sized by population, brighter when it's a favourite.
/// Pan by dragging, zoom on the wheel, click a star to connect.
/// </summary>
public sealed class GalaxyMap : Control
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<GalaxyMap, IEnumerable?>(nameof(ItemsSource));

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public event EventHandler<object>? NodeInvoked;

    private static readonly Color Bg = Color.Parse("#0A0F1C");
    private static readonly Color Accent = Color.Parse("#5AA0FF");
    private static readonly Color Online = Color.Parse("#4ADE80");
    private static readonly Color Offline = Color.Parse("#5A6B8A");
    private static readonly Color Warn = Color.Parse("#F0B454");
    private static readonly Color Text = Color.Parse("#DCE6F5");
    private static readonly Color Dim = Color.Parse("#7C8DB0");

    private readonly DispatcherTimer _timer;
    private readonly (double X, double Y, double R, double A)[] _stars;
    private readonly Typeface _face = new("Inter, Segoe UI, sans-serif");
    private double _phase;

    private double _scale = 1.0;
    private Vector _pan;
    private Point? _dragFrom;
    private Vector _dragPanStart;
    private bool _dragged;
    private IGalaxyNode? _hover;
    private Point _hoverAt;

    public GalaxyMap()
    {
        ClipToBounds = true;
        var rng = new Random(0x6A1A);
        _stars = new (double, double, double, double)[220];
        for (var i = 0; i < _stars.Length; i++)
            _stars[i] = (rng.NextDouble(), rng.NextDouble(), rng.NextDouble() * 1.3 + 0.3, rng.NextDouble() * 0.45 + 0.12);

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) =>
        {
            _phase += 0.02;
            InvalidateVisual();
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _timer.Start(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { base.OnDetachedFromVisualTree(e); _timer.Stop(); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty || change.Property == BoundsProperty)
            InvalidateVisual();
    }

    private IReadOnlyList<IGalaxyNode> Nodes()
    {
        if (ItemsSource is null) return [];
        var list = new List<IGalaxyNode>();
        foreach (var o in ItemsSource)
            if (o is IGalaxyNode n) list.Add(n);
        return list;
    }

    private double BasePx => Math.Max(160, Math.Min(Bounds.Width, Bounds.Height) - 60);

    /// <summary>Stable position in the unit disc from the server address.</summary>
    private static Point Placement(string address)
    {
        uint h = 2166136261;
        foreach (var c in address)
            h = (h ^ c) * 16777619;

        var angle = (h & 0xFFFF) / 65535.0 * Math.Tau;
        var radius = 0.10 + 0.90 * Math.Sqrt(((h >> 16) & 0xFFFF) / 65535.0);
        return new Point(0.5 + Math.Cos(angle) * radius * 0.62, 0.5 + Math.Sin(angle) * radius * 0.62);
    }

    private Point ToScreen(Point unit)
    {
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        return new Point(
            c.X + (unit.X - 0.5) * BasePx * _scale + _pan.X,
            c.Y + (unit.Y - 0.5) * BasePx * _scale + _pan.Y);
    }

    private Point ToUnit(Point screen)
    {
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        return new Point(
            (screen.X - c.X - _pan.X) / (BasePx * _scale) + 0.5,
            (screen.Y - c.Y - _pan.Y) / (BasePx * _scale) + 0.5);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragFrom = e.GetPosition(this);
        _dragPanStart = _pan;
        _dragged = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var now = e.GetPosition(this);

        if (_dragFrom is { } from)
        {
            var d = new Vector(now.X - from.X, now.Y - from.Y);
            if (Math.Abs(d.X) + Math.Abs(d.Y) > 3) _dragged = true;
            _pan = _dragPanStart + d;
            _hover = null;
            InvalidateVisual();
            return;
        }

        var hit = HitTest(now);
        if (!ReferenceEquals(hit, _hover))
        {
            _hover = hit;
            Cursor = new Cursor(hit is null ? StandardCursorType.Arrow : StandardCursorType.Hand);
            InvalidateVisual();
        }
        _hoverAt = now;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        if (!_dragged && HitTest(e.GetPosition(this)) is { } hit)
            NodeInvoked?.Invoke(this, hit);
        _dragFrom = null;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is not null) { _hover = null; InvalidateVisual(); }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var cursor = e.GetPosition(this);
        var before = ToUnit(cursor);
        _scale = Math.Clamp(_scale * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), 0.5, 6.0);
        var after = ToScreen(before);
        _pan += new Vector(cursor.X - after.X, cursor.Y - after.Y);
        InvalidateVisual();
        e.Handled = true;
    }

    private IGalaxyNode? HitTest(Point screen)
    {
        IGalaxyNode? best = null;
        var bestD = double.MaxValue;
        foreach (var n in Nodes())
        {
            var s = ToScreen(Placement(n.Address));
            var d = Math.Sqrt((s.X - screen.X) * (s.X - screen.X) + (s.Y - screen.Y) * (s.Y - screen.Y));
            var r = NodeRadius(n) + 6;
            if (d < r && d < bestD) { bestD = d; best = n; }
        }
        return best;
    }

    private static double NodeRadius(IGalaxyNode n) => 1.8 + Math.Min(7, Math.Sqrt(Math.Max(0, n.Players)) * 0.95);

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        ctx.DrawRectangle(new SolidColorBrush(Bg), null, new Rect(b.Size));

        foreach (var s in _stars)
        {
            var x = ((s.X * b.Width + _pan.X * 0.25) % b.Width + b.Width) % b.Width;
            var y = ((s.Y * b.Height + _pan.Y * 0.25) % b.Height + b.Height) % b.Height;
            var tw = 0.6 + 0.4 * Math.Sin(_phase * 0.6 + s.X * 30);
            ctx.DrawEllipse(new SolidColorBrush(Text, s.A * tw), null, new Point(x, y), s.R, s.R);
        }

        var nodes = Nodes();
        if (nodes.Count == 0)
        {
            var t = Fmt("нет серверов", Dim, 13, false);
            ctx.DrawText(t, new Point(b.Width / 2 - t.Width / 2, b.Height / 2));
            return;
        }

        foreach (var n in nodes)
        {
            var p = ToScreen(Placement(n.Address));
            if (p.X < -20 || p.Y < -20 || p.X > b.Width + 20 || p.Y > b.Height + 20)
                continue;

            var r = NodeRadius(n);
            var color = !n.IsOnline ? Offline : n.IsFavorite ? Warn : n.Players > 0 ? Online : Accent;

            if (n.IsOnline && n.Players > 20)
                ctx.DrawEllipse(new SolidColorBrush(color, 0.10), null, p, r + 4, r + 4);

            ctx.DrawEllipse(new SolidColorBrush(color, n.IsOnline ? 1 : 0.5), null, p, r, r);
            if (n.IsFavorite)
                ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Warn, 0.8), 1), p, r + 3, r + 3);
        }

        if (_hover is { } hv)
            DrawTooltip(ctx, hv);

        var hint = Fmt($"{nodes.Count} серверов · тащить — двигать · колесо — масштаб", Dim, 10, false);
        ctx.DrawText(hint, new Point(14, b.Height - hint.Height - 10));
    }

    private void DrawTooltip(DrawingContext ctx, IGalaxyNode n)
    {
        const double w = 240;
        var title = Fmt(n.Name, Text, 12, true);
        title.MaxTextWidth = w - 20;
        var sub = Fmt(n.IsOnline ? $"{n.Players} игроков · {n.Address}" : $"офлайн · {n.Address}", Dim, 10, false);
        sub.MaxTextWidth = w - 20;

        var h = 12 + title.Height + 3 + sub.Height + 10;
        var x = Math.Clamp(_hoverAt.X + 14, 6, Bounds.Width - w - 6);
        var y = Math.Clamp(_hoverAt.Y + 14, 6, Bounds.Height - h - 6);
        var rect = new Rect(x, y, w, h);

        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#111A2E"), 0.97), new Pen(new SolidColorBrush(Accent, 0.6), 1), rect, 6, 6);
        ctx.DrawText(title, new Point(x + 10, y + 7));
        ctx.DrawText(sub, new Point(x + 10, y + 7 + title.Height + 3));
    }

    private FormattedText Fmt(string text, Color color, double size, bool bold) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(_face.FontFamily, weight: bold ? FontWeight.Bold : FontWeight.Normal),
            size, new SolidColorBrush(color));
}
