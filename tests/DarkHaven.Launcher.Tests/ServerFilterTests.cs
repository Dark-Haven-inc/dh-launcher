using DarkHaven.Launcher.Servers;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public class ServerFilterTests
{
    private static ServerEntry Server(string name, int players, params string[] tags) =>
        new($"ss14://{name.ToLowerInvariant().Replace(' ', '-')}")
        {
            Name = name,
            Players = players,
            SoftMaxPlayers = 50,
            Reachability = ServerReachability.Online,
            Tags = tags,
        };

    private static readonly ServerEntry[] Servers =
    [
        Server("Alpha RU HRP", 20, "lang:ru", "rp:high"),
        Server("Bravo EN", 0, "lang:en", "rp:none"),
        Server("Charlie 18+", 5, "lang:en", "18+", "rp:med"),
        Server("Delta full", 50, "lang:ru", "rp:med"),
    ];

    [Fact]
    public void Search_matches_name_and_address()
    {
        var f = new ServerFilter { Search = "bravo" };
        Assert.Equal(["Bravo EN"], f.Apply(Servers).Select(s => s.Name));

        f.Search = "charlie-18";
        Assert.Equal(["Charlie 18+"], f.Apply(Servers).Select(s => s.Name));
    }

    [Fact]
    public void HideEmpty_and_HideFull()
    {
        Assert.DoesNotContain(new ServerFilter { HideEmpty = true }.Apply(Servers), s => s.Name == "Bravo EN");
        Assert.DoesNotContain(new ServerFilter { HideFull = true }.Apply(Servers), s => s.Name == "Delta full");
    }

    [Fact]
    public void Hide18Plus()
    {
        Assert.DoesNotContain(new ServerFilter { Hide18Plus = true }.Apply(Servers), s => s.Name == "Charlie 18+");
    }

    [Fact]
    public void Language_and_roleplay_filters()
    {
        var ru = new ServerFilter();
        ru.Languages.Add("ru");
        Assert.Equal(["Alpha RU HRP", "Delta full"], ru.Apply(Servers).Select(s => s.Name).OrderBy(x => x));

        var hrp = new ServerFilter();
        hrp.RolePlay.Add("high");
        Assert.Equal(["Alpha RU HRP"], hrp.Apply(Servers).Select(s => s.Name));
    }

    [Fact]
    public void Sorts_by_players_desc_then_name()
    {
        var byPlayers = new ServerFilter { Sort = ServerSort.Players }.Apply(Servers).Select(s => s.Name).ToArray();
        Assert.Equal(["Delta full", "Alpha RU HRP", "Charlie 18+", "Bravo EN"], byPlayers);

        var byName = new ServerFilter { Sort = ServerSort.Name }.Apply(Servers).Select(s => s.Name).ToArray();
        Assert.Equal(["Alpha RU HRP", "Bravo EN", "Charlie 18+", "Delta full"], byName);
    }
}
