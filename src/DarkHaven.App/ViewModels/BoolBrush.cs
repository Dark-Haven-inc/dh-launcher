using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DarkHaven.App.ViewModels;

/// <summary>Bool → one of two brushes.</summary>
public sealed class BoolBrush(IBrush ifTrue, IBrush ifFalse) : IValueConverter
{
    // Monochrome: state is carried by StatusGlyph shapes; these only pick a gray.
    public static readonly BoolBrush OnlineOffline =
        new(new SolidColorBrush(Color.Parse("#EDEDED")), new SolidColorBrush(Color.Parse("#7A7A80")));

    /// <summary>true (pending) → dim, false → normal text.</summary>
    public static readonly BoolBrush DimText =
        new(new SolidColorBrush(Color.Parse("#8A8A8F")), new SolidColorBrush(Color.Parse("#EDEDED")));

    /// <summary>true (selected / live) → normal text, false → dim.</summary>
    public static readonly BoolBrush AccentText =
        new(new SolidColorBrush(Color.Parse("#EDEDED")), new SolidColorBrush(Color.Parse("#8A8A8F")));

    /// <summary>Same as <see cref="AccentText"/>, named for what it does now.</summary>
    public static readonly BoolBrush TextOrDim = AccentText;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? ifTrue : ifFalse;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
