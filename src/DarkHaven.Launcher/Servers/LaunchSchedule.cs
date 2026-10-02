namespace DarkHaven.Launcher.Servers;

/// <summary>
/// Servers that run at set times rather than all day (their owner sets the next launch on the platform): the countdown
/// shown on the list, and what to tell the player about the ones in their favourites.
/// </summary>
public static class LaunchSchedule
{
    /// <summary>How long before a launch the player hears it's coming.</summary>
    public static readonly TimeSpan SoonBefore = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long after the set time a server that hasn't come up yet still counts as launching, and a server that has is
    /// still news. The platform stops showing a launch after 3 hours anyway.
    /// </summary>
    public static readonly TimeSpan StartWindow = TimeSpan.FromHours(3);

    /// <summary>"запуск через 2 ч 15 мин", "запускается…", "запущен"; null when there is no launch to talk about.</summary>
    public static string? Describe(DateTimeOffset? at, bool online, DateTimeOffset now)
    {
        if (at is not { } launch)
            return null;

        var left = launch - now;
        if (left > TimeSpan.Zero)
            return $"запуск через {Span(left)}";
        if (-left > StartWindow)
            return null;
        return online ? "запущен" : "запускается…";
    }

    /// <summary>"3 д 4 ч", "5 ч 20 мин", "14 мин", "меньше минуты".</summary>
    public static string Span(TimeSpan span)
    {
        if (span.TotalDays >= 1)
            return span.Hours > 0 ? $"{(int)span.TotalDays} д {span.Hours} ч" : $"{(int)span.TotalDays} д";
        if (span.TotalHours >= 1)
            return span.Minutes > 0 ? $"{(int)span.TotalHours} ч {span.Minutes} мин" : $"{(int)span.TotalHours} ч";
        // Rounded up: "через 1 мин" rather than "через 0 мин" for the last seconds.
        var minutes = (int)Math.Ceiling(span.TotalMinutes);
        return minutes >= 1 && span.TotalSeconds >= 30 ? $"{minutes} мин" : "меньше минуты";
    }
}

/// <summary>
/// The favourites' launch alerts: "скоро запуск" once the launch is <see cref="LaunchSchedule.SoonBefore"/> away, and
/// "запущен" once its time has come and the server answers - each once per launch, however often it's asked.
/// </summary>
public sealed class LaunchAlerts
{
    private readonly HashSet<string> _sent = [];

    public IReadOnlyList<string> Check(IEnumerable<ServerEntry> servers, ISet<string> favourites, DateTimeOffset now)
    {
        var alerts = new List<string>();
        foreach (var server in servers)
        {
            if (server.NextLaunchAt is not { } at || !favourites.Contains(server.Address))
                continue;

            var name = server.DisplayName;
            var note = string.IsNullOrWhiteSpace(server.LaunchNote) ? "" : $" — {server.LaunchNote.Trim()}";
            var key = $"{server.Address}|{at.UtcTicks}";

            if (now >= at)
            {
                if (now - at <= LaunchSchedule.StartWindow
                    && server.Reachability == ServerReachability.Online
                    && _sent.Add(key + "|started"))
                {
                    _sent.Add(key + "|soon"); // started already: "soon" would be old news
                    alerts.Add($"Сервер «{name}» запущен — можно заходить{note}.");
                }
            }
            else if (at - now <= LaunchSchedule.SoonBefore && _sent.Add(key + "|soon"))
            {
                alerts.Add($"Скоро запуск: «{name}» через {LaunchSchedule.Span(at - now)}{note}.");
            }
        }
        return alerts;
    }
}
