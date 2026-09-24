using System.Text.Json;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Models;
using DarkHaven.Launcher.Servers;
using Xunit;

namespace DarkHaven.Launcher.Tests;

/// <summary>
/// The launcher must not drop a player into someone else's game because that server happened to
/// answer on our region's address — but it must also never block a normal connect, so every
/// "can't tell" case has to pass.
/// </summary>
public class ServerIdentityTests
{
    private static ServerInfo Info(string? forkId, string? desc = null) => new()
    {
        Auth = new ServerAuthInfo { Mode = AuthMode.Required, PublicKey = "whatever" },
        Desc = desc,
        Build = new ServerBuildInfo { EngineVersion = "275.1.0", Version = "abc", ForkId = forkId },
    };

    [Fact]
    public void Nothing_pinned_means_nothing_to_check()
    {
        Assert.Null(ServerIdentity.Mismatch(null, Info("marines"), "ХЕЙВЕН"));
        Assert.Null(ServerIdentity.Mismatch("  ", Info("marines"), "ХЕЙВЕН"));
    }

    [Fact]
    public void Our_own_server_passes_whatever_the_case()
    {
        Assert.Null(ServerIdentity.Mismatch("frontier15", Info("frontier15"), "ХЕЙВЕН"));
        Assert.Null(ServerIdentity.Mismatch("frontier15", Info("Frontier15"), "ХЕЙВЕН"));
        Assert.Null(ServerIdentity.Mismatch(" frontier15 ", Info("frontier15"), "ХЕЙВЕН"));
    }

    /// <summary>A server that reports no fork id proves nothing — connecting is still the player's call.</summary>
    [Fact]
    public void A_server_that_says_nothing_is_not_accused()
    {
        Assert.Null(ServerIdentity.Mismatch("frontier15", Info(null), "ХЕЙВЕН"));
        Assert.Null(ServerIdentity.Mismatch("frontier15", Info(""), "ХЕЙВЕН"));
    }

    [Fact]
    public void A_stranger_on_our_address_is_named_and_refused()
    {
        var msg = ServerIdentity.Mismatch("frontier15", Info("marines", "Colonial Marines RU"), "ХЕЙВЕН");
        Assert.NotNull(msg);
        Assert.Contains("ХЕЙВЕН", msg);
        Assert.Contains("marines", msg);
        Assert.Contains("Colonial Marines RU", msg);
    }

    [Fact]
    public void A_region_carries_its_expected_fork_into_the_server_entry()
    {
        const string json = """
        [{"name":"ХЕЙВЕН","address":"ss14://10.0.0.1:1212","expectFork":"frontier15"},
         {"name":"РУБЕЖ","address":""}]
        """;
        var regions = JsonSerializer.Deserialize<DhRegion[]>(json, LauncherJson.Options)!;
        Assert.Equal("frontier15", regions[0].ExpectFork);
        Assert.Null(regions[1].ExpectFork);
    }
}
