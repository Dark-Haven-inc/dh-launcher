using System.Globalization;
using Avalonia.Data.Converters;

namespace DarkHaven.App.ViewModels;

/// <summary>Bool → "  *" for a pinned news item, in the retro views' text (monochrome shows an icon).</summary>
public sealed class PinMark : IValueConverter
{
    public static readonly PinMark Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "  *" : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
