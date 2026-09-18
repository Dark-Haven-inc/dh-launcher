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

    /// <summary>"18 сен 2026 21:05".</summary>
    public static string ShortDateTime(DateTimeOffset d) =>
        $"{ShortDate(d)} {d.ToString("HH:mm", CultureInfo.InvariantCulture)}";

    /// <summary>Thousands grouped with a non-breaking space, the Russian way: "45 000".</summary>
    public static string Number(long n) =>
        n.ToString("N0", CultureInfo.InvariantCulture).Replace(',', ' ');
}
