using System.Globalization;
using Avalonia.Data.Converters;

namespace DarkHaven.App.ViewModels;

/// <summary>Bool → "&gt;" before the account in use, in the retro views' text (monochrome sets it in bold).</summary>
public sealed class ActiveMark : IValueConverter
{
    public static readonly ActiveMark Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? ">" : " ";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
