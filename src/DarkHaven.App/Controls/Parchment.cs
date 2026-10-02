using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DarkHaven.App.Controls;

/// <summary>
/// The medieval theme's sheet: the background color as old parchment — mottled, with a few tide-marked
/// stains, edges gone darker and uneven, and fibres in the grain. Only the darkening is drawn here, in a
/// brown ink over <see cref="Background"/>, so the player's own background color (НАСТРОЙКИ → Вид) ages
/// the same way. Both layers are made with a fixed seed and shared: the stains once, stretched over the
/// whole window; the grain at its own size, one sheet as large as the window has been (a tiled grain shows
/// its seams under display scaling), made again only when the window outgrows it. It only draws; put it
/// behind the content.
/// </summary>
public sealed class Parchment : Control
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<Parchment>();

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    static Parchment()
    {
        // Brushes recolor in place (Themes/Theme.cs); AffectsRender redraws on that too.
        AffectsRender<Parchment>(BackgroundProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<Parchment>(false);
    }

    private const int SheetWidth = 480, SheetHeight = 300, GrainStep = 256;
    private static readonly Color Ink = Color.Parse("#4A2E12");

    private static WriteableBitmap? _sheet;
    private static WriteableBitmap? _grain;

    public override void Render(DrawingContext context)
    {
        var all = new Rect(Bounds.Size);
        if (Background is { } background)
            context.FillRectangle(background, all);

        _sheet ??= Sheet();
        context.DrawImage(_sheet, new Rect(0, 0, SheetWidth, SheetHeight), all);

        // Grown in steps, so a window being resized makes a new one now and then, not on every pixel.
        var (width, height) = (Step(all.Width), Step(all.Height));
        if (_grain is null || _grain.PixelSize.Width < width || _grain.PixelSize.Height < height)
        {
            _grain?.Dispose();
            _grain = Grain(Math.Max(width, _grain?.PixelSize.Width ?? 0), Math.Max(height, _grain?.PixelSize.Height ?? 0));
        }
        context.DrawImage(_grain, all, all);

        static int Step(double size) => Math.Max(1, (int)Math.Ceiling(size / GrainStep)) * GrainStep;
    }

    /// <summary>The large-scale aging, stretched over the window: soft mottling, two or three stains with a
    /// darker tide line, and edges that darken unevenly (a rounded-rectangle falloff, roughened by noise).</summary>
    private static WriteableBitmap Sheet()
    {
        var mottle = new ValueNoise(seed: 15, cells: 6);
        var stains = new ValueNoise(seed: 1215, cells: 3);
        var rough = new ValueNoise(seed: 777, cells: 14);
        return Paint(SheetWidth, SheetHeight, (x, y) =>
        {
            double u = (x + 0.5) / SheetWidth, v = (y + 0.5) / SheetHeight;

            var edge = Math.Pow(Math.Pow(Math.Abs(u * 2 - 1), 4) + Math.Pow(Math.Abs(v * 2 - 1), 4), 0.25);
            edge += (rough.Fbm(u, v) - 0.5) * 0.22;
            var vignette = SmoothStep(0.62, 1.12, edge) * 0.34;

            var m = mottle.Fbm(u, v) * 0.075;

            var s = stains.Fbm(u, v);
            var stain = SmoothStep(0.64, 0.7, s) * 0.045 + Math.Exp(-Math.Pow((s - 0.7) / 0.01, 2)) * 0.035;

            return vignette + m + stain;
        });
    }

    /// <summary>The grain: faint specks, and short curling fibres here and there.</summary>
    private static WriteableBitmap Grain(int width, int height)
    {
        var random = new Random(1215);
        var alpha = new double[width * height];
        for (var i = 0; i < alpha.Length; i++)
        {
            var r = random.NextDouble();
            alpha[i] = r * r * r * 0.06;
        }

        for (var fibre = width * height / 400; fibre > 0; fibre--)
        {
            double x = random.NextDouble() * width, y = random.NextDouble() * height;
            var angle = random.NextDouble() * Math.PI * 2;
            var length = 4 + random.Next(12);
            var strength = 0.015 + random.NextDouble() * 0.03;
            for (var step = 0; step < length && x >= 0 && y >= 0 && x < width && y < height; step++)
            {
                var at = (int)y * width + (int)x;
                alpha[at] = Math.Min(1, alpha[at] + strength);
                angle += (random.NextDouble() - 0.5) * 0.5;
                x += Math.Cos(angle);
                y += Math.Sin(angle);
            }
        }

        return Paint(width, height, (x, y) => alpha[y * width + x]);
    }

    private static WriteableBitmap Paint(int width, int height, Func<int, int, double> alpha)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        var row = new int[width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var a = (int)(Math.Clamp(alpha(x, y), 0, 1) * 255);
                row[x] = (a << 24) | (Ink.R * a / 255 << 16) | (Ink.G * a / 255 << 8) | (Ink.B * a / 255);
            }
            Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, width);
        }
        return bitmap;
    }

    private static double SmoothStep(double from, double to, double x)
    {
        var t = Math.Clamp((x - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Smoothly interpolated random values on a grid, summed over three octaves (0..1).</summary>
    private sealed class ValueNoise(int seed, int cells)
    {
        private const int Size = 64;
        private readonly double[] _values = Values(seed);

        private static double[] Values(int seed)
        {
            var random = new Random(seed);
            var values = new double[Size * Size];
            for (var i = 0; i < values.Length; i++)
                values[i] = random.NextDouble();
            return values;
        }

        public double Fbm(double u, double v) =>
            (At(u * cells, v * cells) * 4 + At(u * cells * 2 + 17, v * cells * 2 + 31) * 2 + At(u * cells * 4 + 5, v * cells * 4 + 11)) / 7;

        private double At(double x, double y)
        {
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            double fx = Fade(x - x0), fy = Fade(y - y0);
            var top = Lerp(Value(x0, y0), Value(x0 + 1, y0), fx);
            var bottom = Lerp(Value(x0, y0 + 1), Value(x0 + 1, y0 + 1), fx);
            return Lerp(top, bottom, fy);
        }

        private double Value(int x, int y) => _values[(y & (Size - 1)) * Size + (x & (Size - 1))];

        private static double Fade(double t) => t * t * (3 - 2 * t);

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    }
}
