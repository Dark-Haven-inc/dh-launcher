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
    private double _targetScale = 1.0;
    private Point _zoomAnchorScreen;
    private Point _zoomAnchorMap;
    private Vector _pan;
    private Point? _dragFrom;
    private Vector _dragPanStart;
    private bool _dragged;
    private IMapNode? _hover;
    private Point _hoverAt;
    private bool _needsFit = true;

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

            if (Math.Abs(_targetScale - _scale) > 0.0005)
            {
                _scale += (_targetScale - _scale) * 0.25;
                // keep the point under the cursor fixed while the zoom eases in
                var anchorNow = ToScreen(_zoomAnchorMap);
                _pan += new Vector(_zoomAnchorScreen.X - anchorNow.X, _zoomAnchorScreen.Y - anchorNow.Y);
            }

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
        if (change.Property == ItemsSourceProperty)
            _needsFit = true;
        if (change.Property == ItemsSourceProperty || change.Property == SelectedItemProperty || change.Property == BoundsProperty)
            InvalidateVisual();
    }

    /// <summary>Frame all regions with a little padding, centred, at the start and on data changes.</summary>
    private void Fit(IReadOnlyList<IMapNode> nodes)
    {
        _needsFit = false;
        _scale = _targetScale = 1.0;
        _pan = default;

        double minX = 1, minY = 1, maxX = 0, maxY = 0;
        foreach (var n in nodes)
        {
            minX = Math.Min(minX, n.X); maxX = Math.Max(maxX, n.X);
            minY = Math.Min(minY, n.Y); maxY = Math.Max(maxY, n.Y);
        }

        var spanX = Math.Max(0.15, maxX - minX);
        var spanY = Math.Max(0.15, maxY - minY);
        // BasePx maps a span of 1.0; leave ~22% breathing room around the cluster
        _targetScale = _scale = Math.Clamp(0.78 / Math.Max(spanX, spanY), 0.6, 2.2);

        var midMap = new Point((minX + maxX) / 2, (minY + maxY) / 2);
        var mid = ToScreen(midMap);
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        _pan = new Vector(c.X - mid.X, c.Y - mid.Y);
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
        var now = e.GetPosition(this);

        if (_dragFrom is { } from)
        {
            var delta = new Vector(now.X - from.X, now.Y - from.Y);
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 3)
                _dragged = true;
            _pan = _dragPanStart + delta;
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

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is not null)
        {
            _hover = null;
            InvalidateVisual();
        }
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
        _zoomAnchorScreen = cursor;
        _zoomAnchorMap = ToMap(cursor);
        _targetScale = Math.Clamp(_targetScale * (e.Delta.Y > 0 ? 1.18 : 1 / 1.18), 0.55, 3.5);
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

        if (_needsFit && b.Width > 1 && b.Height > 1)
            Fit(nodes);

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

            // "you are here"
            if (n.IsCurrent)
            {
                var badge = Fmt("ВЫ ЗДЕСЬ", Online, 9, true);
                var bw = badge.Width + 12;
                var br = new Rect(p.X - bw / 2, p.Y - r - 22, bw, 15);
                ctx.DrawRectangle(new SolidColorBrush(Online, 0.15), new Pen(new SolidColorBrush(Online), 1),
                    br, 4, 4);
                ctx.DrawText(badge, new Point(br.X + 6, br.Y + 2));
                var ringP = 1 + 0.18 * Math.Sin(_phase * 3);
                ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Online, 0.7), 1.5), p, (r + 7) * ringP, (r + 7) * ringP);
            }

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

        // hover tooltip
        if (_hover is { } hv)
            DrawTooltip(ctx, hv);

        // hint
        var hint = Fmt("тащить — двигать · колесо — масштаб", Dim, 10, false);
        ctx.DrawText(hint, new Point(14, b.Height - hint.Height - 10));
    }

    private void DrawTooltip(DrawingContext ctx, IMapNode n)
    {
        const double w = 210;
        var title = Fmt(n.Name, Text, 12, true);
        var body = new FormattedText(
            string.IsNullOrWhiteSpace(n.Blurb) ? "—" : n.Blurb,
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(_face.FontFamily), 11, new SolidColorBrush(Dim)) { MaxTextWidth = w - 20 };

        var h = 14 + title.Height + 4 + body.Height + 12;
        var x = Math.Clamp(_hoverAt.X + 16, 6, Bounds.Width - w - 6);
        var y = Math.Clamp(_hoverAt.Y + 16, 6, Bounds.Height - h - 6);
        var rect = new Rect(x, y, w, h);

        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#111A2E"), 0.97),
            new Pen(new SolidColorBrush(Accent, 0.6), 1), rect, 6, 6);
        ctx.DrawText(title, new Point(x + 10, y + 8));
        ctx.DrawText(body, new Point(x + 10, y + 8 + title.Height + 4));
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
