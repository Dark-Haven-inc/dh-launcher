using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace DarkHaven.App.Controls;

/// <summary>
/// A flat, monochrome map of <see cref="IMapNode"/>s: a faint grid, dashed links between neighbours,
/// and a square per node whose shape carries its state (same marks as <see cref="StatusGlyph"/>).
/// The selected node gets a ring and a larger label. Drag to pan, wheel to zoom, click to select.
/// </summary>
public sealed class FlatMap : Control
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<FlatMap, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<FlatMap, object?>(nameof(SelectedItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

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

    private const double PadX = 64, PadY = 48, Square = 8, Ring = 20, HitRadius = 14;

    private readonly List<IMapNode> _nodes = [];
    private INotifyCollectionChanged? _observed;
    private IMapNode? _hover;
    private Vector _pan;
    private double _zoom = 1;
    private Point? _dragFrom;
    private bool _dragged;

    public FlatMap()
    {
        ClipToBounds = true;
        // Colors come from the palette at render time; repaint when the player recolors it.
        ResourcesChanged += (_, _) => InvalidateVisual();
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty)
            Rebind();
        else if (change.Property == SelectedItemProperty)
            InvalidateVisual();
    }

    private void Rebind()
    {
        if (_observed is not null)
            _observed.CollectionChanged -= OnCollectionChanged;
        foreach (var n in _nodes.OfType<INotifyPropertyChanged>())
            n.PropertyChanged -= OnNodeChanged;

        _nodes.Clear();
        if (ItemsSource is { } src)
            _nodes.AddRange(src.OfType<IMapNode>());
        foreach (var n in _nodes.OfType<INotifyPropertyChanged>())
            n.PropertyChanged += OnNodeChanged;

        _observed = ItemsSource as INotifyCollectionChanged;
        if (_observed is not null)
            _observed.CollectionChanged += OnCollectionChanged;

        _pan = default;
        _zoom = 1;
        InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebind();
    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    // --- geometry ---

    private Point Place(IMapNode n)
    {
        var w = Math.Max(1, Bounds.Width - 2 * PadX);
        var h = Math.Max(1, Bounds.Height - 2 * PadY);
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var p = new Point(PadX + n.X * w, PadY + n.Y * h);
        return c + (p - c) * _zoom + _pan;
    }

    private IMapNode? HitTest(Point at)
    {
        IMapNode? best = null;
        var bestDist = double.MaxValue;
        foreach (var n in _nodes)
        {
            var p = Place(n);
            // The square, or the label to its right.
            var inLabel = at.X >= p.X && at.X <= p.X + 16 + n.Name.Length * 8 && Math.Abs(at.Y - p.Y) <= 10;
            var d = Math.Sqrt(Math.Pow(at.X - p.X, 2) + Math.Pow(at.Y - p.Y, 2));
            if ((d <= HitRadius || inLabel) && d < bestDist)
            {
                best = n;
                bestDist = d;
            }
        }
        return best;
    }

    // --- input ---

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _dragFrom = e.GetPosition(this);
        _dragged = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        if (_dragFrom is { } from)
        {
            var delta = at - from;
            if (_dragged || Math.Abs(delta.X) + Math.Abs(delta.Y) > 3)
            {
                _dragged = true;
                _pan += delta;
                _dragFrom = at;
                InvalidateVisual();
            }
            return;
        }

        var hover = HitTest(at);
        if (!ReferenceEquals(hover, _hover))
        {
            _hover = hover;
            Cursor = new Cursor(hover is null ? StandardCursorType.Arrow : StandardCursorType.Hand);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var wasDrag = _dragged;
        _dragFrom = null;
        _dragged = false;
        e.Pointer.Capture(null);
        if (wasDrag)
            return;

        if (HitTest(e.GetPosition(this)) is { } node)
        {
            SelectedItem = node;
            NodeInvoked?.Invoke(this, node);
        }
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

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var at = e.GetPosition(this);
        var old = _zoom;
        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), 0.6, 4);
        // Keep the point under the cursor where it is.
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var world = (at - c - _pan) / old;
        _pan = at - c - world * _zoom;
        InvalidateVisual();
        e.Handled = true;
    }

    // --- drawing ---

    private FontFamily Font(string key, string fallback) =>
        this.TryFindResource(key, out var v) && v is FontFamily f ? f : new FontFamily(fallback);

    private IBrush Brush(string key, string fallback) =>
        new SolidColorBrush(this.TryFindResource(key, out var v) && v is Color c ? c : Color.Parse(fallback));

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        ctx.FillRectangle(Brushes.Transparent, new Rect(b.Size));

        var gridPen = new Pen(Brush("DhMapGrid", "#141416"), 1);
        for (var x = 16.5; x < b.Width; x += 48)
            ctx.DrawLine(gridPen, new Point(x, 0), new Point(x, b.Height));
        for (var y = 8.5; y < b.Height; y += 48)
            ctx.DrawLine(gridPen, new Point(0, y), new Point(b.Width, y));

        if (_nodes.Count == 0)
            return;

        var text = Brush("DhText", "#EDEDED");
        var dim = Brush("DhTextDim", "#8A8A8F");
        var faint = Brush("DhTextFaint", "#7A7A80");
        var bg = Brush("DhBg", "#0B0B0C");
        var sans = Font("DhSans", "Inter");
        var mono = Font("DhMono", "monospace");

        // Links, each pair once.
        var linkPen = new Pen(Brush("DhMapLink", "#3A3A3E"), 1) { DashStyle = new DashStyle([4, 4], 0) };
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
                    ctx.DrawLine(linkPen, Place(n), Place(m));
            }
        }

        // Cluster names (the network a server belongs to), above each cluster.
        foreach (var cluster in _nodes.Where(n => !string.IsNullOrEmpty(n.RegionLabel)).GroupBy(n => n.RegionLabel!))
        {
            var pts = cluster.Select(Place).ToList();
            var at = new Point(pts.Average(p => p.X), pts.Min(p => p.Y) - 28);
            var label = Text(cluster.Key.ToUpperInvariant(), mono, 10, FontWeight.Normal, faint);
            ctx.DrawText(label, new Point(at.X - label.Width / 2, at.Y));
        }

        // Nodes: unselected first, the selected one last (on top).
        foreach (var n in _nodes.OrderBy(n => ReferenceEquals(n, SelectedItem)))
        {
            var p = Place(n);
            var selected = ReferenceEquals(n, SelectedItem);
            var lit = selected || ReferenceEquals(n, _hover) || n.IsOnline;
            var fg = selected || ReferenceEquals(n, _hover) ? text : n.IsOnline ? text : dim;

            if (selected)
                ctx.DrawRectangle(bg, new Pen(text, 1), new Rect(p.X - Ring / 2 + 0.5, p.Y - Ring / 2 + 0.5, Ring - 1, Ring - 1));

            var sq = new Rect(p.X - Square / 2, p.Y - Square / 2, Square, Square);
            var pen = new Pen(lit ? text : dim, 1);
            if (n.IsQuarantine || (!n.IsOnline && !n.IsOffline))
            {
                ctx.FillRectangle(bg, sq);
                ctx.DrawRectangle(pen, sq.Deflate(0.5));
            }
            else if (n.IsOffline)
            {
                ctx.DrawLine(pen, sq.TopLeft, sq.BottomRight);
                ctx.DrawLine(pen, sq.TopRight, sq.BottomLeft);
            }
            else if (IsFull(n))
            {
                ctx.FillRectangle(bg, sq);
                ctx.DrawRectangle(pen, sq.Deflate(0.5));
                ctx.FillRectangle(fg, new Rect(sq.X, sq.Y, Square / 2, Square));
            }
            else
            {
                ctx.FillRectangle(fg, sq);
            }

            var x = p.X + (selected ? Ring / 2 + 8 : Square / 2 + 8);
            var name = Text(n.Name, sans, selected ? 14 : 12, selected ? FontWeight.SemiBold : FontWeight.Medium, fg);
            ctx.DrawText(name, new Point(x, p.Y - name.Height / 2 - (n.IsOnline ? 6 : 0)));
            if (n.IsOnline && !string.IsNullOrEmpty(n.Population))
            {
                var pop = Text(n.Population, mono, 10, FontWeight.Normal, selected ? text : dim);
                ctx.DrawText(pop, new Point(x, p.Y + name.Height / 2 - 4));
            }
        }
    }

    private static bool IsFull(IMapNode n)
    {
        var parts = n.Population.Split('/');
        return parts.Length == 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var m) && m > 0 && a >= m;
    }

    private static FormattedText Text(string s, FontFamily family, double size, FontWeight weight, IBrush brush) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(family, FontStyle.Normal, weight), size, brush);
}
