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

    /// <summary>Which network/operator this server belongs to (e.g. "Corvax") — servers sharing a
    /// label are placed near each other and get a shared label on the map instead of scattering
    /// independently. Empty/unclustered servers place individually.</summary>
    string NetworkLabel { get; }
}

/// <summary>
/// The РУхаб as a galaxy: every server a glowing beacon, placed by a stable hash of its address so
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

    private static readonly Color Bg = Color.Parse("#070B14");
    private static readonly Color Accent = Color.Parse("#5AA0FF");
    private static readonly Color Online = Color.Parse("#4ADE80");
    private static readonly Color Offline = Color.Parse("#4E5F80");
    private static readonly Color Warn = Color.Parse("#F0B454");
    private static readonly Color Text = Color.Parse("#DCE6F5");
    private static readonly Color Dim = Color.Parse("#8395B8");
    private static readonly Color FrontierRed = Color.Parse("#F0384C");
    private static readonly Color[] NebColors = [Color.Parse("#1E3A8A"), Color.Parse("#5A2A7A"), Color.Parse("#0E5C6E"), Color.Parse("#7A1E3A")];

    private readonly DispatcherTimer _timer;
    private readonly Star[] _stars;
    private readonly Nebula[] _nebulae;
    private readonly Typeface _face = new("Inter, Segoe UI, sans-serif");
    private readonly Typeface _faceBold = new("Inter, Segoe UI, sans-serif", weight: FontWeight.Bold);
    private double _phase;

    private double _scale = 1.0;
    private Vector _pan;
    private Point? _dragFrom;
    private Vector _dragPanStart;
    private bool _dragged;
    private IGalaxyNode? _hover;
    private Point _hoverAt;

    private readonly struct Star(double x, double y, double r, double a, int layer)
    {
        public readonly double X = x, Y = y, R = r, A = a;
        public readonly int Layer = layer;
    }

    private readonly struct Nebula(double x, double y, double r, Color c, double drift, double phase)
    {
        public readonly double X = x, Y = y, R = r, Drift = drift, Phase = phase;
        public readonly Color C = c;
    }

    public GalaxyMap()
    {
        ClipToBounds = true;
        var rng = new Random(0x6A1A);

        _stars = new Star[260];
        for (var i = 0; i < _stars.Length; i++)
        {
            var layer = i < 150 ? 0 : i < 230 ? 1 : 2;
            var size = layer switch { 0 => rng.NextDouble() * 0.7 + 0.25, 1 => rng.NextDouble() * 1.1 + 0.5, _ => rng.NextDouble() * 1.6 + 0.8 };
            var alpha = layer switch { 0 => rng.NextDouble() * 0.25 + 0.08, 1 => rng.NextDouble() * 0.4 + 0.15, _ => rng.NextDouble() * 0.5 + 0.28 };
            _stars[i] = new Star(rng.NextDouble(), rng.NextDouble(), size, alpha, layer);
        }

        _nebulae = new Nebula[4];
        for (var i = 0; i < _nebulae.Length; i++)
            _nebulae[i] = new Nebula(rng.NextDouble(), rng.NextDouble(), rng.NextDouble() * 240 + 200,
                NebColors[i % NebColors.Length], rng.NextDouble() * 0.5 + 0.15, rng.NextDouble() * 6.28);

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(35), DispatcherPriority.Background, (_, _) =>
        {
            _phase += 0.016;
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

    private static uint Hash(string s)
    {
        uint h = 2166136261;
        foreach (var c in s) h = (h ^ c) * 16777619;
        return h;
    }

    /// <summary>Stable position in the unit disc, purely from a hash — used both for a network's
    /// shared cluster center and, for anything not part of a real network, the server's own address.</summary>
    private static Point DiscPoint(string seed, double minR, double maxR)
    {
        var h = Hash(seed);
        var angle = (h & 0xFFFF) / 65535.0 * Math.Tau;
        var radius = minR + (maxR - minR) * Math.Sqrt(((h >> 16) & 0xFFFF) / 65535.0);
        return new Point(0.5 + Math.Cos(angle) * radius * 0.62, 0.5 + Math.Sin(angle) * radius * 0.62);
    }

    private const string MiscLabel = "Другие сервера";

    /// <summary>Servers sharing a real (2+ member) network label cluster tightly around a point
    /// derived from that label, with a small per-server jitter so they don't fully overlap — the map
    /// reads as a handful of recognizable networks instead of one undifferentiated starfield.</summary>
    private static Point Placement(IGalaxyNode n)
    {
        if (string.IsNullOrEmpty(n.NetworkLabel) || n.NetworkLabel == MiscLabel)
            return DiscPoint(n.Address, 0.10, 1.0);

        var center = DiscPoint(n.NetworkLabel, 0.10, 0.85);
        var jitter = DiscPoint(n.Address, 0, 0.09);
        return new Point(center.X + (jitter.X - 0.5), center.Y + (jitter.Y - 0.5));
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
            var s = ToScreen(Placement(n));
            var d = Math.Sqrt((s.X - screen.X) * (s.X - screen.X) + (s.Y - screen.Y) * (s.Y - screen.Y));
            var r = NodeRadius(n) + 6;
            if (d < r && d < bestD) { bestD = d; best = n; }
        }
        return best;
    }

    private static double NodeRadius(IGalaxyNode n) => 2.2 + Math.Min(7, Math.Sqrt(Math.Max(0, n.Players)) * 0.95);

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        ctx.DrawRectangle(new SolidColorBrush(Bg), null, new Rect(b.Size));

        DrawNebula(ctx, b);
        DrawStarfield(ctx, b);

        var nodes = Nodes();
        if (nodes.Count == 0)
        {
            var t = Fmt("нет серверов", Dim, 13, false);
            ctx.DrawText(t, new Point(b.Width / 2 - t.Width / 2, b.Height / 2));
            return;
        }

        var clusterCentroids = new Dictionary<string, (double SumX, double SumY, int Count)>();
        foreach (var n in nodes)
        {
            var p = ToScreen(Placement(n));
            if (!string.IsNullOrEmpty(n.NetworkLabel) && n.NetworkLabel != MiscLabel)
            {
                clusterCentroids.TryGetValue(n.NetworkLabel, out var acc);
                clusterCentroids[n.NetworkLabel] = (acc.SumX + p.X, acc.SumY + p.Y, acc.Count + 1);
            }

            if (p.X < -24 || p.Y < -24 || p.X > b.Width + 24 || p.Y > b.Height + 24)
                continue;
            DrawNode(ctx, n, p);
        }

        foreach (var (label, acc) in clusterCentroids)
            DrawClusterLabel(ctx, b, label, new Point(acc.SumX / acc.Count, acc.SumY / acc.Count));

        if (_hover is { } hv)
            DrawTooltip(ctx, hv);

        DrawTitle(ctx);
        DrawLegend(ctx, b, nodes.Count);
    }

    private void DrawNebula(DrawingContext ctx, Rect b)
    {
        foreach (var neb in _nebulae)
        {
            var cx = neb.X * b.Width + Math.Sin(_phase * neb.Drift * 0.25 + neb.Phase) * 36 + _pan.X * 0.04;
            var cy = neb.Y * b.Height + Math.Cos(_phase * neb.Drift * 0.2 + neb.Phase) * 26 + _pan.Y * 0.04;
            var pulse = 0.9 + 0.1 * Math.Sin(_phase * 0.4 + neb.Phase);
            var brush = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(new Color(46, neb.C.R, neb.C.G, neb.C.B), 0),
                    new GradientStop(new Color(16, neb.C.R, neb.C.G, neb.C.B), 0.5),
                    new GradientStop(new Color(0, neb.C.R, neb.C.G, neb.C.B), 1),
                },
            };
            var r = neb.R * pulse;
            ctx.DrawEllipse(brush, null, new Point(cx, cy), r, r * 0.7);
        }
    }

    private void DrawStarfield(DrawingContext ctx, Rect b)
    {
        foreach (var s in _stars)
        {
            var par = s.Layer switch { 0 => 0.05, 1 => 0.12, _ => 0.25 };
            var x = ((s.X * b.Width + _pan.X * par) % b.Width + b.Width) % b.Width;
            var y = ((s.Y * b.Height + _pan.Y * par) % b.Height + b.Height) % b.Height;
            var tw = s.Layer == 0 ? 1 : 0.55 + 0.45 * Math.Sin(_phase * 1.1 + s.X * 55 + s.Y * 20);
            ctx.DrawEllipse(new SolidColorBrush(Text, Math.Clamp(s.A * tw, 0, 1)), null, new Point(x, y), s.R, s.R);
        }
    }

    private void DrawNode(DrawingContext ctx, IGalaxyNode n, Point p)
    {
        var r = NodeRadius(n);
        var hovered = ReferenceEquals(n, _hover);
        var color = !n.IsOnline ? Offline : n.IsFavorite ? Warn : n.Players > 0 ? Online : Accent;
        var core = Lerp(color, Colors.White, n.IsOnline ? 0.5 : 0.15);

        // soft glow, brighter for populated / favourite worlds
        if (n.IsOnline)
        {
            var glow = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(new Color(80, color.R, color.G, color.B), 0),
                    new GradientStop(new Color(0, color.R, color.G, color.B), 1),
                },
            };
            var gr = r * (n.Players > 20 ? 3.6 : 2.4) * (hovered ? 1.25 : 1);
            ctx.DrawEllipse(glow, null, p, gr, gr);
        }

        // a small sparkle flare instead of a flat dot — same visual language as СЕКТОР FRONTIER 15
        var rayLen = r * (hovered ? 1.9 : 1.5);
        var rayW = r * 0.16;
        var rayAlpha = n.IsOnline ? 0.8 : 0.32;
        DrawRay(ctx, p, 0, rayLen, rayW, core, rayAlpha);
        DrawRay(ctx, p, Math.PI / 2, rayLen, rayW, core, rayAlpha);
        DrawRay(ctx, p, Math.PI, rayLen, rayW, core, rayAlpha);
        DrawRay(ctx, p, 3 * Math.PI / 2, rayLen, rayW, core, rayAlpha);

        ctx.DrawEllipse(new SolidColorBrush(core), null, p, r * 0.55, r * 0.55);

        if (n.IsFavorite)
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Warn, 0.85), 1.2), p, r + 4, r + 4);
    }

    private static void DrawRay(DrawingContext ctx, Point c, double angle, double length, double width, Color color, double alpha)
    {
        var dir = new Point(Math.Cos(angle), Math.Sin(angle));
        var perp = new Point(-dir.Y, dir.X);
        var tip = new Point(c.X + dir.X * length, c.Y + dir.Y * length);
        var back = new Point(c.X - dir.X * length * 0.18, c.Y - dir.Y * length * 0.18);
        var baseA = new Point(c.X + perp.X * width, c.Y + perp.Y * width);
        var baseB = new Point(c.X - perp.X * width, c.Y - perp.Y * width);

        var g = new StreamGeometry();
        using (var gc = g.Open())
        {
            gc.BeginFigure(tip, true);
            gc.LineTo(baseA);
            gc.LineTo(back);
            gc.LineTo(baseB);
            gc.EndFigure(true);
        }
        ctx.DrawGeometry(new SolidColorBrush(color, alpha), null, g);
    }

    private void DrawClusterLabel(DrawingContext ctx, Rect b, string label, Point centroid)
    {
        var t = Fmt(label, Lerp(Text, Colors.White, 0.2), 12.5, true);
        var x = Math.Clamp(centroid.X - t.Width / 2, 6, Math.Max(6, b.Width - t.Width - 6));
        var y = centroid.Y - 30;
        if (y < 30) y = centroid.Y + 22; // flip below the cluster if there's no room above

        // a soft plate so the name stays legible over the starfield
        var pad = 5.0;
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#0B1322"), 0.55), null,
            new Rect(x - pad, y - 2, t.Width + pad * 2, t.Height + 4), 4, 4);
        ctx.DrawText(t, new Point(x, y));
    }

    private void DrawTitle(DrawingContext ctx)
    {
        var word = Fmt("FRONTIER", Lerp(Text, Colors.White, 0.25), 17, true);
        var num = Fmt("15", FrontierRed, 17, true);
        var sub = Fmt("РУХАБ", Dim, 9.5, true);

        const double x = 16;
        const double y = 14;
        ctx.DrawText(word, new Point(x, y));
        ctx.DrawText(num, new Point(x + word.Width + 7, y));
        ctx.DrawText(sub, new Point(x + 1, y + word.Height + 1));
    }

    private void DrawLegend(DrawingContext ctx, Rect b, int count)
    {
        var items = new (Color, string)[] { (Online, "онлайн"), (Warn, "избранное"), (Offline, "офлайн") };
        var x = 16.0;
        var y = b.Height - 24;
        foreach (var (col, lbl) in items)
        {
            ctx.DrawEllipse(new SolidColorBrush(col), null, new Point(x + 4, y + 5), 3.5, 3.5);
            var t = Fmt(lbl, Dim, 9.5, false);
            ctx.DrawText(t, new Point(x + 13, y));
            x += 13 + t.Width + 16;
        }
        var hint = Fmt($"{count} серверов · тащить — двигать · колесо — масштаб", Color.Parse("#5A6B8A"), 9, false);
        ctx.DrawText(hint, new Point(b.Width - hint.Width - 14, b.Height - hint.Height - 12));
    }

    private void DrawTooltip(DrawingContext ctx, IGalaxyNode n)
    {
        const double w = 240;
        var title = Fmt(n.Name, Text, 12.5, true);
        title.MaxTextWidth = w - 22;
        var sub = Fmt(n.IsOnline ? $"◆ {n.Players} игроков · {n.Address}" : $"офлайн · {n.Address}",
            n.IsOnline ? Online : Dim, 10, false);
        sub.MaxTextWidth = w - 22;

        var h = 14 + title.Height + 5 + sub.Height + 12;
        var x = Math.Clamp(_hoverAt.X + 16, 6, Math.Max(6, Bounds.Width - w - 6));
        var y = Math.Clamp(_hoverAt.Y + 16, 6, Math.Max(6, Bounds.Height - h - 6));
        var rect = new Rect(x, y, w, h);

        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#0B1322"), 0.98), new Pen(new SolidColorBrush(Accent, 0.7), 1), rect, 7, 7);
        ctx.DrawRectangle(new SolidColorBrush(FrontierRed, 0.9), null, new Rect(x, y, 3, h), 2, 2);
        ctx.DrawText(title, new Point(x + 12, y + 9));
        ctx.DrawText(sub, new Point(x + 12, y + 9 + title.Height + 5));
    }

    private static Color Lerp(Color a, Color b, double t) => new(
        (byte)(a.A + (b.A - a.A) * t),
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    private FormattedText Fmt(string text, Color color, double size, bool bold) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? _faceBold : _face, size, new SolidColorBrush(color));
}
