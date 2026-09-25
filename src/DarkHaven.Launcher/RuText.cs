using System.Globalization;

namespace DarkHaven.Launcher;

/// <summary>
/// Russian dates and numbers without ICU. The launcher runs with <c>InvariantGlobalization</c>
/// (Directory.Build.props): there, <c>new CultureInfo("ru-RU")</c> throws, and "MMMM" silently gives
/// English month names. Both happened — the first one quietly broke the whole platform part of the
/// profile screen (member-since, launcher-ban notice) since it was written.
/// </summary>
public static class RuText
{
    private static readonly string[] MonthsGenitive =
        ["января", "февраля", "марта", "апреля", "мая", "июня",
         "июля", "августа", "сентября", "октября", "ноября", "декабря"];

    private static readonly string[] MonthsShort =
        ["янв", "фев", "мар", "апр", "мая", "июн", "июл", "авг", "сен", "окт", "ноя", "дек"];

    /// <summary>"18 сентября 2026".</summary>
    public static string Date(int day, int month, int year) => $"{day} {MonthsGenitive[month - 1]} {year}";
    public static string Date(DateOnly d) => Date(d.Day, d.Month, d.Year);
    public static string Date(DateTime d) => Date(d.Day, d.Month, d.Year);
    public static string Date(DateTimeOffset d) => Date(d.Day, d.Month, d.Year);

    /// <summary>"18 сен 2026" — for dense lists.</summary>
    public static string ShortDate(DateTimeOffset d) => $"{d.Day} {MonthsShort[d.Month - 1]} {d.Year}";

    /// <summary>"18 сен, 21:05" — a moment within the last month, where the year goes without saying.</summary>
    public static string DayTime(DateTimeOffset d) =>
        $"{d.Day} {MonthsShort[d.Month - 1]}, {d.ToString("HH:mm", CultureInfo.InvariantCulture)}";

    /// <summary>"18 сен".</summary>
    public static string DayMonth(DateTimeOffset d) => $"{d.Day} {MonthsShort[d.Month - 1]}";

    /// <summary>"18 сен 2026 21:05".</summary>
    public static string ShortDateTime(DateTimeOffset d) =>
        $"{ShortDate(d)} {d.ToString("HH:mm", CultureInfo.InvariantCulture)}";

    /// <summary>Thousands grouped with a non-breaking space, the Russian way: "45 000".</summary>
    public static string Number(long n) =>
        n.ToString("N0", CultureInfo.InvariantCulture).Replace(',', ' ');

    /// <summary>"3 дня", "2 часа", "1 месяц" — a length of time, rounded to its biggest unit.</summary>
    public static string Span(TimeSpan span)
    {
        if (span.TotalDays >= 365) return Plural((int)Math.Round(span.TotalDays / 365), "год", "года", "лет");
        if (span.TotalDays >= 30) return Plural((int)Math.Round(span.TotalDays / 30), "месяц", "месяца", "месяцев");
        if (span.TotalDays >= 1) return Plural((int)Math.Round(span.TotalDays), "день", "дня", "дней");
        if (span.TotalHours >= 1) return Plural((int)Math.Round(span.TotalHours), "час", "часа", "часов");
        return Plural(Math.Max(1, (int)Math.Round(span.TotalMinutes)), "минута", "минуты", "минут");
    }

    /// <summary>"1 день", "3 дня", "5 дней", "11 дней", "21 день".</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        var mod100 = n % 100;
        var mod10 = n % 10;
        var word = mod100 is >= 11 and <= 14 ? many : mod10 == 1 ? one : mod10 is >= 2 and <= 4 ? few : many;
        return $"{n} {word}";
    }
}
