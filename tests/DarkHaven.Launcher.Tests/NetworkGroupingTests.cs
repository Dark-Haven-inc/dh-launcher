using DarkHaven.Launcher.Servers;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public class NetworkGroupingTests
{
    private static ServerEntry Server(string address, string name, int players = 10) =>
        new(address) { Name = name, Players = players };

    [Fact]
    public void A_shard_on_a_different_domain_joins_its_network_by_name()
    {
        // Real case that slipped through: 6 "МЁРТВЫЙ КОСМОС" shards on deadspace14.net form a
        // network, but one more shard of the same project lives on a completely different domain
        // (a partner's infrastructure) — it must not get stranded in misc just because its host key
        // differs, since every player recognizes it as the same network by name.
        ServerEntry[] servers =
        [
            Server("ss14s://f1.deadspace14.net", "МЁРТВЫЙ КОСМОС Фобос"),
            Server("ss14s://f2.deadspace14.net", "МЁРТВЫЙ КОСМОС Титан"),
            Server("ss14s://f3.deadspace14.net", "МЁРТВЫЙ КОСМОС Деймос"),
            Server("ss14s://s1.deadspace14.net", "МЁРТВЫЙ КОСМОС Союз-1"),
            Server("ss14s://main.luacorp.ru", "МЁРТВЫЙ КОСМОС Луна - Frontier"),
            Server("ss14://203.0.113.9:1212", "Мёртвый космос: Кассиопея"),
        ];

        var groups = NetworkGrouping.Group(servers);

        var mk = Assert.Single(groups, g => g.Label == "МЁРТВЫЙ КОСМОС");
        Assert.Equal(6, mk.Servers.Count);
        Assert.DoesNotContain(groups, g => g.Label == NetworkGrouping.MiscLabel);
    }

    [Fact]
    public void A_same_named_server_on_an_unrelated_domain_does_not_join_a_network()
    {
        // Guards against the opposite mistake: a short/coincidental prefix match shouldn't merge an
        // unrelated server into someone else's network.
        ServerEntry[] servers =
        [
            Server("ss14s://a.example.net", "Corvax One"),
            Server("ss14s://b.example.net", "Corvax Two"),
            Server("ss14s://c.example.net", "Corvax Three"),
            Server("ss14s://d.example.net", "Corvax Four"),
            Server("ss14://198.51.100.4:1212", "Completely Unrelated Station"),
        ];

        var groups = NetworkGrouping.Group(servers);

        var corvax = Assert.Single(groups, g => g.Label == "Corvax");
        Assert.Equal(4, corvax.Servers.Count);
        var misc = Assert.Single(groups, g => g.Label == NetworkGrouping.MiscLabel);
        Assert.Equal(["Completely Unrelated Station"], misc.Servers.Select(s => s.Name));
    }
}
