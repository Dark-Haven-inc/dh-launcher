using DarkHaven.Launcher.Local;
using Xunit;

namespace DarkHaven.Launcher.Tests;

/// <summary>A test that runs only with <c>DH_LOCAL_E2E=1</c>: it downloads about a gigabyte and takes minutes.</summary>
public sealed class LiveLocalServerFactAttribute : FactAttribute
{
    public LiveLocalServerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DH_LOCAL_E2E") != "1")
            Skip = "a real server from the published builds: set DH_LOCAL_E2E=1 (about 1 GB of downloads, minutes)";
    }
}

/// <summary>
/// ЛОКАЛКА for real, on whatever system runs the test: the published build for this platform, Microsoft's
/// .NET runtime downloaded as a player without .NET would get it, the server started and stopped. The
/// workflow <c>local-servers-check.yml</c> runs it on Linux and Windows.
/// </summary>
public sealed class LocalServerLiveTests : IDisposable
{
    // Short: on Windows the server's resources nest deep, and a long root trips the 260-character limit.
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dhl-{Guid.NewGuid().ToString("N")[..8]}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* the OS cleans temp */ }
    }

    [LiveLocalServerFact]
    public async Task The_published_build_for_this_system_starts_and_stops()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var servers = new LocalServers(http, _root);
        servers.Runtime = new DotnetRuntime(http, Path.Combine(_root, "dotnet"), trySystem: false);

        var list = await servers.Catalog.GetAsync();
        Assert.True(list.Builds.Count > 0, $"no published build for {LocalBuildCatalog.Rid}");

        var profile = servers.Profiles.New("Проверка", LocalServerMode.Play);
        servers.Profiles.Save(profile);
        var host = servers.HostFor(profile);
        try
        {
            await servers.StartAsync(profile);
            Assert.True(host.State == LocalServerState.Running,
                $"{host.State}: {host.Detail}\n{string.Join('\n', host.Lines.TakeLast(60))}");
            var major = servers.Builds.RequiredDotnetMajor(list.Builds[0].Version) ?? 10;
            Assert.True(DotnetRuntime.HasRuntime(Path.Combine(_root, "dotnet"), major), "Microsoft's runtime wasn't used");
        }
        finally
        {
            await host.StopAsync();
        }
        Assert.Equal(LocalServerState.Stopped, host.State);
    }
}
