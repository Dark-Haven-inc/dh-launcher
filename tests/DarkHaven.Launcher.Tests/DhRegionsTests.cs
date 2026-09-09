using System.Net.Http;
using System.Text.Json;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Servers;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public class DhRegionsTests
{
    private const string Json = """
    [
      { "name": "ХЕЙВЕН", "address": "ss14://example.test:1212", "status": "open", "central": true,
        "neighbours": ["РУБЕЖ"] },
      { "name": "РУБЕЖ", "address": "", "status": "quarantine", "neighbours": ["ХЕЙВЕН"] },
      { "name": "ВЕРФЬ", "address": "" }
    ]
    """;

    [Fact]
    public void Parses_status_and_defaults_to_open()
    {
        var regions = JsonSerializer.Deserialize<DhRegion[]>(Json, LauncherJson.Options)!;

        Assert.Equal(3, regions.Length);
        Assert.False(regions[0].IsQuarantine);          // "open"
        Assert.True(regions[1].IsQuarantine);           // "quarantine"
        Assert.False(regions[2].IsQuarantine);          // status omitted -> open
    }

    [Fact]
    public async Task PollAsync_marks_quarantine_regions_offline_without_a_request()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, Json);

        try
        {
            // A handler that fails the test if it is ever called for the quarantined region.
            using var http = new HttpClient(new ThrowingHandler());
            var regions = new DhRegions(http, path);
            await regions.LoadAsync();

            var entries = await regions.PollAsync();

            var rubezh = entries.Single(e => e.Name == "РУБЕЖ");
            Assert.True(rubezh.RegionQuarantine);
            Assert.Equal(ServerReachability.Offline, rubezh.Reachability);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host == "example.test")
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));

            throw new Xunit.Sdk.XunitException($"unexpected request to {request.RequestUri}");
        }
    }
}
