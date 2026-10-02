using DarkHaven.Launcher.Servers;
using Xunit;

namespace DarkHaven.Launcher.Tests;

/// <summary>Servers that run at set times: the countdown on the list and the favourites' alerts.</summary>
public sealed class LaunchScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 18, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(2 * 24 * 60 + 3 * 60, "запуск через 2 д 3 ч")]
    [InlineData(5 * 60 + 20, "запуск через 5 ч 20 мин")]
    [InlineData(3 * 60, "запуск через 3 ч")]
    [InlineData(14, "запуск через 14 мин")]
    public void The_countdown_reads_naturally(int minutesLeft, string text) =>
        Assert.Equal(text, LaunchSchedule.Describe(Now.AddMinutes(minutesLeft), false, Now));

    [Fact]
    public void Around_and_after_the_launch()
    {
        Assert.Equal("запуск через меньше минуты", LaunchSchedule.Describe(Now.AddSeconds(10), false, Now));
        Assert.Equal("запуск через 1 мин", LaunchSchedule.Describe(Now.AddSeconds(50), false, Now));
        Assert.Equal("запускается…", LaunchSchedule.Describe(Now.AddMinutes(-5), false, Now));
        Assert.Equal("запущен", LaunchSchedule.Describe(Now.AddMinutes(-5), true, Now));
        Assert.Null(LaunchSchedule.Describe(Now.AddHours(-4), true, Now)); // long gone: nothing to say
        Assert.Null(LaunchSchedule.Describe(null, true, Now));
    }

    private static ServerEntry Server(string address, DateTimeOffset? at, bool online = false, string? note = null) =>
        new(address)
        {
            Name = "Ивент",
            NextLaunchAt = at,
            LaunchNote = note,
            Reachability = online ? ServerReachability.Online : ServerReachability.Offline,
        };

    [Fact]
    public void A_favourite_gets_soon_then_started_once_each()
    {
        var alerts = new LaunchAlerts();
        var favourites = new HashSet<string>(["ss14://event.example"], StringComparer.OrdinalIgnoreCase);
        var at = Now.AddMinutes(30);

        Assert.Empty(alerts.Check([Server("ss14://event.example", at)], favourites, Now)); // 30 min out: too early

        var soon = alerts.Check([Server("ss14://event.example", at, note: "штурм")], favourites, Now.AddMinutes(20));
        Assert.Equal(["Скоро запуск: «Ивент» через 10 мин — штурм."], soon);
        Assert.Empty(alerts.Check([Server("ss14://event.example", at)], favourites, Now.AddMinutes(21)));

        // Time's up but the server isn't answering yet: nothing until it does.
        Assert.Empty(alerts.Check([Server("ss14://event.example", at)], favourites, Now.AddMinutes(31)));
        var started = alerts.Check([Server("ss14://event.example", at, online: true)], favourites, Now.AddMinutes(33));
        Assert.Equal(["Сервер «Ивент» запущен — можно заходить."], started);
        Assert.Empty(alerts.Check([Server("ss14://event.example", at, online: true)], favourites, Now.AddMinutes(34)));

        // The owner sets the next launch: that one is news again.
        var next = Now.AddDays(1);
        Assert.Single(alerts.Check([Server("ss14://event.example", next)], favourites, next.AddMinutes(-5)));
    }

    [Fact]
    public void Only_favourites_and_never_a_stale_start()
    {
        var alerts = new LaunchAlerts();
        var favourites = new HashSet<string>(["ss14://fav.example"], StringComparer.OrdinalIgnoreCase);

        Assert.Empty(alerts.Check([Server("ss14://other.example", Now.AddMinutes(5))], favourites, Now));
        // Opened the launcher long after the launch: no "запущен" from four hours ago, and no "скоро" at all.
        Assert.Empty(alerts.Check([Server("ss14://fav.example", Now.AddHours(-4), online: true)], favourites, Now));
        // Opened it after the launch, server up: just "запущен", not "скоро" too.
        Assert.Equal(["Сервер «Ивент» запущен — можно заходить."],
            alerts.Check([Server("ss14://fav.example", Now.AddMinutes(-10), online: true)], favourites, Now));
    }
}
