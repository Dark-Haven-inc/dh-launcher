using System.Globalization;
using Avalonia.Data.Converters;

namespace DarkHaven.App.Themes;

/// <summary>
/// Pieces of a text for the themes' own pages: the first letter alone (the medieval theme's drop caps,
/// <see cref="FirstLetter"/>), the rest after it (<see cref="AfterFirstLetter"/>), and the whole in
/// capitals (the cyberpunk theme's labels, <see cref="Upper"/>).
/// </summary>
public sealed class TextBits(Func<string, string> pick) : IValueConverter
{
    public static readonly TextBits FirstLetter = new(s => s.Length == 0 ? "" : s[..1].ToUpper(CultureInfo.CurrentCulture));

    public static readonly TextBits AfterFirstLetter = new(s => s.Length <= 1 ? "" : s[1..]);

    public static readonly TextBits Upper = new(s => s.ToUpper(CultureInfo.CurrentCulture));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? pick(s.TrimStart()) : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
