using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DarkHaven.App.Themes;
using DarkHaven.Launcher;

namespace DarkHaven.App.ViewModels;

/// <summary>How a profile's colour and frame turn into brushes — shared by every card.</summary>
public static class ProfileLook
{
    public const string DefaultAccent = "#3A3A3E";
    public static readonly string[] Frames = ["none", "blue", "gold", "cyan"];
    private static readonly Regex Hex = new("^#[0-9A-Fa-f]{6}$");

    public static bool IsHex(string? s) => s is not null && Hex.IsMatch(s);

    public static Color Accent(string? hex) => Color.Parse(IsHex(hex) ? hex! : DefaultAccent);

    private static bool Legacy => Theme.Current.Layout == ThemeLayout.Legacy;

    /// <summary>
    /// The banner when there's no picture: the profile colour, flat and muted, so a bright pick
    /// doesn't shout over the monochrome launcher. The legacy design fades it into a darker shade
    /// of itself instead.
    /// </summary>
    public static IBrush BannerBrush(string? hex)
    {
        var c = Accent(hex);
        // The medieval sheet wants the colour as a wash of ink on parchment, not a dark slab.
        if (Theme.Current.Layout == ThemeLayout.Medieval)
            return new SolidColorBrush(Mix(c, Color.Parse("#B89A6A"), 0.4));
        if (!Legacy)
            return new SolidColorBrush(Color.FromRgb((byte)(c.R * 0.45), (byte)(c.G * 0.45), (byte)(c.B * 0.45)));

        var deep = Color.FromRgb((byte)(c.R * 0.3), (byte)(c.G * 0.3), (byte)(c.B * 0.3));
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(c, 0), new GradientStop(deep, 1) },
        };
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R * t + b.R * (1 - t)), (byte)(a.G * t + b.G * (1 - t)), (byte)(a.B * t + b.B * (1 - t)));

    public static FrameBrushes Frame(string? frame) => frame switch
    {
        "gold" => new(Color.Parse("#F0B454"), Color.Parse("#3A2E12")),
        "cyan" => new(Color.Parse("#7FD4FF"), Color.Parse("#14313A")),
        "none" => new(Color.Parse(Theme.Current.Layout switch
        {
            ThemeLayout.Legacy => "#24365A",
            ThemeLayout.Medieval => "#94784F",
            ThemeLayout.Cyberpunk => "#3B3B66",
            _ => "#323236",
        }), Colors.Transparent),
        "blue" => new(Color.Parse("#5AA0FF"), Color.Parse("#111A2E")),
        _ => new(Color.Parse("#8A8A8F"), Color.Parse("#111113")),
    };
}

/// <summary>XAML: a date as "18 сентября 2026" (StringFormat would give English months — no ICU).</summary>
public sealed class RuDateConverter : IValueConverter
{
    public static readonly RuDateConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTimeOffset d => RuText.Date(d.ToLocalTime()),
        DateTime d => RuText.Date(d.ToLocalTime()),
        _ => "—",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>XAML: seconds as "12 ч 5 мин".</summary>
public sealed class DurationConverter : IValueConverter
{
    public static readonly DurationConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long or int ? ProfileViewModel.FormatDuration(System.Convert.ToInt64(value)) : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
