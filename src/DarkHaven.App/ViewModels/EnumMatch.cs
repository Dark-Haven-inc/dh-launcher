using System.Globalization;
using Avalonia.Data.Converters;

namespace DarkHaven.App.ViewModels;

/// <summary>Binding converter: true when the bound enum value equals the ConverterParameter.</summary>
public sealed class EnumMatch : IValueConverter
{
    public static readonly EnumMatch Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && value.Equals(parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
