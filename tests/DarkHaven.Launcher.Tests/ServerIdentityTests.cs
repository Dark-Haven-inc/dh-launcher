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

    /// <summary>ХЕЙВЕН today: our server says "custom" and is moving to "Dark-Haven" on the CDN; the
    /// Colonial Marines server on the same machine says "colonialmarines".</summary>
    [Theory]
    [InlineData("custom", true)]
    [InlineData("Dark-Haven", true)]
    [InlineData("dark-haven", true)]
    [InlineData("colonialmarines", false)]
    [InlineData("colonialmarines-pathogen", false)]
    public void Haven_accepts_its_current_and_next_build_and_refuses_the_marines(string fork, bool ours) =>
        Assert.Equal(ours, ServerIdentity.Mismatch("custom,Dark-Haven", Info(fork), "ХЕЙВЕН") is null);

    [Fact]
    public void The_bundled_regions_pin_haven_against_the_marines()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "dh-regions.json");
        if (!File.Exists(path))
            path = Path.Combine(FindRepoRoot(), "src", "DarkHaven.Launcher", "Assets", "dh-regions.json");
        var regions = JsonSerializer.Deserialize<DhRegion[]>(File.ReadAllText(path), LauncherJson.Options)!;
        var haven = regions.Single(r => r.Name == "ХЕЙВЕН");
        Assert.NotNull(ServerIdentity.Mismatch(haven.ExpectFork, Info("colonialmarines"), haven.Name));
        Assert.Null(ServerIdentity.Mismatch(haven.ExpectFork, Info("custom"), haven.Name));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DarkHavenLauncher.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
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
