using System.Globalization;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public class RuTextTests
{
    [Fact]
    public void Runs_where_the_app_runs_without_ICU()
    {
        // The whole repo builds with InvariantGlobalization, tests included — this is the condition
        // under which new CultureInfo("ru-RU") threw and broke the profile screen.
        Assert.Throws<CultureNotFoundException>(() => new CultureInfo("ru-RU"));
    }

    [Fact]
    public void Dates_use_russian_month_names()
    {
        Assert.Equal("18 сентября 2026", RuText.Date(new DateTimeOffset(2026, 9, 18, 21, 5, 0, TimeSpan.Zero)));
        Assert.Equal("1 мая 2026", RuText.Date(new DateOnly(2026, 5, 1)));
        Assert.Equal("31 дек 2025 23:59", RuText.ShortDateTime(new DateTimeOffset(2025, 12, 31, 23, 59, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(45000, "45 000")]
    [InlineData(-1234567, "-1 234 567")]
    public void Numbers_group_thousands_with_a_space(long n, string expected) =>
        Assert.Equal(expected, RuText.Number(n));
}
