using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DarkHaven.App.Controls;

/// <summary>How a <see cref="FlatMap"/> is drawn.</summary>
public enum MapLook
{
    /// <summary>Monochrome: a grid, dashed links, squares.</summary>
    Flat,

    /// <summary>The medieval theme: an old chart in ink — rhumb lines from the middle, roads that curve and
    /// are dotted, a tower for each region (a castle with a red pennant for the central one), labels in small
    /// caps with the sheet showing round them.</summary>
    Ink,

    /// <summary>The cyberpunk theme: a net in neon — glowing links, diamonds that glow while their region
    /// is up, a pink hexagon in brackets round the chosen one.</summary>
    Neon,
}

public sealed partial class FlatMap
{
    private Color ColorOf(string key, string fallback) =>
        this.TryFindResource(key, out var v) && v is Color c ? c : Color.Parse(fallback);

    private FontFamily FontOr(string key, string fallbackKey) =>
        this.TryFindResource(key, out var v) && v is FontFamily f ? f : Font(fallbackKey, "monospace");

    private static Color Alpha(Color c, double a) => Color.FromArgb((byte)(a * 255), c.R, c.G, c.B);

    /// <summary>The links, each pair once.</summary>
    private IEnumerable<(IMapNode A, IMapNode B)> Links()
    {
        var byName = _nodes.GroupBy(n => n.Name).ToDictionary(g => g.Key, g => g.First());
        var drawn = new HashSet<(string, string)>();
        foreach (var n in _nodes)
        {
            foreach (var other in n.Neighbours)
            {
                if (!byName.TryGetValue(other, out var m))
                    continue;
                var key = string.CompareOrdinal(n.Name, m.Name) < 0 ? (n.Name, m.Name) : (m.Name, n.Name);
                if (drawn.Add(key))
                    yield return (n, m);
            }
        }
    }

    private IEnumerable<IMapNode> NodesSelectedLast() => _nodes.OrderBy(n => ReferenceEquals(n, SelectedItem));

    /// <summary>Text with a halo of <paramref name="halo"/> round it, so lines under it don't cut through.</summary>
    private static void DrawHaloText(DrawingContext ctx, FormattedText text, FormattedText halo, Point at)
    {
        foreach (var (dx, dy) in new[] { (-1.2, 0.0), (1.2, 0.0), (0.0, -1.2), (0.0, 1.2), (-0.9, -0.9), (0.9, 0.9), (-0.9, 0.9), (0.9, -0.9) })
            ctx.DrawText(halo, at + new Vector(dx, dy));
        ctx.DrawText(text, at);
    }

    private static string Spaced(string s) => string.Join(" ", s.ToUpperInvariant().ToCharArray());

    // ------------------------------------------------------------------ ink

    private void RenderInk(DrawingContext ctx)
    {
        var b = Bounds;
        var ink = ColorOf("DhText", "#2A1B0E");
        var dim = ColorOf("DhTextDim", "#5A4129");
        var red = ColorOf("DhAccent", "#8C2A1C");
        var sheet = ColorOf("DhBg", "#E9DBB8");
        var caps = FontOr("DhSerifCaps", "DhMono");
        var serif = FontOr("DhSerif", "DhMono");

        // The graticule, faint and dashed.
        var grid = new Pen(new SolidColorBrush(ColorOf("DhMapGrid", "#D9C69C")), 1) { DashStyle = new DashStyle([6, 6], 0) };
        for (var x = 36.5; x < b.Width; x += 96)
            ctx.DrawLine(grid, new Point(x, 0), new Point(x, b.Height));
        for (var y = 24.5; y < b.Height; y += 96)
            ctx.DrawLine(grid, new Point(0, y), new Point(b.Width, y));

        // Rhumb lines, as a portolan chart draws them from its wind rose: sixteen, the cardinal ones stronger.
        var hub = _nodes.FirstOrDefault(n => n.IsCentral) is { } central ? Place(central) : new Point(b.Width / 2, b.Height / 2);
        var reach = Math.Sqrt(b.Width * b.Width + b.Height * b.Height);
        for (var i = 0; i < 16; i++)
        {
            var a = i * Math.PI / 8;
            var pen = new Pen(new SolidColorBrush(Alpha(i % 4 == 0 ? red : dim, i % 4 == 0 ? 0.22 : 0.12)), i % 4 == 0 ? 1 : 0.7);
            ctx.DrawLine(pen, hub, hub + new Vector(Math.Cos(a), Math.Sin(a)) * reach);
        }

        if (_nodes.Count == 0)
            return;

        // Roads: dotted, bowed a little to one side as a hand would draw them.
        var road = new Pen(new SolidColorBrush(Alpha(dim, 0.9)), 2) { DashStyle = new DashStyle([0.1, 3.2], 0), LineCap = PenLineCap.Round };
        foreach (var (n, m) in Links())
        {
            var (p, q) = (Place(n), Place(m));
            var d = q - p;
            var length = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            if (length < 1)
                continue;
            var normal = new Vector(-d.Y / length, d.X / length);
            var bow = ((n.Name.Length * 7 + m.Name.Length * 3) % 2 == 0 ? 1 : -1) * Math.Min(28, length * 0.12);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(p, false);
                g.QuadraticBezierTo(p + d / 2 + normal * bow, q);
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, road, geometry);
        }

        // Network names, spaced small caps in red over their cluster.
        foreach (var cluster in _nodes.Where(n => !string.IsNullOrEmpty(n.RegionLabel)).GroupBy(n => n.RegionLabel!))
        {
            var pts = cluster.Select(Place).ToList();
            var label = Text(Spaced(cluster.Key), caps, 11, FontWeight.Normal, new SolidColorBrush(red));
            var halo = Text(Spaced(cluster.Key), caps, 11, FontWeight.Normal, new SolidColorBrush(sheet));
            DrawHaloText(ctx, label, halo, new Point(pts.Average(p => p.X) - label.Width / 2, pts.Min(p => p.Y) - 36));
        }

        foreach (var n in NodesSelectedLast())
        {
            var p = Place(n);
            var selected = ReferenceEquals(n, SelectedItem);
            var hover = ReferenceEquals(n, _hover);
            var color = n.IsOnline || selected || hover ? ink : dim;

            if (selected || hover)
            {
                var ring = new Pen(new SolidColorBrush(selected ? red : Alpha(dim, 0.6)), selected ? 1.4 : 1);
                ctx.DrawEllipse(null, ring, p, 17, 17);
                if (selected)
                    ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Alpha(red, 0.5)), 0.7), p, 20.5, 20.5);
            }

            DrawTower(ctx, n, p, n.IsCentral ? 1.35 : 1, color, red, sheet);

            var x = p.X + (n.IsCentral ? 16 : 13) + (selected ? 8 : 0);
            var nameBrush = new SolidColorBrush(selected ? red : color);
            var name = Text(n.Name, caps, selected ? 15 : 13, selected ? FontWeight.Bold : FontWeight.Normal, nameBrush);
            var nameHalo = Text(n.Name, caps, selected ? 15 : 13, selected ? FontWeight.Bold : FontWeight.Normal, new SolidColorBrush(sheet));
            var top = p.Y - name.Height / 2 - (n.IsOnline ? 6 : 0);
            DrawHaloText(ctx, name, nameHalo, new Point(x, top));
            if (n.IsOnline && !string.IsNullOrEmpty(n.Population))
            {
                var pop = Text(n.Population, serif, 11, FontWeight.Normal, new SolidColorBrush(dim));
                var popHalo = Text(n.Population, serif, 11, FontWeight.Normal, new SolidColorBrush(sheet));
                DrawHaloText(ctx, pop, popHalo, new Point(x, top + name.Height - 3));
            }
        }
    }

    /// <summary>A tower in ink: solid while its region is up, outlined (and cracked) when down, dashed in
    /// quarantine, half inked when full. The central region is a castle with a red pennant.</summary>
    private void DrawTower(DrawingContext ctx, IMapNode n, Point p, double scale, Color ink, Color red, Color sheet)
    {
        double S(double v) => v * scale;
        var body = new Rect(p.X - S(5), p.Y - S(5), S(10), S(12));

        var outline = new StreamGeometry();
        using (var g = outline.Open())
        {
            // Walls with three merlons on top.
            g.BeginFigure(new Point(body.Left, body.Bottom), true);
            g.LineTo(new Point(body.Left, body.Top - S(3)));
            g.LineTo(new Point(body.Left + S(2.5), body.Top - S(3)));
            g.LineTo(new Point(body.Left + S(2.5), body.Top - S(1)));
            g.LineTo(new Point(body.Left + S(3.75), body.Top - S(1)));
            g.LineTo(new Point(body.Left + S(3.75), body.Top - S(3)));
            g.LineTo(new Point(body.Right - S(3.75), body.Top - S(3)));
            g.LineTo(new Point(body.Right - S(3.75), body.Top - S(1)));
            g.LineTo(new Point(body.Right - S(2.5), body.Top - S(1)));
            g.LineTo(new Point(body.Right - S(2.5), body.Top - S(3)));
            g.LineTo(new Point(body.Right, body.Top - S(3)));
            g.LineTo(new Point(body.Right, body.Bottom));
            g.EndFigure(true);
        }

        var brush = new SolidColorBrush(ink);
        var sheetBrush = new SolidColorBrush(sheet);
        var pen = new Pen(brush, 1.1) { LineJoin = PenLineJoin.Miter };
        if (n.IsQuarantine || (!n.IsOnline && !n.IsOffline))
        {
            ctx.DrawGeometry(sheetBrush, new Pen(brush, 1.1) { DashStyle = new DashStyle([1.5, 1.5], 0) }, outline);
        }
        else if (n.IsOffline)
        {
            ctx.DrawGeometry(sheetBrush, pen, outline);
            ctx.DrawLine(pen, new Point(body.Left + S(2), body.Top), new Point(body.Right - S(3), body.Bottom - S(2)));
        }
        else if (IsFull(n))
        {
            ctx.DrawGeometry(sheetBrush, pen, outline);
            using (ctx.PushClip(new Rect(body.Left - S(1), body.Top - S(4), S(6), S(17))))
                ctx.DrawGeometry(brush, null, outline);
        }
        else
        {
            ctx.DrawGeometry(brush, pen, outline);
            // A door, cut out of the solid wall.
            ctx.DrawGeometry(sheetBrush, null, Door(p, scale));
        }

        if (n.IsCentral)
        {
            // A flagstaff and a pennant flying from it.
            var top = new Point(p.X, body.Top - S(3));
            ctx.DrawLine(new Pen(brush, 1), top, top - new Vector(0, S(8)));
            var flag = new StreamGeometry();
            using (var g = flag.Open())
            {
                g.BeginFigure(top - new Vector(0, S(8)), true);
                g.LineTo(top - new Vector(-S(7), S(6.5)));
                g.LineTo(top - new Vector(0, S(5)));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(new SolidColorBrush(red), null, flag);
        }
    }

    private static StreamGeometry Door(Point p, double scale)
    {
        var door = new StreamGeometry();
        using var g = door.Open();
        var (w, bottom) = (2.0 * scale, p.Y + 7 * scale);
        g.BeginFigure(new Point(p.X - w, bottom), true);
        g.LineTo(new Point(p.X - w, bottom - 3.5 * scale));
        g.ArcTo(new Point(p.X + w, bottom - 3.5 * scale), new Size(w, w), 0, false, SweepDirection.Clockwise);
        g.LineTo(new Point(p.X + w, bottom));
        g.EndFigure(true);
        return door;
    }

    // ------------------------------------------------------------------ neon

    private void RenderNeon(DrawingContext ctx)
    {
        var b = Bounds;
        var cyan = ColorOf("DhCyan", "#05D9E8");
        var pink = ColorOf("DhActive", "#FF2A6D");
        var warn = ColorOf("DhWarn", "#FCEE0A");
        var danger = ColorOf("DhDanger", "#FF4545");
        var text = ColorOf("DhText", "#EAF6FF");
        var dim = ColorOf("DhTextDim", "#93A1BD");
        var bg = ColorOf("DhBg", "#0A0A12");
        var tech = FontOr("DhTech", "DhMono");
        var mono = Font("DhMono", "monospace");

        // The grid, and a brighter dot where every other pair of lines crosses.
        var gridColor = ColorOf("DhMapGrid", "#14142A");
        var grid = new Pen(new SolidColorBrush(gridColor), 1);
        for (var x = 20.5; x < b.Width; x += 40)
            ctx.DrawLine(grid, new Point(x, 0), new Point(x, b.Height));
        for (var y = 20.5; y < b.Height; y += 40)
            ctx.DrawLine(grid, new Point(0, y), new Point(b.Width, y));
        var dot = new SolidColorBrush(Alpha(cyan, 0.28));
        for (var x = 20; x < b.Width; x += 80)
            for (var y = 20; y < b.Height; y += 80)
                ctx.FillRectangle(dot, new Rect(x - 0.5, y - 0.5, 2, 2));

        if (_nodes.Count == 0)
            return;

        // Links glow: a wide faint stroke, a narrower one, then the thin bright core.
        var glowWide = new Pen(new SolidColorBrush(Alpha(cyan, 0.08)), 7) { LineCap = PenLineCap.Round };
        var glow = new Pen(new SolidColorBrush(Alpha(cyan, 0.22)), 3) { LineCap = PenLineCap.Round };
        var core = new Pen(new SolidColorBrush(Alpha(cyan, 0.85)), 1);
        foreach (var (n, m) in Links())
        {
            var (p, q) = (Place(n), Place(m));
            var live = n.IsOnline && m.IsOnline;
            if (live)
            {
                ctx.DrawLine(glowWide, p, q);
                ctx.DrawLine(glow, p, q);
                ctx.DrawLine(core, p, q);
            }
            else
            {
                ctx.DrawLine(new Pen(new SolidColorBrush(Alpha(dim, 0.45)), 1) { DashStyle = new DashStyle([3, 4], 0) }, p, q);
            }
        }

        // Network names, spaced, in pink.
        foreach (var cluster in _nodes.Where(n => !string.IsNullOrEmpty(n.RegionLabel)).GroupBy(n => n.RegionLabel!))
        {
            var pts = cluster.Select(Place).ToList();
            var label = Text("// " + Spaced(cluster.Key), tech, 10, FontWeight.Normal, new SolidColorBrush(pink));
            ctx.DrawText(label, new Point(pts.Average(p => p.X) - label.Width / 2, pts.Min(p => p.Y) - 34));
        }

        foreach (var n in NodesSelectedLast())
        {
            var p = Place(n);
            var selected = ReferenceEquals(n, SelectedItem);
            var hover = ReferenceEquals(n, _hover);
            var r = n.IsCentral ? 7.5 : 6;

            if (n.IsOnline)
            {
                var halo = new RadialGradientBrush
                {
                    GradientStops = { new GradientStop(Alpha(cyan, 0.45), 0), new GradientStop(Alpha(cyan, 0), 1) },
                };
                ctx.DrawEllipse(halo, null, p, r * 3, r * 3);
            }

            var diamond = Diamond(p, r);
            var cyanBrush = new SolidColorBrush(cyan);
            if (n.IsQuarantine || (!n.IsOnline && !n.IsOffline))
            {
                ctx.DrawGeometry(new SolidColorBrush(bg), new Pen(new SolidColorBrush(warn), 1.2) { DashStyle = new DashStyle([2, 2], 0) }, diamond);
            }
            else if (n.IsOffline)
            {
                var x = new Pen(new SolidColorBrush(danger), 1.6) { LineCap = PenLineCap.Round };
                ctx.DrawLine(x, p + new Vector(-r * 0.7, -r * 0.7), p + new Vector(r * 0.7, r * 0.7));
                ctx.DrawLine(x, p + new Vector(r * 0.7, -r * 0.7), p + new Vector(-r * 0.7, r * 0.7));
            }
            else if (IsFull(n))
            {
                ctx.DrawGeometry(new SolidColorBrush(bg), new Pen(cyanBrush, 1.2), diamond);
                using (ctx.PushClip(new Rect(p.X - r, p.Y - r, r, r * 2)))
                    ctx.DrawGeometry(cyanBrush, null, diamond);
            }
            else
            {
                ctx.DrawGeometry(cyanBrush, null, diamond);
            }
            if (n.IsCentral)
                ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Alpha(cyan, 0.7)), 1), Diamond(p, r + 4));

            if (selected || hover)
                DrawTarget(ctx, p, selected ? pink : Alpha(cyan, 0.6), selected);

            var x0 = p.X + r + (selected ? 16 : 9);
            var nameColor = selected ? pink : n.IsOnline || hover ? text : dim;
            var name = Text(n.Name.ToUpperInvariant(), tech, selected ? 13.5 : 11.5, selected ? FontWeight.Bold : FontWeight.Medium, new SolidColorBrush(nameColor));
            var top = p.Y - name.Height / 2 - (n.IsOnline ? 6 : 0);
            ctx.DrawText(name, new Point(x0, top));
            if (n.IsOnline && !string.IsNullOrEmpty(n.Population))
                ctx.DrawText(Text(n.Population, mono, 10, FontWeight.Normal, new SolidColorBrush(cyan)), new Point(x0, top + name.Height - 2));
        }
    }

    private static StreamGeometry Diamond(Point p, double r)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(p + new Vector(0, -r), true);
        c.LineTo(p + new Vector(r, 0));
        c.LineTo(p + new Vector(0, r));
        c.LineTo(p + new Vector(-r, 0));
        c.EndFigure(true);
        return g;
    }

    /// <summary>A hexagon round the node and targeting brackets at the corners of a box round it.</summary>
    private static void DrawTarget(DrawingContext ctx, Point p, Color color, bool strong)
    {
        var pen = new Pen(new SolidColorBrush(color), strong ? 1.4 : 1);
        var hex = new StreamGeometry();
        using (var g = hex.Open())
        {
            for (var i = 0; i < 6; i++)
            {
                var a = Math.PI / 6 + i * Math.PI / 3;
                var at = p + new Vector(Math.Cos(a), Math.Sin(a)) * 15;
                if (i == 0) g.BeginFigure(at, false); else g.LineTo(at);
            }
            g.EndFigure(true);
        }
        ctx.DrawGeometry(null, pen, hex);
        if (!strong)
            return;

        const double box = 22, arm = 6;
        foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
        {
            var corner = p + new Vector(sx * box, sy * box);
            ctx.DrawLine(pen, corner, corner - new Vector(sx * arm, 0));
            ctx.DrawLine(pen, corner, corner - new Vector(0, sy * arm));
        }
    }
}
