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
        Assert.Equal("5 мар, 07:05", RuText.DayTime(new DateTimeOffset(2026, 3, 5, 7, 5, 0, TimeSpan.Zero)));
        Assert.Equal("5 мар", RuText.DayMonth(new DateTimeOffset(2026, 3, 5, 7, 5, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(45000, "45 000")]
    [InlineData(-1234567, "-1 234 567")]
    public void Numbers_group_thousands_with_a_space(long n, string expected) =>
        Assert.Equal(expected, RuText.Number(n));

    [Theory]
    [InlineData(1, "1 день")]
    [InlineData(3, "3 дня")]
    [InlineData(5, "5 дней")]
    [InlineData(11, "11 дней")]
    [InlineData(21, "21 день")]
    [InlineData(22, "22 дня")]
    public void Ban_lengths_in_days_agree_with_the_number(int days, string expected) =>
        Assert.Equal(expected, RuText.Span(TimeSpan.FromDays(days)));

    [Fact]
    public void Ban_lengths_pick_the_biggest_unit()
    {
        Assert.Equal("2 часа", RuText.Span(TimeSpan.FromHours(2)));
        Assert.Equal("1 месяц", RuText.Span(TimeSpan.FromDays(31)));
        Assert.Equal("1 год", RuText.Span(TimeSpan.FromDays(366)));
        Assert.Equal("30 минут", RuText.Span(TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Reads_a_page_of_the_public_ban_list()
    {
        const string json = """
        {"available":true,"total":42,"items":[{"id":7,"isRoleBan":true,"at":"2026-09-22T10:00:00+00:00",
          "expiresAt":"2026-09-29T10:00:00+00:00","reason":"халатность","player":"RoleBanned","admin":"AdminA",
          "liftedAt":null,"liftedBy":null,"roles":"Captain, Security"}]}
        """;
        var page = System.Text.Json.JsonSerializer.Deserialize<DarkHaven.Launcher.Api.PlatformBanListPage>(json, DarkHaven.Launcher.Api.LauncherJson.Options)!;
        Assert.True(page.Available);
        Assert.Equal(42, page.Total);
        Assert.Equal(("RoleBanned", "Captain, Security"), (page.Items[0].Player, page.Items[0].Roles));
    }
}
