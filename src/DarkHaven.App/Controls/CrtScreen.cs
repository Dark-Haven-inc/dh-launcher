using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;
using DarkHaven.App.Themes;

namespace DarkHaven.App.Controls;

/// <summary>
/// The retro theme's picture tube, laid over the window's content, after cool-retro-term: the phosphor's
/// glow (a tight one on the characters and a wide halo), its light lifting the dark of the screen,
/// scanlines, darkened edges, grain, a brighter band rolling down as the picture drifts out of sync, a
/// faint flicker if the player wants one, and the sweep of a CRT switching on. It only draws — the pointer
/// goes straight through to the content. Inert unless <see cref="IsOn"/> (the retro window,
/// Views/Retro/MainWindow.axaml, turns it on); which effects run is <see cref="RetroEffects"/>. The moving
/// ones (grain, band, flicker) stand still while the window isn't the active one, so a launcher left
/// in the background, or behind the game, redraws nothing.
/// <para>The glow is the content drawn once more (a VisualBrush), and a VisualBrush holds what the content
/// looked like when it was drawn: left alone, the glow would keep showing a page the player has left. So it
/// is redrawn after every layout pass and every bit of input (at most 20 times a second), and four times a
/// second while the window is active, for what changes on its own (the map's animation).</para>
/// </summary>
public sealed class CrtScreen : Decorator
{
    public static readonly StyledProperty<bool> IsOnProperty =
        AvaloniaProperty.Register<CrtScreen, bool>(nameof(IsOn));

    public bool IsOn
    {
        get => GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    // Kept light enough that dark text on a bright (reverse video) bar stays readable.
    private readonly GlowLayer _halo = new(radius: 14, opacity: 0.3) { ZIndex = 1 };
    private readonly GlowLayer _glow = new(radius: 2, opacity: 0.25) { ZIndex = 2 };
    private readonly TubeLayer _tube = new() { ZIndex = 3 };
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _glowSoon;
    private readonly DispatcherTimer _glowTick;
    private TopLevel? _top;

    public CrtScreen()
    {
        VisualChildren.Add(_halo);
        VisualChildren.Add(_glow);
        VisualChildren.Add(_tube);
        _clock = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => OnClock());
        _glowSoon = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => RedrawGlow());
        _glowTick = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            if (IsActiveWindow)
                RedrawGlow();
        });
    }

    private bool IsActiveWindow => _top is Window { IsActive: true, WindowState: not WindowState.Minimized };

    private void RedrawGlow()
    {
        _glowSoon.Stop();
        _glow.InvalidateVisual();
        _halo.InvalidateVisual();
    }

    private void GlowSoon()
    {
        if (_glow.IsVisible && !_glowSoon.IsEnabled)
            _glowSoon.Start();
    }

    private void OnInput(object? sender, RoutedEventArgs e) => GlowSoon();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChildProperty)
            _halo.Source = _glow.Source = Child;
        else if (change.Property == IsOnProperty)
            Refresh(switchOn: true);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RetroEffects.Changed += OnEffectsChanged;
        _top = TopLevel.GetTopLevel(this);
        if (_top is not null)
        {
            _top.LayoutUpdated += OnLayoutUpdated;
            const RoutingStrategies all = RoutingStrategies.Tunnel | RoutingStrategies.Bubble;
            _top.AddHandler(PointerMovedEvent, OnInput, all, handledEventsToo: true);
            _top.AddHandler(PointerPressedEvent, OnInput, all, handledEventsToo: true);
            _top.AddHandler(PointerReleasedEvent, OnInput, all, handledEventsToo: true);
            _top.AddHandler(PointerExitedEvent, OnInput, all, handledEventsToo: true);
            _top.AddHandler(PointerWheelChangedEvent, OnInput, all, handledEventsToo: true);
            _top.AddHandler(KeyDownEvent, OnInput, all, handledEventsToo: true);
            _top.AddHandler(KeyUpEvent, OnInput, all, handledEventsToo: true);
        }
        Refresh(switchOn: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        RetroEffects.Changed -= OnEffectsChanged;
        if (_top is not null)
        {
            _top.LayoutUpdated -= OnLayoutUpdated;
            _top.RemoveHandler(PointerMovedEvent, OnInput);
            _top.RemoveHandler(PointerPressedEvent, OnInput);
            _top.RemoveHandler(PointerReleasedEvent, OnInput);
            _top.RemoveHandler(PointerExitedEvent, OnInput);
            _top.RemoveHandler(PointerWheelChangedEvent, OnInput);
            _top.RemoveHandler(KeyDownEvent, OnInput);
            _top.RemoveHandler(KeyUpEvent, OnInput);
            _top = null;
        }
        _clock.Stop();
        _glowSoon.Stop();
        _glowTick.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => GlowSoon();

    private void OnEffectsChanged() => Refresh(switchOn: false);

    private void Refresh(bool switchOn)
    {
        var on = IsOn && TopLevel.GetTopLevel(this) is not null;
        _glow.IsVisible = _halo.IsVisible = on && RetroEffects.Glow;
        _tube.IsVisible = on;
        _tube.Scanlines = RetroEffects.Scanlines;
        _tube.Noise = RetroEffects.Noise;
        _tube.Band = RetroEffects.Band;
        _tube.Flickers = RetroEffects.Flicker;
        if (this.TryFindResource("DhText", out var ink) && ink is Color color)
            _tube.Ink = color;

        if (on && (RetroEffects.Noise || RetroEffects.Band || RetroEffects.Flicker))
            _clock.Start();
        else
            _clock.Stop();

        if (on && RetroEffects.Glow)
        {
            _glowTick.Start();
            RedrawGlow();
        }
        else
        {
            _glowTick.Stop();
        }

        if (on && switchOn)
            _tube.SwitchOn();
        _tube.InvalidateVisual();
    }

    private void OnClock()
    {
        if (IsActiveWindow)
            _tube.Advance(_clock.Interval);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _halo.Measure(availableSize);
        _glow.Measure(availableSize);
        _tube.Measure(availableSize);
        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        _halo.Arrange(new Rect(finalSize));
        _glow.Arrange(new Rect(finalSize));
        _tube.Arrange(new Rect(finalSize));
        return size;
    }

    /// <summary>The content once more, blurred and screened over itself: bright text bleeds into the dark around it.</summary>
    private sealed class GlowLayer : Control
    {
        public Visual? Source;

        public GlowLayer(double radius, double opacity)
        {
            IsHitTestVisible = false;
            Opacity = opacity;
            Effect = new BlurEffect { Radius = radius };
            RenderOptions.SetBitmapBlendingMode(this, BitmapBlendingMode.Screen);
        }

        public override void Render(DrawingContext context)
        {
            if (Source is null)
                return;
            var brush = new VisualBrush(Source) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            context.FillRectangle(brush, new Rect(Bounds.Size));
        }
    }

    /// <summary>Phosphor light, scanlines, vignette, grain, the rolling band, flicker and the switch-on sweep.</summary>
    private sealed class TubeLayer : Control
    {
        private static readonly TimeSpan SweepTime = TimeSpan.FromMilliseconds(450);
        private static readonly TimeSpan BandTime = TimeSpan.FromSeconds(9);
        private const int NoiseSize = 128;

        // Every third row of device pixels darkened, as the gaps between a CRT's scanlines.
        private static readonly IBrush Line = new ImmutableSolidColorBrush(Color.FromArgb(0x4D, 0, 0, 0));
        private static readonly IBrush Vignette = new RadialGradientBrush
        {
            Center = RelativePoint.Center,
            GradientOrigin = RelativePoint.Center,
            RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
            GradientStops =
            {
                // Transparent black: Colors.Transparent is transparent white, and would fade through gray.
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.55),
                new GradientStop(Color.FromArgb(0x73, 0, 0, 0), 1),
            },
        }.ToImmutable();

        public bool Scanlines = true;
        public bool Noise = true;
        public bool Band = true;
        public bool Flickers;

        private Color _ink = Colors.White;
        private IBrush? _light, _band;
        private WriteableBitmap? _grain;

        public Color Ink
        {
            get => _ink;
            set
            {
                if (value == _ink && _light is not null)
                    return;
                _ink = value;
                _light = PhosphorLight(value);
                _band = BandBrush(value);
                _grain?.Dispose();
                _grain = Grain(value);
            }
        }

        private double _dim;
        private double _bandAt = 0.3;
        private Vector _grainAt;
        private double _sweep = 1;
        private TimeSpan? _sweepStart;

        public TubeLayer()
        {
            IsHitTestVisible = false;
            RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        }

        /// <summary>One tick of the moving effects.</summary>
        public void Advance(TimeSpan step)
        {
            _bandAt = (_bandAt + step / BandTime) % 1;
            _grainAt = new Vector(Random.Shared.Next(NoiseSize), Random.Shared.Next(NoiseSize));
            _dim = Flickers ? Random.Shared.NextDouble() * 0.035 : 0;
            InvalidateVisual();
        }

        /// <summary>Plays the sweep of the tube coming on: a dot, a line across, then the picture.</summary>
        public void SwitchOn()
        {
            if (TopLevel.GetTopLevel(this) is not { } top)
                return;
            _sweep = 0;
            _sweepStart = null;
            top.RequestAnimationFrame(Tick);
        }

        private void Tick(TimeSpan now)
        {
            _sweepStart ??= now;
            _sweep = Math.Min(1, (now - _sweepStart.Value) / SweepTime);
            InvalidateVisual();
            if (_sweep < 1 && TopLevel.GetTopLevel(this) is { } top)
                top.RequestAnimationFrame(Tick);
            else
                _sweep = 1;
        }

        public override void Render(DrawingContext context)
        {
            var size = Bounds.Size;
            var all = new Rect(size);

            // The phosphor's own light: the screen's dark is lit a little green, most in the middle.
            if (_light is not null)
                context.FillRectangle(_light, all);

            if (Band && _band is not null)
            {
                var height = size.Height * 0.06;
                context.FillRectangle(_band, new Rect(0, _bandAt * (size.Height + height) - height, size.Width, height));
            }

            if (Noise && _grain is not null)
            {
                var grain = new ImageBrush(_grain)
                {
                    TileMode = TileMode.Tile,
                    Stretch = Stretch.None,
                    DestinationRect = new RelativeRect(-_grainAt.X, -_grainAt.Y, NoiseSize, NoiseSize, RelativeUnit.Absolute),
                };
                context.FillRectangle(grain, all);
            }

            if (Scanlines)
            {
                var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
                var rows = (int)Math.Ceiling(size.Height * scale);
                for (var row = 2; row < rows; row += 3)
                    context.FillRectangle(Line, new Rect(0, row / scale, size.Width, 1 / scale));
            }

            context.FillRectangle(Vignette, all);

            if (_dim > 0)
                context.FillRectangle(new ImmutableSolidColorBrush(Color.FromArgb((byte)(_dim * 255), 0, 0, 0)), all);

            if (_sweep < 1)
                DrawSweep(context, size);
        }

        private void DrawSweep(DrawingContext context, Size size)
        {
            var ink = new ImmutableSolidColorBrush(_ink);
            var middle = size.Height / 2;
            if (_sweep < 0.3)
            {
                var width = size.Width * Ease(_sweep / 0.3);
                context.FillRectangle(Brushes.Black, new Rect(size));
                context.FillRectangle(ink, new Rect((size.Width - width) / 2, middle - 1, width, 2));
                return;
            }

            // The line opens up into the picture, which starts overexposed and settles.
            var t = (_sweep - 0.3) / 0.7;
            var height = Math.Max(2, size.Height * Ease(t));
            var top = middle - height / 2;
            context.FillRectangle(Brushes.Black, new Rect(0, 0, size.Width, top));
            context.FillRectangle(Brushes.Black, new Rect(0, top + height, size.Width, size.Height - top - height));
            using (context.PushOpacity(0.6 * (1 - t)))
                context.FillRectangle(ink, new Rect(0, top, size.Width, height));
        }

        private static double Ease(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

        private static IBrush PhosphorLight(Color ink) => new RadialGradientBrush
        {
            Center = RelativePoint.Center,
            GradientOrigin = RelativePoint.Center,
            RadiusX = new RelativeScalar(0.7, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.7, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x14, ink.R, ink.G, ink.B), 0),
                new GradientStop(Color.FromArgb(0, ink.R, ink.G, ink.B), 1),
            },
        }.ToImmutable();

        // A faint strip with short edges; transparent stops keep the ink's own color.
        private static IBrush BandBrush(Color ink) => new LinearGradientBrush
        {
            StartPoint = RelativePoint.TopLeft,
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, ink.R, ink.G, ink.B), 0),
                new GradientStop(Color.FromArgb(0x0A, ink.R, ink.G, ink.B), 0.15),
                new GradientStop(Color.FromArgb(0x0A, ink.R, ink.G, ink.B), 0.85),
                new GradientStop(Color.FromArgb(0, ink.R, ink.G, ink.B), 1),
            },
        }.ToImmutable();

        /// <summary>A tile of phosphor grain: most pixels dark, a few lit faintly.</summary>
        private static WriteableBitmap Grain(Color ink)
        {
            var bitmap = new WriteableBitmap(new PixelSize(NoiseSize, NoiseSize), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var buffer = bitmap.Lock();
            var random = new Random(15);
            var row = new int[NoiseSize];
            for (var y = 0; y < NoiseSize; y++)
            {
                for (var x = 0; x < NoiseSize; x++)
                {
                    var v = random.NextDouble();
                    var a = (int)(v * v * v * v * 24); // mostly nothing, now and then a faint speck
                    row[x] = (a << 24) | (ink.R * a / 255 << 16) | (ink.G * a / 255 << 8) | (ink.B * a / 255);
                }
                Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, NoiseSize);
            }
            return bitmap;
        }
    }
}
