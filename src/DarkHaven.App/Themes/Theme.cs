using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using DarkHaven.Launcher.Data;

namespace DarkHaven.App.Themes;

/// <summary>A main window with its views (Views/, Views/Legacy/), and the control styles they are
/// drawn with (Themes/Styles/&lt;name&gt;.axaml).</summary>
public enum ThemeLayout { Monochrome, Legacy }

/// <summary>
/// The player's own colors on top of a theme's palette (НАСТРОЙКИ → Вид, next to the theme); null
/// keeps the palette's. Much like Material You, a pick sets the hue, and each color's lightness is
/// then worked out from what it has to stand out against (<see cref="ColorMath"/>):
/// <para><see cref="Base"/> is the background, as picked — unless it is too mid-gray for text to
/// stand out on it, then it moves to the nearer of dark and light (<see cref="Theme.BackgroundFor"/>).
/// Every gray of the palette keeps the contrast it had with the palette's own background — text
/// stays as readable, borders as faint — on whichever side the new one needs (dark text on a light
/// background), and takes its hue: in full next to the background, fading out toward the text.
/// Saturated colors stay as they are.</para>
/// <para><see cref="Active"/> is the accent. Where it fills an area (primary buttons, checked boxes)
/// it only has to look different from the background, hue counts; where it is a 1–2 px line or text
/// (selected chips, the nav marker) it needs 4.5:1 contrast. Either way it moves lighter or darker
/// only as far as that takes.</para>
/// </summary>
public sealed record ThemeColors(HsvColor? Base, HsvColor? Active)
{
    public static readonly ThemeColors None = new(null, null);
}

/// <summary>
/// A look the player picks in НАСТРОЙКИ: a layout painted in a palette (Themes/Palettes/&lt;name&gt;.axaml).
/// Another palette on an existing layout is one file there and one line in <see cref="All"/>.
/// <para>Every palette defines the same keys, since either layout looks them up by name: the colors
/// DhBg, DhBgPanel, DhBgElevated, DhBgHover, DhBgPress, DhBorder, DhBorderStrong, DhBorderBright,
/// DhText, DhBody, DhTextDim, DhTextFaint, DhAccent, DhAccentBright, DhCyan, DhOnline, DhWarn,
/// DhDanger, DhOffline, DhFrontierSilver, DhFrontierRed, DhActive, DhActiveHover, DhActivePress,
/// DhOnActive, DhActiveLine, DhScrim, DhMapGrid, DhMapLink, and a SolidColorBrush "&lt;key&gt;Brush" for each but the
/// last two. SystemAccentColor* are optional.</para>
/// </summary>
public sealed record Theme(string Id, string Title, ThemeLayout Layout, string Palette)
{
    public static readonly IReadOnlyList<Theme> All =
    [
        new("monochrome", "Monochrome", ThemeLayout.Monochrome, "Monochrome"),
        new("legacy", "Legacy", ThemeLayout.Legacy, "Legacy"),
    ];

    public static Theme Default => All[0];

    /// <summary>The one applied last.</summary>
    public static Theme Current { get; private set; } = Default;

    /// <summary>The player's colors on <see cref="Current"/>.</summary>
    public static ThemeColors CurrentColors { get; private set; } = ThemeColors.None;

    /// <summary>The current palette's own background and active color — where the color editor
    /// starts when the player has none of their own.</summary>
    public static HsvColor PaletteBase { get; private set; }
    public static HsvColor PaletteActive { get; private set; }

    /// <summary>The background as it is now, in the player's colors.</summary>
    public static Color CurrentBackground { get; private set; }

    /// <summary>How different from the background an accent-filled area must look (OKLab distance):
    /// a clearly different hue is enough, a dark blue on a near-black gray is not.</summary>
    private const double AreaDifference = 0.2;

    /// <summary>Contrast for accent lines and text — WCAG's for text, which 1 px lines need as much.</summary>
    private const double LineContrast = 4.5;

    /// <summary>How much contrast the background must leave for text (white on a dark one, black on a light one).</summary>
    private const double BackgroundHeadroom = 9;

    /// <summary>Grays up to this contrast keep theirs exactly (dim and faint text, borders); the ones
    /// above, the main text, share whatever the background leaves up to <see cref="BackgroundHeadroom"/>.</summary>
    private const double ExactContrast = 7;

    /// <summary>The saved choice (config "Theme"); an unknown or missing id is the default.</summary>
    public static Theme Find(string? id) => All.FirstOrDefault(t => t.Id == id) ?? Default;

    // The palette as its file has it, and what the application has now: the same, recolored. Both
    // hold the same brush objects — views took those once, through StaticResource, so recoloring
    // changes their Color in place rather than handing out new ones.
    private static ResourceDictionary? _source;
    private static IResourceDictionary? _palette;
    private static IStyle? _styles;

    /// <summary>
    /// Puts the theme's palette, in the player's colors, into the application's resources and its
    /// layout's styles after FluentTheme, replacing the previous theme's. Views resolve StaticResource
    /// once, when they load, so an open window keeps the old look — <see cref="App.SwitchTheme"/>
    /// replaces it.
    /// </summary>
    public static void Apply(Application app, Theme theme, ThemeColors colors)
    {
        // The palette goes in first: the styles look its colors up while they load.
        _source = (ResourceDictionary)AvaloniaXamlLoader.Load(Asset($"Palettes/{theme.Palette}.axaml"));
        PaletteBase = ((Color)_source["DhBg"]!).ToHsv();
        PaletteActive = ((Color)_source["DhActive"]!).ToHsv();
        Recolor(app, colors);

        var styles = (IStyle)AvaloniaXamlLoader.Load(Asset($"Styles/{theme.Layout}.axaml"));
        if (_styles is not null)
            app.Styles.Remove(_styles);
        app.Styles.Add(styles);
        _styles = styles;

        Current = theme;
    }

    /// <summary>Repaints the current palette in the player's colors; everything on screen follows at once.</summary>
    public static void Recolor(Application app, ThemeColors colors)
    {
        if (_source is null)
            return;

        var palette = Recolored(_source, colors);
        var merged = app.Resources.MergedDictionaries;
        var at = _palette is null ? -1 : merged.IndexOf(_palette);
        if (at >= 0)
            merged[at] = palette;
        else
            merged.Add(palette);
        _palette = palette;
        CurrentColors = colors;
        CurrentBackground = (Color)palette["DhBg"]!;

        // Fluent's own parts (drop-down lists, text selection) follow: light ones on a light background.
        app.RequestedThemeVariant = ColorMath.PrefersLight(CurrentBackground) ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    /// <summary>The background a pick gives: as picked, or moved darker or lighter (whichever is
    /// nearer) until text on it can reach <see cref="BackgroundHeadroom"/>.</summary>
    public static Color BackgroundFor(HsvColor pick)
    {
        var c = pick.ToRgb();
        return ColorMath.PrefersLight(c)
            ? ColorMath.Readable(c, Colors.White, BackgroundHeadroom)
            : ColorMath.Readable(c, Colors.Black, BackgroundHeadroom);
    }

    /// <summary>The accent as buttons and checked boxes are filled with it, on the current background.</summary>
    public static Color AccentFill(HsvColor pick) => ColorMath.Distinct(pick.ToRgb(), CurrentBackground, AreaDifference);

    /// <summary>A new main window of the layout; the caller sets its DataContext.</summary>
    public Window CreateMainWindow() => Layout switch
    {
        ThemeLayout.Legacy => new Views.Legacy.MainWindow(),
        _ => new Views.MainWindow(),
    };

    // --- the player's colors, saved per theme as "h,s,v" ---

    private string BaseKey => $"Theme.{Id}.Base";
    private string ActiveKey => $"Theme.{Id}.Active";

    public ThemeColors LoadColors(SettingsDatabase settings) =>
        new(ParseHsv(settings.GetConfig(BaseKey)), ParseHsv(settings.GetConfig(ActiveKey)));

    public void SaveColors(SettingsDatabase settings, ThemeColors colors)
    {
        settings.SetConfig(BaseKey, FormatHsv(colors.Base));
        settings.SetConfig(ActiveKey, FormatHsv(colors.Active));
    }

    private static HsvColor? ParseHsv(string? text) =>
        text?.Split(',') is [var h, var s, var v]
        && double.TryParse(h, NumberStyles.Float, CultureInfo.InvariantCulture, out var hue)
        && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var sat)
        && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var val)
            ? new HsvColor(1, hue, sat, val)
            : null;

    private static string? FormatHsv(HsvColor? c) =>
        c is { } x ? string.Create(CultureInfo.InvariantCulture, $"{x.H:0.#},{x.S:0.###},{x.V:0.###}") : null;

    // --- recoloring ---

    private static ResourceDictionary Recolored(ResourceDictionary source, ThemeColors colors)
    {
        var byKey = new Dictionary<string, Color>();
        foreach (var key in source.Keys.OfType<string>())
            if (source[key] is Color c)
                byKey[key] = c;

        if (colors.Base is { } pick)
        {
            var from = byKey["DhBg"];
            var to = BackgroundFor(pick);
            var fits = Squeeze(Headroom(from), Headroom(to));
            foreach (var key in byKey.Keys.ToList())
                byKey[key] = key == "DhBg" ? to : FollowBase(byKey[key], from, to, fits);
        }

        if (colors.Active is { } active)
            foreach (var (key, c) in ActiveColors(active.ToRgb(), byKey["DhBg"], byKey["DhText"]))
                byKey[key] = c;

        var result = new ResourceDictionary();
        foreach (var key in source.Keys)
        {
            var value = source[key];
            if (key is string k && byKey.TryGetValue(k, out var color))
                value = color;
            else if (value is SolidColorBrush brush && key is string bk && bk.EndsWith("Brush")
                     && byKey.TryGetValue(bk[..^"Brush".Length], out var brushColor))
                brush.Color = brushColor;
            result.Add(key, value);
        }
        return result;
    }

    /// <summary>The most contrast anything can have on this background.</summary>
    private static double Headroom(Color background) =>
        Math.Max(ColorMath.Contrast(background, Colors.White), ColorMath.Contrast(background, Colors.Black));

    /// <summary>
    /// Contrasts on the palette's background (up to <paramref name="had"/>) as they fit on the new one
    /// (up to <paramref name="has"/>): the same up to <see cref="ExactContrast"/>, the rest squeezed in
    /// proportion (on a log scale) — so text stays above dim text instead of both hitting black.
    /// </summary>
    private static Func<double, double> Squeeze(double had, double has)
    {
        if (has >= had)
            return r => r;
        var (knee, top, room) = (Math.Log(ExactContrast), Math.Log(had), Math.Log(has));
        return r => r <= ExactContrast ? r : Math.Exp(knee + (Math.Log(r) - knee) * (room - knee) / (top - knee));
    }

    /// <summary>A gray of the palette, moved from its background <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static Color FollowBase(Color c, Color from, Color to, Func<double, double> fits)
    {
        // Real colors, and see-through ones (the scrim stays a dark veil), are left alone.
        if (c.ToHsv().S > 0.25 || c.A < 255)
            return c;

        var gray = ColorMath.ToOklch(c);
        var (bg0, bg) = (ColorMath.ToOklch(from), ColorMath.ToOklch(to));
        // How far toward white it sat: its tint fades with that, so text stays near neutral.
        var t = bg0.L >= 1 ? 1 : Math.Clamp((gray.L - bg0.L) / (1 - bg0.L), 0, 1);
        var chroma = Math.Max(0, gray.C + (bg.C - bg0.C) * (1 - t));
        return ColorMath.AtContrast(new Oklch(gray.L, chroma, bg.C > 1e-4 ? bg.H : gray.H), to, fits(ColorMath.Contrast(c, from)));
    }

    private static IEnumerable<(string Key, Color Color)> ActiveColors(Color pick, Color background, Color text)
    {
        var fill = ColorMath.Distinct(pick, background, AreaDifference);
        yield return ("DhActive", fill);
        yield return ("DhActiveHover", ColorMath.Nudge(fill, background, 0.05));
        yield return ("DhActivePress", ColorMath.Nudge(fill, background, -0.1));
        yield return ("DhActiveLine", ColorMath.Readable(pick, background, LineContrast));
        // Text on the fill: the background's color or the text's, whichever reads better on it.
        yield return ("DhOnActive", ColorMath.Contrast(fill, background) >= ColorMath.Contrast(fill, text) ? background : text);
    }

    private static Uri Asset(string path) => new($"avares://Frontier15Launcher/Themes/{path}");
}
