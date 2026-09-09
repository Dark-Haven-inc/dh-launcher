using System.Globalization;
using Avalonia.Data.Converters;

namespace DarkHaven.App.ViewModels;

/// <summary>Bool → a filled or hollow star, for the favourite toggle.</summary>
public sealed class FavGlyph : IValueConverter
{
    public static readonly FavGlyph Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "★" : "☆";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
