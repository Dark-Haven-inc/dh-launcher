using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using DarkHaven.Launcher;
using DarkHaven.Launcher.Api;

namespace DarkHaven.App.Controls;

/// <summary>
/// Players online over time — the most online in each stretch, one series, in the launcher's accent. Drawn to the dataviz specs:
/// 2px line with a ~12% wash under it, hairline solid gridlines on round ticks, only the peak
/// labelled, gaps where the server was down (never a fake zero), and a crosshair that snaps to the
/// nearest point with a tooltip — values first, then time. Arrow keys move it too.
/// </summary>
public sealed class OnlineChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<OnlinePoint>?> PointsProperty =
        AvaloniaProperty.Register<OnlineChart, IReadOnlyList<OnlinePoint>?>(nameof(Points));

    /// <summary>The server's player cap — the y-axis reaches at least this, and it's drawn as a reference line.</summary>
    public static readonly StyledProperty<int> CapacityProperty =
        AvaloniaProperty.Register<OnlineChart, int>(nameof(Capacity));

    /// <summary>"24h", "7d" or "30d" — decides how the time axis is labelled.</summary>
    public static readonly StyledProperty<string> RangeProperty =
        AvaloniaProperty.Register<OnlineChart, string>(nameof(Range), "24h");

    public static readonly StyledProperty<string?> EmptyTextProperty =
        AvaloniaProperty.Register<OnlineChart, string?>(nameof(EmptyText));

    public IReadOnlyList<OnlinePoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public int Capacity { get => GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
    public string Range { get => GetValue(RangeProperty); set => SetValue(RangeProperty, value); }
    public string? EmptyText { get => GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    private const double PadLeft = 34, PadRight = 16, PadTop = 28, PadBottom = 26;
    private int? _hover;

    static OnlineChart()
    {
        AffectsRender<OnlineChart>(PointsProperty, CapacityProperty, RangeProperty, EmptyTextProperty);
        FocusableProperty.OverrideDefaultValue<OnlineChart>(true);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PointsProperty)
            _hover = null;
    }

    private Rect Plot => new(PadLeft, PadTop,
        Math.Max(0, Bounds.Width - PadLeft - PadRight), Math.Max(0, Bounds.Height - PadTop - PadBottom));

    private Color Res(string key, string fallback) =>
        this.TryFindResource(key, out var v) && v is Color c ? c : Color.Parse(fallback);

    public override void Render(DrawingContext ctx)
    {
        var plot = Plot;
        if (plot.Width < 20 || plot.Height < 20)
            return;

        var accent = Res("DhAccent", "#3B82F6");
        var text = new SolidColorBrush(Res("DhText", "#DCE6F5"));
        var dim = new SolidColorBrush(Res("DhTextDim", "#7C8DB0"));
        var surface = Res("DhBgElevated", "#111A2E");
        var grid = new Pen(new SolidColorBrush(Res("DhBorder", "#24365A")), 1);
        var gridStrong = new Pen(new SolidColorBrush(Res("DhBorderBright", "#3C5C96")), 1);
        var typeface = new Typeface(GetValue(TextElement.FontFamilyProperty));
        FormattedText Label(string s, IBrush brush, double size = 10, FontWeight weight = FontWeight.Normal) =>
            new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(typeface.FontFamily, FontStyle.Normal, weight), size, brush);

        var points = Points ?? [];
        var n = points.Count;
        var hasData = n > 1 && points.Any(p => p.Peak is not null);
        var maxPeak = hasData ? points.Max(p => p.Peak ?? 0) : 0;
        var (step, top) = ChartScale.For(Math.Max(maxPeak, Capacity));

        double X(int i) => plot.Left + plot.Width * i / Math.Max(1, n - 1);
        double Y(double v) => plot.Bottom - plot.Height * Math.Min(v, top) / top;

        // Y gridlines on round ticks, labels in muted text — the grid recedes, the data doesn't.
        foreach (var tick in ChartScale.Ticks(step, top))
        {
            var y = Math.Round(Y(tick)) + 0.5;
            ctx.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Label(tick.ToString(CultureInfo.InvariantCulture), dim);
            ctx.DrawText(label, new Point(plot.Left - 6 - label.Width, y - label.Height / 2));
        }

        // The server's cap: how full it got, at a glance.
        if (Capacity > 0 && Capacity <= top && Capacity % step != 0)
        {
            var y = Math.Round(Y(Capacity)) + 0.5;
            ctx.DrawLine(gridStrong, new Point(plot.Left, y), new Point(plot.Right, y));
        }
        if (Capacity > 0)
        {
            var label = Label($"мест: {Capacity}", dim);
            ctx.DrawText(label, new Point(plot.Right - label.Width, Y(Capacity) - label.Height - 2));
        }

        if (!hasData)
        {
            var empty = Label(EmptyText ?? "Данных пока нет", dim, 12);
            ctx.DrawText(empty, new Point(plot.Center.X - empty.Width / 2, plot.Center.Y - empty.Height / 2));
            return;
        }

        DrawTimeAxis(ctx, plot, points, Label, dim);

        // The series: one stroke and one wash per run of consecutive values; a down stretch breaks it.
        var wash = new SolidColorBrush(accent, 0.12);
        var line = new Pen(new SolidColorBrush(accent), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var accentBrush = new SolidColorBrush(accent);
        for (var i = 0; i < n;)
        {
            if (points[i].Peak is null) { i++; continue; }
            var start = i;
            while (i < n && points[i].Peak is not null) i++;
            var end = i - 1;

            if (end == start)
            {
                ctx.DrawEllipse(accentBrush, null, new Point(X(start), Y(points[start].Peak!.Value)), 2.5, 2.5);
                continue;
            }

            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(X(start), plot.Bottom), true);
                for (var k = start; k <= end; k++) g.LineTo(new Point(X(k), Y(points[k].Peak!.Value)));
                g.LineTo(new Point(X(end), plot.Bottom));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(wash, null, area);

            var stroke = new StreamGeometry();
            using (var g = stroke.Open())
            {
                g.BeginFigure(new Point(X(start), Y(points[start].Peak!.Value)), false);
                for (var k = start + 1; k <= end; k++) g.LineTo(new Point(X(k), Y(points[k].Peak!.Value)));
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, line, stroke);
        }

        // The one direct label: the highest point.
        var peakIndex = Enumerable.Range(0, n).Where(i => points[i].Peak is not null).MaxBy(i => points[i].Peak!.Value);
        var peakPoint = new Point(X(peakIndex), Y(points[peakIndex].Peak!.Value));
        DrawMarker(ctx, peakPoint, accentBrush, surface);
        var peakLabel = Label($"пик {Format(points[peakIndex].Peak!.Value)}", text, 11, FontWeight.SemiBold);
        var peakX = Math.Clamp(peakPoint.X - peakLabel.Width / 2, plot.Left, plot.Right - peakLabel.Width);
        ctx.DrawText(peakLabel, new Point(peakX, Math.Max(2, peakPoint.Y - peakLabel.Height - 8)));

        if (_hover is { } h && h >= 0 && h < n)
            DrawHover(ctx, plot, points[h], X(h), Y, Label, text, dim, gridStrong, accentBrush, surface);
    }

    private static void DrawMarker(DrawingContext ctx, Point at, IBrush fill, Color surface) =>
        // 8px dot with a 2px ring in the surface colour, so it stays legible on top of the line.
        ctx.DrawEllipse(fill, new Pen(new SolidColorBrush(surface), 2), at, 4, 4);

    private void DrawTimeAxis(DrawingContext ctx, Rect plot, IReadOnlyList<OnlinePoint> points,
        Func<string, IBrush, double, FontWeight, FormattedText> label, IBrush dim)
    {
        var t0 = points[0].At.ToLocalTime();
        var tn = points[^1].At.ToLocalTime();
        var span = (tn - t0).TotalSeconds;
        if (span <= 0) return;

        // Local-time ticks: every 3 hours for a day, every midnight for a week, every 5th day for a month.
        var ticks = new List<(DateTimeOffset At, string Text)>();
        if (Range == "24h")
        {
            var t = new DateTimeOffset(t0.Year, t0.Month, t0.Day, t0.Hour, 0, 0, t0.Offset).AddHours(1);
            for (; t <= tn; t = t.AddHours(1))
                if (t.Hour % 3 == 0) ticks.Add((t, t.ToString("HH:mm", CultureInfo.InvariantCulture)));
        }
        else
        {
            var every = Range == "7d" ? 1 : 5;
            var t = new DateTimeOffset(t0.Year, t0.Month, t0.Day, 0, 0, 0, t0.Offset).AddDays(1);
            for (var i = 0; t <= tn; t = t.AddDays(1), i++)
                if (i % every == 0) ticks.Add((t, RuText.DayMonth(t)));
        }

        foreach (var (at, text) in ticks)
        {
            var x = plot.Left + plot.Width * (at - t0).TotalSeconds / span;
            var formatted = label(text, dim, 10, FontWeight.Normal);
            var left = x - formatted.Width / 2;
            if (left < plot.Left - 4 || left + formatted.Width > plot.Right + 4)
                continue; // a clipped label is worse than none
            ctx.DrawText(formatted, new Point(left, plot.Bottom + 7));
        }
    }

    private void DrawHover(DrawingContext ctx, Rect plot, OnlinePoint p, double x, Func<double, double> y,
        Func<string, IBrush, double, FontWeight, FormattedText> label, IBrush text, IBrush dim,
        Pen crosshair, IBrush accent, Color surface)
    {
        var cx = Math.Round(x) + 0.5;
        ctx.DrawLine(crosshair, new Point(cx, plot.Top), new Point(cx, plot.Bottom));
        if (p.Peak is { } value)
            DrawMarker(ctx, new Point(x, y(value)), accent, surface);

        // Values lead; the time follows.
        var lines = new List<FormattedText>();
        if (p.Peak is { } peak)
        {
            lines.Add(label($"{peak} онлайн", text, 13, FontWeight.SemiBold));
            if (p.Average is { } a && Math.Round(a) < peak)
                lines.Add(label($"в среднем {Format(a)}", dim, 11, FontWeight.Normal));
        }
        else
        {
            lines.Add(label(p.Uptime is null ? "нет данных" : "сервер не отвечал", text, 12, FontWeight.SemiBold));
        }
        if (p.Uptime is { } up and > 0 and < 1)
            lines.Add(label($"доступен {up * 100:0}% времени", dim, 11, FontWeight.Normal));
        lines.Add(label(RuText.DayTime(p.At.ToLocalTime()), dim, 11, FontWeight.Normal));

        const double pad = 8, gap = 2;
        var width = lines.Max(l => l.Width) + pad * 2;
        var height = lines.Sum(l => l.Height) + gap * (lines.Count - 1) + pad * 2;
        var left = x + 12 + width > plot.Right ? x - 12 - width : x + 12;
        var box = new Rect(Math.Max(0, left), plot.Top + 4, width, height);

        var fill = new SolidColorBrush(Res("DhBgHover", "#1B2942"));
        ctx.DrawRectangle(fill, new Pen(new SolidColorBrush(Res("DhBorderBright", "#3C5C96")), 1), box, 4, 4);
        var ty = box.Top + pad;
        foreach (var l in lines)
        {
            ctx.DrawText(l, new Point(box.Left + pad, ty));
            ty += l.Height + gap;
        }
    }

    private static string Format(double v) =>
        Math.Abs(v - Math.Round(v)) < 0.05 ? Math.Round(v).ToString(CultureInfo.InvariantCulture) : v.ToString("0.0", CultureInfo.InvariantCulture);

    // --- pointer & keyboard: the crosshair finds the X ---

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var n = Points?.Count ?? 0;
        if (n < 2) return;
        var plot = Plot;
        var px = e.GetPosition(this).X;
        var i = (int)Math.Round((px - plot.Left) / Math.Max(1, plot.Width) * (n - 1));
        SetHover(Math.Clamp(i, 0, n - 1));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var n = Points?.Count ?? 0;
        if (n < 2) { base.OnKeyDown(e); return; }
        var delta = e.Key switch { Key.Left => -1, Key.Right => 1, _ => 0 };
        if (delta == 0) { base.OnKeyDown(e); return; }
        SetHover(Math.Clamp((_hover ?? n - 1) + delta, 0, n - 1));
        e.Handled = true;
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        SetHover(null);
    }

    private void SetHover(int? i)
    {
        if (_hover == i) return;
        _hover = i;
        InvalidateVisual();
    }
}
