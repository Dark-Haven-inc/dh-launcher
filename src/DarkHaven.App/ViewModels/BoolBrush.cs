using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DarkHaven.App.ViewModels;

/// <summary>Bool → one of two palette brushes, looked up by key when converting, so they follow the theme.</summary>
public sealed class BoolBrush(string ifTrue, string ifFalse) : IValueConverter
{
    public static readonly BoolBrush OnlineOffline = new("DhOnlineBrush", "DhOfflineBrush");

    /// <summary>true (pending) → dim, false → normal text.</summary>
    public static readonly BoolBrush DimText = new("DhTextDimBrush", "DhTextBrush");

    /// <summary>true (selected / live) → normal text, false → dim.</summary>
    public static readonly BoolBrush TextOrDim = new("DhTextBrush", "DhTextDimBrush");

    /// <summary>true (selected) → accent, false → normal text.</summary>
    public static readonly BoolBrush AccentText = new("DhAccentBrightBrush", "DhTextBrush");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? ifTrue : ifFalse;
        return Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var brush)
            ? brush as IBrush
            : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
