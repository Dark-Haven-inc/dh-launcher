using System.Globalization;
using System.Text;
using Avalonia.Data.Converters;

namespace DarkHaven.App.Themes;

/// <summary>
/// The retro theme writes out what monochrome shows as an icon: a button whose content is only Nerd Font
/// glyphs (private-use characters) gets its tooltip as its label instead — "обновить", not ↻. Anything
/// else, and an icon without a tooltip, stays as it is. Values: the button's Content, its ToolTip.Tip.
/// </summary>
public sealed class RetroLabel : IMultiValueConverter
{
    public static readonly RetroLabel Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var content = values.Count > 0 ? values[0] : null;
        return content is string text && IsGlyphs(text) && values.Count > 1 && values[1] is string { Length: > 0 } tip
            ? tip
            : content;
    }

    private static bool IsGlyphs(string text)
    {
        var any = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
                continue;
            if (rune.Value is not (>= 0xE000 and <= 0xF8FF or >= 0xF0000))
                return false;
            any = true;
        }
        return any;
    }
}
