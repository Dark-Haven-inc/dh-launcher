using Avalonia.Media;

namespace DarkHaven.App.Themes;

/// <summary>A color in OKLCH: perceptual lightness 0–1, chroma, hue in degrees.</summary>
public readonly record struct Oklch(double L, double C, double H);

/// <summary>
/// The color math behind the player's theme colors, in the spirit of Material You: a color keeps its
/// hue and chroma and only its lightness moves, until it meets what its role needs against the
/// background — contrast for lines and text (WCAG ratio), a visible difference for filled areas.
/// Lightness and hue are OKLCH's (close to Material's HCT, and simple to compute); when a lightness
/// can't hold the chroma inside sRGB, the chroma gives way.
/// </summary>
public static class ColorMath
{
    /// <summary>WCAG contrast ratio, 1–21.</summary>
    public static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>How different two colors look, hue included (Euclidean distance in OKLab; ~0.02 is barely visible).</summary>
    public static double Difference(Color a, Color b)
    {
        var (l1, a1, b1) = ToOklab(a);
        var (l2, a2, b2) = ToOklab(b);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }

    /// <summary>Whether text and lines read better in light on this background than in dark.</summary>
    public static bool PrefersLight(Color background) =>
        Contrast(background, Colors.White) >= Contrast(background, Colors.Black);

    /// <summary>
    /// <paramref name="color"/> if it contrasts with <paramref name="background"/> by at least
    /// <paramref name="ratio"/>; otherwise lighter or darker, just enough. It moves away from the
    /// background — lighter on a dark one — and the other way only if that side can't get there.
    /// </summary>
    public static Color Readable(Color color, Color background, double ratio) =>
        Separate(color, background, c => Contrast(c, background) >= ratio, c => Contrast(c, background));

    /// <summary>
    /// <paramref name="color"/> if it differs from <paramref name="background"/> by at least
    /// <paramref name="difference"/> (hue counts: blue on gray stands out at any lightness);
    /// otherwise its lightness moves away from the background until it does.
    /// </summary>
    public static Color Distinct(Color color, Color background, double difference) =>
        Separate(color, background, c => Difference(c, background) >= difference, c => Difference(c, background));

    /// <summary>
    /// The color, with its chroma and hue, at the lightness where it contrasts with
    /// <paramref name="background"/> by <paramref name="ratio"/> — on the side the background wants
    /// text on (the nearest it gets, when no lightness is enough).
    /// </summary>
    public static Color AtContrast(Oklch color, Color background, double ratio, byte alpha = 255)
    {
        var bg = ToOklch(background);
        var toward = PrefersLight(background) ? 1.0 : 0.0;
        var l = Search(bg.L, toward, l => Contrast(FromOklch(color with { L = l }), background) >= ratio) ?? toward;
        return FromOklch(color with { L = l }, alpha);
    }

    /// <summary>The color's lightness moved by <paramref name="amount"/> (OKLCH L), away from the
    /// background if positive; toward it the other way when the first has no room.</summary>
    public static Color Nudge(Color color, Color background, double amount)
    {
        var c = ToOklch(color);
        var away = ToOklch(background).L <= c.L ? 1 : -1;
        var l = Math.Clamp(c.L + away * amount, 0, 1);
        if (Math.Abs(l - c.L) < Math.Abs(amount) / 2)
            l = Math.Clamp(c.L - away * amount, 0, 1);
        return FromOklch(c with { L = l }, color.A);
    }

    private static Color Separate(Color color, Color background, Func<Color, bool> ok, Func<Color, double> score)
    {
        if (ok(color))
            return color;

        var c = ToOklch(color);
        var bg = ToOklch(background);
        // Past the background's own lightness, the further the lightness goes the more it stands out.
        Color At(double l) => FromOklch(c with { L = l }, color.A);
        double? Toward(double end) => Search(end > bg.L ? Math.Max(c.L, bg.L) : Math.Min(c.L, bg.L), end, l => ok(At(l)));

        double first = PrefersLight(background) ? 1 : 0, second = 1 - first;
        if ((Toward(first) ?? Toward(second)) is { } found)
            return At(found);
        return score(At(first)) >= score(At(second)) ? At(first) : At(second);
    }

    /// <summary>The point nearest <paramref name="from"/> on the way to <paramref name="to"/> where
    /// <paramref name="ok"/> starts to hold (it must keep holding from there on), or null if it never does.</summary>
    private static double? Search(double from, double to, Func<double, bool> ok)
    {
        if (ok(from))
            return from;
        if (!ok(to))
            return null;
        for (var i = 0; i < 24; i++)
        {
            var mid = (from + to) / 2;
            if (ok(mid))
                to = mid;
            else
                from = mid;
        }
        return to;
    }

    // --- conversions ---

    public static Oklch ToOklch(Color color)
    {
        var (l, a, b) = ToOklab(color);
        var h = Math.Atan2(b, a) * 180 / Math.PI;
        return new Oklch(l, Math.Sqrt(a * a + b * b), h < 0 ? h + 360 : h);
    }

    /// <summary>Back to sRGB; a chroma sRGB can't show at that lightness is reduced until it fits.</summary>
    public static Color FromOklch(Oklch c, byte alpha = 255)
    {
        var rgb = Linear(c.L, c.C, c.H);
        if (!InGamut(rgb))
        {
            double lo = 0, hi = c.C;
            for (var i = 0; i < 20; i++)
            {
                var mid = (lo + hi) / 2;
                if (InGamut(Linear(c.L, mid, c.H)))
                    lo = mid;
                else
                    hi = mid;
            }
            rgb = Linear(c.L, lo, c.H);
        }
        return Color.FromArgb(alpha, Encode(rgb.R), Encode(rgb.G), Encode(rgb.B));
    }

    private static (double L, double A, double B) ToOklab(Color color)
    {
        double r = Decode(color.R), g = Decode(color.G), b = Decode(color.B);
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    private static (double R, double G, double B) Linear(double lightness, double chroma, double hue)
    {
        var a = chroma * Math.Cos(hue * Math.PI / 180);
        var b = chroma * Math.Sin(hue * Math.PI / 180);
        var l = Math.Pow(lightness + 0.3963377774 * a + 0.2158037573 * b, 3);
        var m = Math.Pow(lightness - 0.1055613458 * a - 0.0638541728 * b, 3);
        var s = Math.Pow(lightness - 0.0894841775 * a - 1.2914855480 * b, 3);
        return (4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
                -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
                -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }

    private static bool InGamut((double R, double G, double B) c) =>
        c.R is >= -1e-4 and <= 1 + 1e-4 && c.G is >= -1e-4 and <= 1 + 1e-4 && c.B is >= -1e-4 and <= 1 + 1e-4;

    private static double Luminance(Color c) => 0.2126 * Decode(c.R) + 0.7152 * Decode(c.G) + 0.0722 * Decode(c.B);

    private static double Decode(byte x)
    {
        var v = x / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static byte Encode(double v)
    {
        v = Math.Clamp(v, 0, 1);
        var s = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
        return (byte)Math.Round(s * 255);
    }
}
