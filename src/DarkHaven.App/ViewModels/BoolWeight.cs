using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DarkHaven.App.ViewModels;

/// <summary>Bool → medium weight (true) or regular, e.g. to mark the active account.</summary>
public sealed class BoolWeight : IValueConverter
{
    public static readonly BoolWeight Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? FontWeight.Medium : FontWeight.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
