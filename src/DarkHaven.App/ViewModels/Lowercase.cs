using System.Globalization;
using Avalonia.Data.Converters;

namespace DarkHaven.App.ViewModels;

/// <summary>
/// The monochrome layout writes the launcher's own text in lowercase: this lowers the first letter of
/// each sentence of a view-model string. Only an ordinary Russian capital is lowered — «ИГРОК», «DH»,
/// Discord and Frontier 15 stay as they are. Not for names, nicknames or anything else people typed.
/// </summary>
public sealed class Lowercase : IValueConverter
{
    public static readonly Lowercase Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s ? Sentences(s) : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    public static string Sentences(string s)
    {
        var chars = s.ToCharArray();
        var start = true;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (start && IsRuUpper(c))
            {
                var next = i + 1 < chars.Length ? chars[i + 1] : ' ';
                // "Карта" → "карта", and one-letter words: "У игрока" → "у игрока", but not "У ДРУЗЕЙ".
                if (IsRuLower(next) || (next == ' ' && "УВСКОИАЯ".Contains(c) && !NextWordIsCaps(chars, i + 2)))
                    chars[i] = char.ToLowerInvariant(c);
            }
            if (c is '.' or '?' or '!')
                start = true;
            else if (c != ' ')
                start = false;
        }
        return new string(chars);
    }

    private static bool IsRuUpper(char c) => c is >= 'А' and <= 'Я' or 'Ё';
    private static bool IsRuLower(char c) => c is >= 'а' and <= 'я' or 'ё';
    private static bool NextWordIsCaps(char[] chars, int i)
        => i + 1 < chars.Length && IsRuUpper(chars[i]) && IsRuUpper(chars[i + 1]);
}
