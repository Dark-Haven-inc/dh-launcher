using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DarkHaven.App.ViewModels;

/// <summary>Bool → one of two brushes.</summary>
public sealed class BoolBrush(IBrush ifTrue, IBrush ifFalse) : IValueConverter
{
    public static readonly BoolBrush OnlineOffline =
        new(new SolidColorBrush(Color.Parse("#4ADE80")), new SolidColorBrush(Color.Parse("#5A6B8A")));

    /// <summary>true (pending) → dim, false → normal text.</summary>
    public static readonly BoolBrush DimText =
        new(new SolidColorBrush(Color.Parse("#7C8DB0")), new SolidColorBrush(Color.Parse("#DCE6F5")));

    /// <summary>true (selected) → accent, false → normal text.</summary>
    public static readonly BoolBrush AccentText =
        new(new SolidColorBrush(Color.Parse("#5AA0FF")), new SolidColorBrush(Color.Parse("#DCE6F5")));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? ifTrue : ifFalse;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
