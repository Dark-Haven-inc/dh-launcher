using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace DarkHaven.App.Controls;

/// <summary>
/// The "КАРТА СЕТИ" — a living star-map of the Dark Haven sector: a drifting nebula, a parallax
/// starfield, HUD range-rings, energy lanes between gate-linked regions, and star systems whose
/// glow / rotating reticles / population track their live status. Pan by dragging, zoom on the
/// wheel, click a system to select it.
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

    public static readonly StyledProperty<bool> AutoRotateProperty =
        AvaloniaProperty.Register<SectorMap, bool>(nameof(AutoRotate), true);

    /// <summary>Whether the holo-table keeps slowly spinning. On for the small, decorative СЕКТОР
    /// FRONTIER 15 map; off for anything meant to be searched/scanned (e.g. the РУхаб network map) —
    /// a moving target is the wrong call when the point is finding and clicking a specific thing.</summary>
    public bool AutoRotate
    {
        get => GetValue(AutoRotateProperty);
        set => SetValue(AutoRotateProperty, value);
    }

    public static readonly StyledProperty<bool> PerspectiveProperty =
        AvaloniaProperty.Register<SectorMap, bool>(nameof(Perspective), true);

    /// <summary>Whether nodes get bigger/smaller and pulled toward/away from center by depth. Tuned
    /// for a tight cluster near the middle (СЕКТОР FRONTIER 15's 5 regions) — spread nodes across
    /// nearly the whole disk (the РУхаб network map) and the SAME depth curve instead crushes
    /// far-from-center nodes together, undoing whatever layout spread them apart in the first place.
    /// Off keeps the tilt (still reads as a 3D deck) without that position-dependent distortion.</summary>
    public bool Perspective
    {
        get => GetValue(PerspectiveProperty);
        set => SetValue(PerspectiveProperty, value);
    }

    /// <summary>Raised with the clicked node (an <see cref="IMapNode"/>).</summary>
    public event EventHandler<object>? NodeInvoked;

    // palette
    private static readonly Color Bg = Color.Parse("#070B14");
    private static readonly Color Accent = Color.Parse("#3B82F6");
    private static readonly Color AccentBright = Color.Parse("#7FB4FF");
    private static readonly Color Cyan = Color.Parse("#7FD4FF");
    private static readonly Color Online = Color.Parse("#4ADE80");
    private static readonly Color Warn = Color.Parse("#F5B54C");
    private static readonly Color Offline = Color.Parse("#4E5F80");
    private static readonly Color Text = Color.Parse("#E4ECFA");
    private static readonly Color Dim = Color.Parse("#8395B8");
    private static readonly Color FrontierRed = Color.Parse("#F0384C");
    private static readonly double[] MainAxes = [0, Math.PI / 2, Math.PI, 3 * Math.PI / 2];
    private static readonly double[] DiagAxes = [Math.PI / 4, 3 * Math.PI / 4, 5 * Math.PI / 4, 7 * Math.PI / 4];

    // pseudo-3D "holo table": fixed camera tilt, slow auto-rotation, and a perspective depth scale —
    // all folded into ToScreen so panning/zooming/hit-testing keep working unchanged on top of it.
    private const double TiltDeg = 34.0;
    private static readonly double SinT = Math.Sin(TiltDeg * Math.PI / 180);
    private static readonly double CosT = Math.Cos(TiltDeg * Math.PI / 180);
    private const double PerspectiveK = 1.15;
    private const double RotSpeed = 0.08;

    private readonly DispatcherTimer _timer;
    private readonly Star[] _stars;
    private readonly Nebula[] _nebulae;
    private readonly Typeface _face = new("Inter, Segoe UI, sans-serif");
    private readonly Typeface _faceBold = new("Inter, Segoe UI, sans-serif", weight: FontWeight.Bold);

    // --- render caches ---
    // Every one of these draws a color the layout can repeat across dozens of nodes (up to ~45+ on
    // the РУхаб map), and every gradient here is positioned via RelativeUnit.Relative — meaning the
    // SAME brush instance paints correctly at any node's screen position/size. Before this, DrawNode
    // allocated a fresh gradient brush + several pens + geometries for EVERY node on EVERY ~45ms
    // tick; on a busy region map that was tens of thousands of small allocations a second, pure GC
    // pressure with no visual benefit since the values never actually varied per node. Keying by the
    // handful of distinct state colors that actually occur turns that into dictionary lookups.
    private readonly Dictionary<(Color Color, double Opacity), IBrush> _solidCache = new();
    private readonly Dictionary<(Color Color, double Opacity, double Thickness, PenLineCap Cap), Pen> _penCache = new();
    private readonly Dictionary<Color, RadialGradientBrush> _glowCache = new();
    private readonly Dictionary<Color, RadialGradientBrush> _regionBlobCache = new();
    private readonly Dictionary<Color, (Pen Tether, IBrush GroundFill, Pen GroundStroke)> _groundKitCache = new();
    private readonly Dictionary<(Color Core, bool Offline), (IBrush Fill, Pen Stroke)> _coreKitCache = new();

    // Legend/hint text never changes at runtime — no reason to re-measure it on every repaint.
    private readonly FormattedText _hintText;
    private FormattedText _scaleText;
    private readonly FormattedText _legendOnlineText, _legendOfflineText, _legendQuarantineText;

    // Busy scenes (the РУхаб region map, dozens of dots) skip the secondary diagonal sparkle rays —
    // a subtle accent on a handful of beacons, real geometry+brush cost multiplied by node count.
    private bool _dense;

    private double _phase;
    private double _scale = 1.0;
    private double _targetScale = 1.0;
    private Point _zoomAnchorScreen;
    private Point _zoomAnchorLocal;
    private Vector _pan;
    private Vector _targetPan;
    private bool _hasTargetPan;
    private Point? _dragFrom;
    private Vector _dragPanStart;
    private bool _dragged;
    private IMapNode? _hover;
    private Point _hoverAt;
    private bool _needsFit = true;
    private bool _userInteracted; // once true, a data refresh no longer snaps the view back to Fit()
    private INotifyCollectionChanged? _subscribedItems;

    private object? _lastSelected;
    private double _selectPing;          // 0..1, expanding ring after a selection
    private double _shootT = -1;         // shooting-star progress, <0 = idle
    private double _shootAngle;
    private double _shootNextIn = 3;

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

    public SectorMap()
    {
        ClipToBounds = true;
        Focusable = true;

        // Trimmed from 380 stars / 6 nebulae / 30ms ticks — this canvas repaints continuously by
        // design (the holo-table keeps slowly rotating), so cutting the per-frame draw count and
        // tick rate is real, ongoing CPU/GPU savings on lower-end machines, not a one-off cost.
        var rng = new Random(0x5EC7);
        _stars = new Star[220];
        for (var i = 0; i < _stars.Length; i++)
        {
            var layer = i < 115 ? 0 : i < 185 ? 1 : 2;
            var size = layer switch { 0 => rng.NextDouble() * 0.7 + 0.25, 1 => rng.NextDouble() * 1.1 + 0.5, _ => rng.NextDouble() * 1.7 + 0.9 };
            var alpha = layer switch { 0 => rng.NextDouble() * 0.25 + 0.08, 1 => rng.NextDouble() * 0.4 + 0.15, _ => rng.NextDouble() * 0.55 + 0.3 };
            _stars[i] = new Star(rng.NextDouble(), rng.NextDouble(), size, alpha, layer);
        }

        var nebColors = new[] { Color.Parse("#1E3A8A"), Color.Parse("#3B1E8A"), Color.Parse("#0E5C6E"), Color.Parse("#5A2A7A") };
        _nebulae = new Nebula[4];
        for (var i = 0; i < _nebulae.Length; i++)
            _nebulae[i] = new Nebula(
                rng.NextDouble(), rng.NextDouble(),
                rng.NextDouble() * 260 + 200,
                nebColors[i % nebColors.Length],
                rng.NextDouble() * 0.6 + 0.2,
                rng.NextDouble() * 6.28);

        _hintText = Fmt("тащить — двигать · колесо — масштаб", Color.Parse("#5A6B8A"), 14, false);
        _scaleText = Fmt($"{345}%",Color.Parse("#5A6B8A"),14, false);
        _legendOnlineText = Fmt("онлайн", Dim, 9.5, false);
        _legendOfflineText = Fmt("офлайн", Dim, 9.5, false);
        _legendQuarantineText = Fmt("карантин", Dim, 9.5, false);

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(45), DispatcherPriority.Background, (_, _) =>
        {
            _phase += 0.024;

            if (Math.Abs(_targetScale - _scale) > 0.0004)
            {
                _scale += (_targetScale - _scale) * 0.22;
                // re-derive screen position of the same LOCAL (post-projection) point at the new scale —
                // exact, no need to invert the rotating 3D projection, so it can't blow up mid-spin
                var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
                var cur = new Point(c.X + _zoomAnchorLocal.X * BasePx * _scale + _pan.X, c.Y + _zoomAnchorLocal.Y * BasePx * _scale + _pan.Y);
                _pan += new Vector(_zoomAnchorScreen.X - cur.X, _zoomAnchorScreen.Y - cur.Y);
            }

            if (_hasTargetPan)
            {
                _pan += (_targetPan - _pan) * 0.16;
                if ((_targetPan - _pan).Length < 0.5) { _pan = _targetPan; _hasTargetPan = false; }
            }

            if (_selectPing is > 0 and < 1) _selectPing = Math.Min(1, _selectPing + 0.0675);

            if (_shootT < 0)
            {
                _shootNextIn -= 0.045;
                if (_shootNextIn <= 0) { _shootT = 0; _shootAngle = new Random().NextDouble() * Math.Tau; }
            }
            else
            {
                _shootT += 0.045;
                if (_shootT >= 1) { _shootT = -1; _shootNextIn = new Random().Next(4, 11); }
            }

            InvalidateVisual();
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _timer.Start(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { base.OnDetachedFromVisualTree(e); _timer.Stop(); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty)
        {
            if (_subscribedItems is not null) _subscribedItems.CollectionChanged -= OnItemsCollectionChanged;
            _subscribedItems = ItemsSource as INotifyCollectionChanged;
            if (_subscribedItems is not null) _subscribedItems.CollectionChanged += OnItemsCollectionChanged;
            _needsFit = true;
        }
        if (change.Property == SelectedItemProperty && !ReferenceEquals(SelectedItem, _lastSelected))
        {
            _lastSelected = SelectedItem;
            _selectPing = 0.001;
            PanToSelected();
        }
        if (change.Property == ItemsSourceProperty || change.Property == SelectedItemProperty || change.Property == BoundsProperty)
            InvalidateVisual();
    }

    /// <summary>ItemsSource is bound once and its contents refreshed in place (Clear+Add, not a new
    /// collection instance), so ItemsSourceProperty itself never fires again after the first bind —
    /// without this, Fit() would run exactly once, quite possibly against an empty list a moment
    /// before the real data arrives, and lock in a nonsense zoom/pan forever. Re-fit on every refresh
    /// UNTIL the player actually touches the map themselves — then their pan/zoom wins.</summary>
    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_userInteracted) return;
        _needsFit = true;
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

    // --- camera ---

    private double BasePx => Math.Max(120, Math.Min(Bounds.Width, Bounds.Height) - 130);

    private Point ToScreen(IMapNode n) => ToScreen(new Point(n.X, n.Y), NodeHeight(n));

    private Point ToScreen(Point map) => ToScreen(map, 0);

    /// <summary>Projects a map-space point (plus a "floating height" above the holo deck) through
    /// the tilt/rotation/perspective camera, then applies the existing pan+zoom exactly as before —
    /// so dragging, wheel-zoom and hit-testing all keep working on top of the 3D look for free.</summary>
    private Point ToScreen(Point map, double height)
    {
        var local = Project(map.X, map.Y, height);
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        return new Point(c.X + local.X * BasePx * _scale + _pan.X, c.Y + local.Y * BasePx * _scale + _pan.Y);
    }

    private double CurrentRotation => AutoRotate ? _phase * RotSpeed : 0.0;

    private Point Project(double mapX, double mapY, double height)
    {
        var dx0 = mapX - 0.5;
        var dz0 = mapY - 0.5;
        var rot = CurrentRotation;
        var cr = Math.Cos(rot);
        var sr = Math.Sin(rot);
        var dx = dx0 * cr - dz0 * sr;
        var dz = dx0 * sr + dz0 * cr;
        var depth = Perspective ? 1.0 / (1.0 + dz * PerspectiveK) : 1.0;
        return new Point(dx * depth, dz * SinT * depth - height * CosT);
    }

    private double NodeHeight(IMapNode n)
    {
        var seed = (n.Name.GetHashCode() & 0xFFFF) / 65535.0 * Math.Tau;
        var baseH = n.IsCentral ? 0.115 : 0.065;
        return baseH + 0.018 * Math.Sin(_phase * 0.6 + seed);
    }

    /// <summary>How much bigger/smaller a node should render based on how "close to camera" the
    /// rotating scene currently puts it — the other half of making this read as 3D, not just tilted.</summary>
    private double DepthScale(IMapNode n)
    {
        var dx0 = n.X - 0.5;
        var dz0 = n.Y - 0.5;
        var rot = CurrentRotation;
        var dz = dx0 * Math.Sin(rot) + dz0 * Math.Cos(rot);
        return Perspective ? 1.0 / (1.0 + dz * PerspectiveK) : 1.0;
    }

    private void Fit(IReadOnlyList<IMapNode> nodes)
    {
        _needsFit = false;
        _scale = _targetScale = 1.0;
        _pan = default;
        _hasTargetPan = false;

        double minX = 1, minY = 1, maxX = 0, maxY = 0;
        foreach (var n in nodes)
        {
            minX = Math.Min(minX, n.X); maxX = Math.Max(maxX, n.X);
            minY = Math.Min(minY, n.Y); maxY = Math.Max(maxY, n.Y);
        }

        var spanX = Math.Max(0.15, maxX - minX);
        var spanY = Math.Max(0.15, maxY - minY) * SinT; // the holo deck is tilted, so its screen footprint is squished
        // Fixed-size labels don't shrink with the camera, so the only way to give them breathing
        // room is to push nodes further apart on screen — i.e. start zoomed in close, not fitted
        // wide. The player pans/wheel-zooms from here; wheel zoom's own range (0.5–4.0) is untouched.
        _targetScale = _scale = Math.Clamp(2.2 / Math.Max(spanX, spanY), 1.8, 3.8);
		ScaleTextUpdate();

        var mid = ToScreen(new Point((minX + maxX) / 2, (minY + maxY) / 2));
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        _pan = new Vector(c.X - mid.X, c.Y - mid.Y);
    }

    private void PanToSelected()
    {
        if (SelectedItem is not IMapNode n || Bounds.Width < 2) return;
        var cur = ToScreen(n);
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        _targetPan = _pan + new Vector(c.X - cur.X, c.Y - cur.Y);
        _hasTargetPan = true;
    }

    // --- interaction ---

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragFrom = e.GetPosition(this);
        _dragPanStart = _pan;
        _dragged = false;
        _hasTargetPan = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var now = e.GetPosition(this);

        if (_dragFrom is { } from)
        {
            var d = new Vector(now.X - from.X, now.Y - from.Y);
            if (Math.Abs(d.X) + Math.Abs(d.Y) > 3) { _dragged = true; _userInteracted = true; }
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

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is not null) { _hover = null; InvalidateVisual(); }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        if (!_dragged && HitTest(e.GetPosition(this)) is { } hit)
        {
            SelectedItem = hit;
            NodeInvoked?.Invoke(this, hit);
        }
        _dragFrom = null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _userInteracted = true;
        var cursor = e.GetPosition(this);
        _zoomAnchorScreen = cursor;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        _zoomAnchorLocal = new Point((cursor.X - c.X - _pan.X) / (BasePx * _scale), (cursor.Y - c.Y - _pan.Y) / (BasePx * _scale));
        _targetScale = Math.Clamp(_targetScale * (e.Delta.Y > 0 ? 1.16 : 1 / 1.16), 0.5, 4.0);
        ScaleTextUpdate();
        e.Handled = true;
    }

    private void ScaleTextUpdate()
    {
        _scaleText = Fmt($"{Math.Round(_targetScale * 100)}%",Color.Parse("#5A6B8A"),14, false);
    }

    private IMapNode? HitTest(Point screen)
    {
        IMapNode? best = null;
        var bestD = double.MaxValue;
        foreach (var n in Nodes())
        {
            var s = ToScreen(n);
            var d = Math.Sqrt((s.X - screen.X) * (s.X - screen.X) + (s.Y - screen.Y) * (s.Y - screen.Y));
            if (d < 28 && d < bestD) { bestD = d; best = n; }
        }
        return best;
    }

    // --- render ---

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        ctx.DrawRectangle(new SolidColorBrush(Bg), null, new Rect(b.Size));

        DrawNebula(ctx, b);
        DrawStarfield(ctx, b);
        DrawShootingStar(ctx, b);

        var nodes = Nodes();
        if (nodes.Count == 0)
        {
            DrawCentered(ctx, "нет данных о секторе", Dim, 13);
            return;
        }
        if (_needsFit && b.Width > 1 && b.Height > 1)
            Fit(nodes);

        _dense = nodes.Count > 24;

        DrawRangeRings(ctx, b);

        var hasRegions = nodes.Any(n => !string.IsNullOrEmpty(n.RegionLabel));
        // Computed once (not once per drawing pass) and handed to both callers — this walks every
        // node and averages screen points per region, no reason to pay for that twice a frame.
        var regions = hasRegions ? ComputeRegions(nodes) : null;
        if (regions is not null) DrawRegionBlobs(ctx, regions);

        var byName = new Dictionary<string, IMapNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes) byName[n.Name] = n;

        DrawLanes(ctx, nodes, byName);

        foreach (var n in nodes)
            DrawNode(ctx, n);

        // Region mode: dozens of small dots inside a few named clusters — a permanent label under
        // EVERY dot would be the exact crowding this whole map already fought its way out of once.
        // The region's own big title carries the "what is this" job; a dot's own name shows on
        // hover (tooltip, already implemented) and on click (the side panel) instead.
        if (!hasRegions)
            foreach (var n in nodes)
                DrawLabel(ctx, n);

        if (regions is not null) DrawRegionTitles(ctx, regions); else DrawClusterTitle(ctx, nodes);

        if (_hover is { } hv)
            DrawTooltip(ctx, hv);

        DrawLegend(ctx, b);
    }

    private void DrawNebula(DrawingContext ctx, Rect b)
    {
        foreach (var neb in _nebulae)
        {
            var cx = neb.X * b.Width + Math.Sin(_phase * neb.Drift * 0.3 + neb.Phase) * 40 + _pan.X * 0.05;
            var cy = neb.Y * b.Height + Math.Cos(_phase * neb.Drift * 0.24 + neb.Phase) * 30 + _pan.Y * 0.05;
            var pulse = 0.9 + 0.1 * Math.Sin(_phase * 0.5 + neb.Phase);

            var brush = new RadialGradientBrush
            {
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(new Color(64, neb.C.R, neb.C.G, neb.C.B), 0),
                    new GradientStop(new Color(22, neb.C.R, neb.C.G, neb.C.B), 0.5),
                    new GradientStop(new Color(0, neb.C.R, neb.C.G, neb.C.B), 1),
                },
            };
            var r = neb.R * pulse;
            ctx.DrawEllipse(brush, null, new Point(cx, cy), r, r * 0.75);
        }
    }

    private void DrawStarfield(DrawingContext ctx, Rect b)
    {
        foreach (var s in _stars)
        {
            var par = s.Layer switch { 0 => 0.05, 1 => 0.13, _ => 0.28 };
            var x = ((s.X * b.Width + _pan.X * par) % b.Width + b.Width) % b.Width;
            var y = ((s.Y * b.Height + _pan.Y * par) % b.Height + b.Height) % b.Height;
            var tw = s.Layer == 0 ? 1 : 0.55 + 0.45 * Math.Sin(_phase * 1.3 + s.X * 55 + s.Y * 20);
            var a = Math.Clamp(s.A * tw, 0, 1);
            var col = s.Layer == 2 ? Lerp(Text, Cyan, 0.3) : Text;
            ctx.DrawEllipse(new SolidColorBrush(col, a), null, new Point(x, y), s.R, s.R);
            if (s.Layer == 2 && tw > 0.85)
                ctx.DrawEllipse(new SolidColorBrush(col, a * 0.18), null, new Point(x, y), s.R * 3.5, s.R * 3.5);
        }
    }

    private void DrawShootingStar(DrawingContext ctx, Rect b)
    {
        if (_shootT < 0) return;
        var dir = new Point(Math.Cos(_shootAngle), Math.Sin(_shootAngle));
        var start = new Point(b.Width * 0.5 - dir.X * b.Width * 0.7, b.Height * 0.5 - dir.Y * b.Width * 0.7);
        var head = new Point(start.X + dir.X * b.Width * 1.4 * _shootT, start.Y + dir.Y * b.Width * 1.4 * _shootT);
        var tail = new Point(head.X - dir.X * 90, head.Y - dir.Y * 90);
        var fade = Math.Sin(_shootT * Math.PI);
        var pen = new Pen(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(tail, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(head, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(new Color(0, 255, 255, 255), 0), new GradientStop(new Color((byte)(200 * fade), 255, 255, 255), 1) },
        }, 1.6) { LineCap = PenLineCap.Round };
        ctx.DrawLine(pen, tail, head);
        ctx.DrawEllipse(new SolidColorBrush(Colors.White, fade), null, head, 1.8, 1.8);
    }

    private void DrawRangeRings(DrawingContext ctx, Rect b)
    {
        var c = new Point(b.Width / 2 + _pan.X * 0.6, b.Height / 2 + _pan.Y * 0.6);
        var basis = BasePx * _scale;
        for (var i = 1; i <= 3; i++)
        {
            var rr = basis * 0.22 * i;
            // dash offset animates every frame per ring, so only the brush portion is cacheable
            var pen = new Pen(Solid(Cyan, 0.08), 1)
            {
                DashStyle = new DashStyle([1, 6], _phase * (i % 2 == 0 ? 2 : -2)),
            };
            ctx.DrawEllipse(null, pen, c, rr, rr * SinT); // squished into an ellipse — a tilted holo deck, not a flat circle
        }
        var spokePen = SolidPen(Cyan, 0.04, 1);
        for (var k = 0; k < 12; k++)
        {
            var ang = k / 12.0 * Math.Tau + _phase * 0.02;
            var p1 = new Point(c.X + Math.Cos(ang) * basis * 0.16, c.Y + Math.Sin(ang) * basis * 0.16 * SinT);
            var p2 = new Point(c.X + Math.Cos(ang) * basis * 0.66, c.Y + Math.Sin(ang) * basis * 0.66 * SinT);
            ctx.DrawLine(spokePen, p1, p2);
        }
    }

    private void DrawLanes(DrawingContext ctx, IReadOnlyList<IMapNode> nodes, Dictionary<string, IMapNode> byName)
    {
        var drawn = new HashSet<(string, string)>();
        foreach (var n in nodes)
        {
            foreach (var nb in n.Neighbours)
            {
                if (!byName.TryGetValue(nb, out var other)) continue;
                var key = string.CompareOrdinal(n.Name, other.Name) < 0 ? (n.Name, other.Name) : (other.Name, n.Name);
                if (!drawn.Add(key)) continue;

                var a = ToScreen(n);
                var d = ToScreen(other);
                var mid = new Point((a.X + d.X) / 2, (a.Y + d.Y) / 2);
                var dist = Math.Sqrt((d.X - a.X) * (d.X - a.X) + (d.Y - a.Y) * (d.Y - a.Y));
                var normal = new Vector(-(d.Y - a.Y), d.X - a.X);
                if (normal.Length > 0.01) normal /= normal.Length;
                var arc = Math.Min(60, dist * 0.14);
                var ctrl = new Point(mid.X + normal.X * arc, mid.Y + normal.Y * arc);

                var live = n.IsOnline || other.IsOnline;
                var col = live ? Accent : Offline;

                var g = new StreamGeometry();
                using (var gc = g.Open())
                {
                    gc.BeginFigure(a, false);
                    gc.QuadraticBezierTo(ctrl, d);
                    gc.EndFigure(false);
                }

                ctx.DrawGeometry(null, SolidPen(col, live ? 0.07 : 0.04, 7, PenLineCap.Round), g);
                ctx.DrawGeometry(null, SolidPen(col, live ? 0.18 : 0.1, 2.6, PenLineCap.Round), g);
                ctx.DrawGeometry(null, SolidPen(live ? AccentBright : col, live ? 0.5 : 0.22, 1, PenLineCap.Round), g);

                if (live)
                {
                    for (var m = 0; m < 3; m++)
                    {
                        var t = ((_phase * 0.3) + m / 3.0 + n.X * 0.7) % 1.0;
                        var p = Quad(a, ctrl, d, t);
                        ctx.DrawEllipse(Solid(AccentBright, 0.9), null, p, 2.4, 2.4);
                        ctx.DrawEllipse(Solid(AccentBright, 0.25), null, p, 5.5, 5.5);
                        var pt = Quad(a, ctrl, d, Math.Max(0, t - 0.05));
                        ctx.DrawLine(SolidPen(AccentBright, 0.35, 1.6, PenLineCap.Round), pt, p);
                    }
                }
            }
        }
    }

    private void DrawNode(DrawingContext ctx, IMapNode n)
    {
        var p = ToScreen(n);
        var selected = ReferenceEquals(n, SelectedItem);
        var hovered = ReferenceEquals(n, _hover);
        // the central station reads as Dark Haven blue even when the server is down
        var core = n.IsQuarantine ? Warn : n.IsOnline ? Online : n.IsCentral ? Accent : n.IsOffline ? Offline : Dim;
        var depthScale = DepthScale(n);
        var baseR = (n.IsCentral ? 16.0 : 9.0) * (hovered ? 1.12 : 1) * depthScale;
        var pulse = (n.IsOnline || n.IsCentral) ? 1 + 0.09 * Math.Sin(_phase * 2.4 + n.X * 10) : 1;
        var r = baseR * pulse;

        // tether + ground shadow — the beacon floats above the tilted holo deck, this is what pins it there
        var ground = ToScreen(new Point(n.X, n.Y));
        var (tetherPen, groundFill, groundStroke) = GroundKit(core);
        ctx.DrawLine(tetherPen, ground, p);
        ctx.DrawEllipse(groundFill, groundStroke, ground, Math.Max(3, r * 0.55), Math.Max(2, r * 0.55 * SinT));

        // atmospheric glow
        var glowCol = selected ? AccentBright : n.IsCurrent ? Online : n.IsCentral ? AccentBright : core;
        var gr = r * (n.IsOnline || selected || n.IsCentral ? 4.2 : 2.6);
        ctx.DrawEllipse(GlowBrush(glowCol), null, p, gr, gr);

        // selection ping — opacity animates every frame, so this one pen isn't a cache candidate
        if (selected && _selectPing is > 0 and < 1)
        {
            var pr = r + _selectPing * 42;
            ctx.DrawEllipse(null, new Pen(Solid(AccentBright, (1 - _selectPing) * 0.7), 2), p, pr, pr);
        }

        // rotating reticle for online / central
        if (n.IsOnline || n.IsCentral)
            DrawReticle(ctx, p, r + 7, glowCol, _phase * (n.IsCentral ? 0.8 : 1.4), n.IsCentral ? 4 : 3);
        if (n.IsCentral)
            DrawReticle(ctx, p, r + 13, Lerp(glowCol, Cyan, 0.4), -_phase * 0.5, 4);

        // quarantine hazard ring
        if (n.IsQuarantine)
            ctx.DrawEllipse(null, new Pen(Solid(Warn, 0.6), 1.4) { DashStyle = new DashStyle([2, 3], _phase * 4) }, p, r + 6, r + 6);

        // body — a beacon, not a planet: a bright core + a 4-point sparkle flare (this is a region marker on a network map, not a celestial body)
        var coreBright = Lerp(core, Colors.White, n.IsOffline ? 0.15 : 0.55);
        var rayAlpha = n.IsOffline ? 0.35 : 0.85;
        var rayLen = r * 1.4;
        var rayW = r * 0.16;
        foreach (var ang in MainAxes)
            DrawSparkleRay(ctx, p, ang, rayLen, rayW, coreBright, rayAlpha);
        // Dense scenes (the РУхаб region map — dozens of dots) drop the secondary diagonal flare:
        // a subtle accent per beacon, but 4 more geometry+brush allocations multiplied by node count.
        if (!_dense)
            foreach (var ang in DiagAxes)
                DrawSparkleRay(ctx, p, ang, rayLen * 0.5, rayW * 0.65, core, rayAlpha * 0.7);

        var (coreFill, coreStroke) = CoreKit(core, n.IsOffline);
        ctx.DrawEllipse(coreFill, coreStroke, p, r * 0.42, r * 0.42);

        // "you are here" bracket + tag
        if (n.IsCurrent)
        {
            DrawBrackets(ctx, p, r + 11, Online, 1 + 0.12 * Math.Sin(_phase * 3));
            var badge = Fmt("ВЫ ЗДЕСЬ", Online, 8.5, true);
            var bw = badge.Width + 12;
            var br = new Rect(p.X - bw / 2, p.Y - r - 26, bw, 15);
            ctx.DrawRectangle(Solid(Online, 0.16), SolidPen(Online, 1, 1), br, 4, 4);
            ctx.DrawText(badge, new Point(br.X + 6, br.Y + 2.5));
        }

        // selection target brackets
        if (selected)
            DrawBrackets(ctx, p, r + 16, AccentBright, 1);
    }

    private void DrawReticle(DrawingContext ctx, Point c, double radius, Color color, double rot, int arcs)
    {
        for (var i = 0; i < arcs; i++)
        {
            var a0 = rot + i * Math.Tau / arcs;
            var a1 = a0 + 0.7;
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(new Point(c.X + Math.Cos(a0) * radius, c.Y + Math.Sin(a0) * radius), false);
                gc.ArcTo(new Point(c.X + Math.Cos(a1) * radius, c.Y + Math.Sin(a1) * radius),
                    new Size(radius, radius), 0, false, SweepDirection.Clockwise);
                gc.EndFigure(false);
            }
            ctx.DrawGeometry(null, SolidPen(color, 0.55, 1.4, PenLineCap.Round), g);
        }
    }

    /// <summary>One spike of a 4/8-point sparkle-flare marker: a tapered kite from the center out to a tip.</summary>
    private void DrawSparkleRay(DrawingContext ctx, Point c, double angle, double length, double width, Color color, double alpha)
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
        ctx.DrawGeometry(Solid(color, alpha), null, g);
    }

    private void DrawClusterTitle(DrawingContext ctx, IReadOnlyList<IMapNode> nodes)
    {
        if (nodes.Count == 0) return;

        var minY = double.MaxValue;
        var sumX = 0.0;
        foreach (var n in nodes)
        {
            var s = ToScreen(n);
            minY = Math.Min(minY, s.Y);
            sumX += s.X;
        }
        var cx = sumX / nodes.Count;
        var y = Math.Max(4, minY - 52); // stay on-screen even when a spread-out layout pushes minY negative

        var word = Fmt("FRONTIER", Lerp(Text, Colors.White, 0.25), 19, true);
        var num = Fmt("15", FrontierRed, 19, true);
        var gap = 8.0;
        var totalW = word.Width + gap + num.Width;
        var x = Math.Clamp(cx - totalW / 2, 6, Math.Max(6, Bounds.Width - totalW - 6));

        var underline = new Rect(x, y + word.Height + 2, totalW, 2);
        ctx.DrawRectangle(new SolidColorBrush(FrontierRed, 0.55), null, underline);
        ctx.DrawText(word, new Point(x, y));
        ctx.DrawText(num, new Point(x + word.Width + gap, y));
    }

    private static readonly Color[] RegionPalette =
    [
        Color.Parse("#4ADE80"), Color.Parse("#F0384C"), Color.Parse("#3B82F6"), Color.Parse("#F5B54C"),
        Color.Parse("#A855F7"), Color.Parse("#22D3EE"), Color.Parse("#F472B6"), Color.Parse("#84CC16"),
    ];

    private static Color RegionColor(string label)
    {
        var h = 0;
        foreach (var c in label) h = (h * 31 + c) & 0x7FFFFFFF;
        return RegionPalette[h % RegionPalette.Length];
    }

    /// <summary>One entry per distinct <see cref="IMapNode.RegionLabel"/> — its on-screen centroid and
    /// a radius that comfortably encloses every member, used both for the soft boundary blob and for
    /// placing that region's big floating title.</summary>
    private Dictionary<string, (Point Centroid, double Radius, Color Color)> ComputeRegions(IReadOnlyList<IMapNode> nodes)
    {
        var byLabel = new Dictionary<string, List<Point>>();
        foreach (var n in nodes)
        {
            if (string.IsNullOrEmpty(n.RegionLabel)) continue;
            if (!byLabel.TryGetValue(n.RegionLabel, out var list)) byLabel[n.RegionLabel] = list = [];
            list.Add(ToScreen(n));
        }

        var result = new Dictionary<string, (Point, double, Color)>();
        foreach (var (label, pts) in byLabel)
        {
            var cx = pts.Average(p => p.X);
            var cy = pts.Average(p => p.Y);
            var radius = 46.0;
            foreach (var p in pts)
                radius = Math.Max(radius, Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)) + 34);
            result[label] = (new Point(cx, cy), radius, RegionColor(label));
        }
        return result;
    }

    /// <summary>A soft coloured boundary behind each named region — one hue per network, drawn before
    /// the lanes/nodes so it reads as "ground" the beacons sit on, like the nebula outlines on a
    /// galaxy map.</summary>
    private void DrawRegionBlobs(DrawingContext ctx, Dictionary<string, (Point Centroid, double Radius, Color Color)> regions)
    {
        foreach (var (centroid, radius, color) in regions.Values)
        {
            ctx.DrawEllipse(RegionBlobBrush(color), null, centroid, radius, radius * 0.82);
            // dash phase animates with _phase, so this one pen can't be cached like the brush above
            ctx.DrawEllipse(null, new Pen(Solid(color, 0.28), 1.4) { DashStyle = new DashStyle([2, 4], _phase) },
                centroid, radius, radius * 0.82);
        }
    }

    /// <summary>The big always-visible name over each region — e.g. "Corvax" — drawn after the nodes
    /// so it stays legible over the starfield/blob.</summary>
    private void DrawRegionTitles(DrawingContext ctx, Dictionary<string, (Point Centroid, double Radius, Color Color)> regions)
    {
        foreach (var (label, (centroid, radius, color)) in regions)
        {
            var t = Fmt(label, Lerp(color, Colors.White, 0.4), 14, true);
            var x = Math.Clamp(centroid.X - t.Width / 2, 6, Math.Max(6, Bounds.Width - t.Width - 6));
            var y = Math.Max(4, centroid.Y - radius * 0.82 - t.Height - 8);

            const double pad = 5.0;
            ctx.DrawRectangle(Solid(Bg, 0.6), null, new Rect(x - pad, y - 2, t.Width + pad * 2, t.Height + 4), 4, 4);
            ctx.DrawText(t, new Point(x, y));
        }
    }

    private static void DrawBrackets(DrawingContext ctx, Point c, double d, Color color, double s)
    {
        d *= s;
        var len = 5.0;
        var pen = new Pen(new SolidColorBrush(color, 0.9), 1.6) { LineCap = PenLineCap.Round };
        foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
        {
            var corner = new Point(c.X + sx * d, c.Y + sy * d);
            ctx.DrawLine(pen, corner, new Point(corner.X - sx * len, corner.Y));
            ctx.DrawLine(pen, corner, new Point(corner.X, corner.Y - sy * len));
        }
    }

    private void DrawLabel(DrawingContext ctx, IMapNode n)
    {
        var p = ToScreen(n);
        var selected = ReferenceEquals(n, SelectedItem);
        var r = (n.IsCentral ? 15.0 : 9.0) + 8;

        var name = Fmt(n.Name, selected ? Text : Dim, selected ? 12.5 : 11.5, selected);
        string? sub = n.IsOnline && n.Population is { Length: > 0 } pop && pop != "0" ? $"◆ {pop}"
                    : n.IsQuarantine ? "карантин"
                    : n.IsOffline ? "офлайн" : null;
        var subT = sub is null ? null : Fmt(sub, n.IsQuarantine ? Warn : n.IsOnline ? AccentBright : Dim, 9.5, false);

        var w = Math.Max(name.Width, subT?.Width ?? 0) + 16;
        var h = 8 + name.Height + (subT is null ? 0 : subT.Height + 1);
        var plate = new Rect(p.X - w / 2, p.Y + r + 7, w, h);

        // leader line
        ctx.DrawLine(new Pen(new SolidColorBrush(selected ? AccentBright : Dim, 0.5), 1), new Point(p.X, p.Y + r), new Point(p.X, plate.Y));

        ctx.DrawRectangle(
            new SolidColorBrush(Color.Parse("#0C1424"), selected ? 0.92 : 0.7),
            new Pen(new SolidColorBrush(selected ? AccentBright : Color.Parse("#24365A"), selected ? 0.9 : 0.5), 1),
            plate, 4, 4);
        ctx.DrawText(name, new Point(plate.X + (w - name.Width) / 2, plate.Y + 4));
        if (subT is not null)
            ctx.DrawText(subT, new Point(plate.X + (w - subT.Width) / 2, plate.Y + 4 + name.Height + 1));
    }

    private void DrawTooltip(DrawingContext ctx, IMapNode n)
    {
        const double w = 220;
        var title = Fmt(n.Name, Text, 12.5, true);
        var body = new FormattedText(
            string.IsNullOrWhiteSpace(n.Blurb) ? "—" : n.Blurb,
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _face, 11, new SolidColorBrush(Dim)) { MaxTextWidth = w - 22 };

        var h = 14 + title.Height + 5 + body.Height + 12;
        var x = Math.Clamp(_hoverAt.X + 18, 6, Math.Max(6, Bounds.Width - w - 6));
        var y = Math.Clamp(_hoverAt.Y + 18, 6, Math.Max(6, Bounds.Height - h - 6));
        var rect = new Rect(x, y, w, h);

        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#0B1322"), 0.98), new Pen(new SolidColorBrush(Accent, 0.7), 1), rect, 7, 7);
        ctx.DrawRectangle(new SolidColorBrush(Accent, 0.9), null, new Rect(x, y, 3, h), 2, 2);
        ctx.DrawText(title, new Point(x + 12, y + 9));
        ctx.DrawText(body, new Point(x + 12, y + 9 + title.Height + 5));
    }

    private void DrawLegend(DrawingContext ctx, Rect b)
    {
        var items = new (Color Col, FormattedText Text)[]
        {
            (Online, _legendOnlineText), (Offline, _legendOfflineText), (Warn, _legendQuarantineText),
        };
        var x = 16.0;
        var y = b.Height - 24;
        foreach (var (col, t) in items)
        {
            ctx.DrawEllipse(Solid(col), null, new Point(x + 4, y + 5), 3.5, 3.5);
            ctx.DrawText(t, new Point(x + 13, y));
            x += 13 + t.Width + 16;
        }
        ctx.DrawText(_hintText, new Point(b.Width - _hintText.Width - 14, b.Height - _hintText.Height - 12));
        ctx.DrawText(_scaleText, new Point(b.Width - _scaleText.Width - 14, b.Height - _scaleText.Height - 24));
    }

    // --- helpers ---

    private static Point Quad(Point a, Point c, Point d, double t)
    {
        var u = 1 - t;
        return new Point(
            u * u * a.X + 2 * u * t * c.X + t * t * d.X,
            u * u * a.Y + 2 * u * t * c.Y + t * t * d.Y);
    }

    private static Color Lerp(Color a, Color b, double t) => new(
        (byte)(a.A + (b.A - a.A) * t),
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    private FormattedText Fmt(string text, Color color, double size, bool bold) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? _faceBold : _face, size, new SolidColorBrush(color));

    // --- render cache helpers (see the cache fields' comment) ---

    private IBrush Solid(Color color, double opacity = 1)
    {
        var key = (color, opacity);
        if (_solidCache.TryGetValue(key, out var b)) return b;
        return _solidCache[key] = new SolidColorBrush(color, opacity);
    }

    private Pen SolidPen(Color color, double opacity, double thickness, PenLineCap cap = PenLineCap.Flat)
    {
        var key = (color, opacity, thickness, cap);
        if (_penCache.TryGetValue(key, out var p)) return p;
        return _penCache[key] = new Pen(Solid(color, opacity), thickness) { LineCap = cap };
    }

    private RadialGradientBrush GlowBrush(Color color)
    {
        if (_glowCache.TryGetValue(color, out var b)) return b;
        return _glowCache[color] = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(new Color(90, color.R, color.G, color.B), 0),
                new GradientStop(new Color(24, color.R, color.G, color.B), 0.5),
                new GradientStop(new Color(0, color.R, color.G, color.B), 1),
            },
        };
    }

    private RadialGradientBrush RegionBlobBrush(Color color)
    {
        if (_regionBlobCache.TryGetValue(color, out var b)) return b;
        return _regionBlobCache[color] = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(new Color(40, color.R, color.G, color.B), 0),
                new GradientStop(new Color(14, color.R, color.G, color.B), 0.6),
                new GradientStop(new Color(0, color.R, color.G, color.B), 1),
            },
        };
    }

    private (Pen Tether, IBrush GroundFill, Pen GroundStroke) GroundKit(Color core)
    {
        if (_groundKitCache.TryGetValue(core, out var kit)) return kit;
        var tether = new Pen(Solid(core, 0.28), 1) { DashStyle = new DashStyle([1, 2], 0) };
        return _groundKitCache[core] = (tether, Solid(core, 0.16), SolidPen(core, 0.32, 1));
    }

    private (IBrush Fill, Pen Stroke) CoreKit(Color core, bool offline)
    {
        var key = (core, offline);
        if (_coreKitCache.TryGetValue(key, out var kit)) return kit;
        var fill = Solid(Lerp(core, Colors.White, offline ? 0.15 : 0.55));
        var stroke = SolidPen(Lerp(core, Colors.White, 0.3), offline ? 0.35 : 0.9, 1);
        return _coreKitCache[key] = (fill, stroke);
    }

    private void DrawCentered(DrawingContext ctx, string text, Color color, double size)
    {
        var t = Fmt(text, color, size, false);
        ctx.DrawText(t, new Point(Bounds.Width / 2 - t.Width / 2, Bounds.Height / 2 - t.Height / 2));
    }
}
