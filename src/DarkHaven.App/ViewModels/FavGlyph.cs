using System.Globalization;
using Avalonia.Data.Converters;

namespace DarkHaven.App.ViewModels;

/// <summary>Bool → a filled or hollow star, for the favourite toggle.</summary>
public sealed class FavGlyph(string on, string off) : IValueConverter
{
    /// <summary>Nerd Font codicons star_full / star_empty: show it in DhMono.</summary>
    public static readonly FavGlyph Instance = new("", "");

    /// <summary>Plain ★ / ☆, for the legacy views' text fonts.</summary>
    public static readonly FavGlyph Stars = new("★", "☆");

    /// <summary>"*" / "·", for the retro views' text mode.</summary>
    public static readonly FavGlyph Ascii = new("*", "·");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? on : off;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
