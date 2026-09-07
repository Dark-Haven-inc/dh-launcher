using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DarkHaven.App.ViewModels;

/// <summary>true → online (green) brush, false → offline (muted red) brush.</summary>
public sealed class BoolBrush : IValueConverter
{
    public static readonly BoolBrush OnlineOffline = new();

    private static readonly IBrush Online = new SolidColorBrush(Color.Parse("#7FB069"));
    private static readonly IBrush Offline = new SolidColorBrush(Color.Parse("#8A5A5A"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Online : Offline;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
