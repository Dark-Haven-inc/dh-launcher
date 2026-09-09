using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace DarkHaven.App.Controls;

/// <summary>
/// The "КАРТА СЕТИ" — a living star-map of the Dark Haven sector. Regions are star systems,
/// gate links are animated lanes, node size/glow tracks population and status. Pan by dragging,
/// zoom with the wheel, click a system to select it.
/// </summary>
public sealed class SectorMap : Control
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<SectorMap, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<SectorMap, object?>(nameof(SelectedItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    /// <summary>Raised with the clicked node (an <see cref="IMapNode"/>).</summary>
    public event EventHandler<object>? NodeInvoked;

    // palette (kept in sync with App.axaml)
    private static readonly Color Bg = Color.Parse("#0A0F1C");
    private static readonly Color Accent = Color.Parse("#3B82F6");
    private static readonly Color AccentBright = Color.Parse("#5AA0FF");
    private static readonly Color Online = Color.Parse("#4ADE80");
    private static readonly Color Warn = Color.Parse("#F0B454");
    private static readonly Color Offline = Color.Parse("#5A6B8A");
    private static readonly Color Text = Color.Parse("#DCE6F5");
    private static readonly Color Dim = Color.Parse("#7C8DB0");

    private readonly DispatcherTimer _timer;
    private readonly (double X, double Y, double R, double A)[] _stars;
    private double _phase;

    private double _scale = 1.0;
    private Vector _pan;
    private Point? _dragFrom;
    private Vector _dragPanStart;
    private bool _dragged;

    private readonly Typeface _face = new("Inter, Segoe UI, sans-serif");

    public SectorMap()
    {
        ClipToBounds = true;
        Focusable = true;

        var rng = new Random(0x5EC7);
        _stars = new (double, double, double, double)[140];
        for (var i = 0; i < _stars.Length; i++)
            _stars[i] = (rng.NextDouble(), rng.NextDouble(), rng.NextDouble() * 1.4 + 0.3, rng.NextDouble() * 0.5 + 0.15);

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) =>
        {
            _phase += 0.016;
            InvalidateVisual();
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty || change.Property == SelectedItemProperty || change.Property == BoundsProperty)
            InvalidateVisual();
    }

    private IReadOnlyList<IMapNode> Nodes()
    {
        if (ItemsSource is null) return [];
        var list = new List<IMapNode>();
        foreach (var o in ItemsSource)
            if (o is IMapNode n) list.Add(n);
        return list;
    }

    // --- coordinate mapping ---

    private double BasePx => Math.Max(120, Math.Min(Bounds.Width, Bounds.Height) - 130);

    private Point ToScreen(IMapNode n) => ToScreen(new Point(n.X, n.Y));

    private Point ToScreen(Point map)
    {
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        return new Point(
            c.X + (map.X - 0.5) * BasePx * _scale + _pan.X,
            c.Y + (map.Y - 0.5) * BasePx * _scale + _pan.Y);
    }

    private Point ToMap(Point screen)
    {
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        return new Point(
            (screen.X - c.X - _pan.X) / (BasePx * _scale) + 0.5,
            (screen.Y - c.Y - _pan.Y) / (BasePx * _scale) + 0.5);
    }

    // --- interaction ---

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        _dragFrom = p;
        _dragPanStart = _pan;
        _dragged = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from) return;
        var now = e.GetPosition(this);
        var delta = new Vector(now.X - from.X, now.Y - from.Y);
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 3)
            _dragged = true;
        _pan = _dragPanStart + delta;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        var p = e.GetPosition(this);

        if (!_dragged)
        {
            var hit = HitTest(p);
            if (hit is not null)
            {
                SelectedItem = hit;
                NodeInvoked?.Invoke(this, hit);
            }
        }
        _dragFrom = null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var cursor = e.GetPosition(this);
        var before = ToMap(cursor);
        _scale = Math.Clamp(_scale * (e.Delta.Y > 0 ? 1.12 : 1 / 1.12), 0.55, 3.5);
        var after = ToScreen(before);
        _pan += new Vector(cursor.X - after.X, cursor.Y - after.Y);
        InvalidateVisual();
        e.Handled = true;
    }

    private IMapNode? HitTest(Point screen)
    {
        IMapNode? best = null;
        var bestD = double.MaxValue;
        foreach (var n in Nodes())
        {
            var s = ToScreen(n);
            var d = Math.Sqrt((s.X - screen.X) * (s.X - screen.X) + (s.Y - screen.Y) * (s.Y - screen.Y));
            if (d < 26 && d < bestD) { bestD = d; best = n; }
        }
        return best;
    }

    // --- render ---

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        ctx.DrawRectangle(new SolidColorBrush(Bg), null, new Rect(b.Size));

        // parallax starfield
        for (var layer = 0; layer < 2; layer++)
        {
            var par = layer == 0 ? 0.15 : 0.4;
            var off = _pan * par;
            foreach (var s in _stars)
            {
                if ((int)(s.X * 2) != layer && layer == 0) continue;
                var x = (s.X * b.Width + off.X) % b.Width;
                var y = (s.Y * b.Height + off.Y) % b.Height;
                if (x < 0) x += b.Width;
                if (y < 0) y += b.Height;
                var tw = 0.6 + 0.4 * Math.Sin(_phase * 0.7 + s.X * 40);
                ctx.DrawEllipse(new SolidColorBrush(Text, s.A * tw * (layer == 0 ? 0.5 : 1)), null,
                    new Point(x, y), s.R, s.R);
            }
        }

        var nodes = Nodes();
        if (nodes.Count == 0)
        {
            DrawCentered(ctx, "нет данных о секторе", Dim, 13);
            return;
        }

        var byName = new Dictionary<string, IMapNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes) byName[n.Name] = n;

        // lanes
        var drawn = new HashSet<(string, string)>();
        foreach (var n in nodes)
        {
            foreach (var nb in n.Neighbours)
            {
                if (!byName.TryGetValue(nb, out var other)) continue;
                var key = string.CompareOrdinal(n.Name, other.Name) < 0 ? (n.Name, other.Name) : (other.Name, n.Name);
                if (!drawn.Add(key)) continue;

                var a = ToScreen(n);
                var c = ToScreen(other);
                var live = n is { IsOnline: true } || other is { IsOnline: true };
                var laneColor = live ? Accent : Offline;

                ctx.DrawLine(new Pen(new SolidColorBrush(laneColor, 0.22), 1.4), a, c);

                if (live)
                {
                    // a pulse travelling along the lane
                    var t = (_phase * 0.35 + n.X + other.Y) % 1.0;
                    var p = new Point(a.X + (c.X - a.X) * t, a.Y + (c.Y - a.Y) * t);
                    ctx.DrawEllipse(new SolidColorBrush(AccentBright, 0.9), null, p, 2.2, 2.2);
                }
            }
        }

        // nodes
        foreach (var n in nodes)
        {
            var p = ToScreen(n);
            var selected = ReferenceEquals(n, SelectedItem);
            var core = n.IsQuarantine ? Warn : n.IsOnline ? Online : n.IsOffline ? Offline : Dim;
            var baseR = n.IsCentral ? 13.0 : 8.0;
            var pulse = n.IsOnline ? 1 + 0.12 * Math.Sin(_phase * 2.2 + n.X * 10) : 1;
            var r = baseR * pulse;

            // glow
            if (n.IsOnline || selected)
            {
                var g = selected ? AccentBright : core;
                for (var i = 3; i >= 1; i--)
                    ctx.DrawEllipse(new SolidColorBrush(g, 0.06 * i), null, p, r + i * 6, r + i * 6);
            }

            // quarantine ring
            if (n.IsQuarantine)
                ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Warn, 0.5), 1.2) { DashStyle = new DashStyle([3, 3], _phase * 3) },
                    p, r + 5, r + 5);

            ctx.DrawEllipse(new SolidColorBrush(core, n.IsOffline ? 0.5 : 1), null, p, r, r);
            if (n.IsCentral)
                ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Text, 0.85), 1.5), p, r + 3, r + 3);
            if (selected)
                ctx.DrawEllipse(null, new Pen(new SolidColorBrush(AccentBright), 2), p, r + 9, r + 9);

            // label
            var label = Fmt(n.Name, selected ? Text : Dim, selected ? 13 : 12, selected);
            ctx.DrawText(label, new Point(p.X - label.Width / 2, p.Y + r + 6));

            if (n.IsOnline && n.Population is { Length: > 0 } pop && pop != "0")
            {
                var pl = Fmt(pop, AccentBright, 11, false);
                ctx.DrawText(pl, new Point(p.X - pl.Width / 2, p.Y + r + 6 + label.Height));
            }
            else if (n.IsQuarantine)
            {
                var pl = Fmt("карантин", Warn, 10, false);
                ctx.DrawText(pl, new Point(p.X - pl.Width / 2, p.Y + r + 6 + label.Height));
            }
        }

        // hint
        var hint = Fmt("тащить — двигать · колесо — масштаб", Dim, 10, false);
        ctx.DrawText(hint, new Point(14, b.Height - hint.Height - 10));
    }

    private FormattedText Fmt(string text, Color color, double size, bool bold) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(_face.FontFamily, weight: bold ? FontWeight.Bold : FontWeight.Normal),
            size, new SolidColorBrush(color));

    private void DrawCentered(DrawingContext ctx, string text, Color color, double size)
    {
        var t = Fmt(text, color, size, false);
        ctx.DrawText(t, new Point(Bounds.Width / 2 - t.Width / 2, Bounds.Height / 2 - t.Height / 2));
    }
}
