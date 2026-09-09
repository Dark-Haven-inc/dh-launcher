using System.Net;
using System.Net.Http;
using DarkHaven.Launcher.Api;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public class HubApiTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), $"dh-hub-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task Falls_back_to_the_cached_list_when_the_hub_is_down()
    {
        const string body = """[{"address":"ss14://a.test:1212","statusData":{"name":"A","players":3}}]""";

        // 1st call: hub responds — list returned + cache written
        var ok = new HubApi(new HttpClient(new StubHandler(_ => Respond(HttpStatusCode.OK, body))), _cache);
        var live = await ok.GetServersAsync();
        Assert.Single(live);
        Assert.Equal("ss14://a.test:1212", live[0].Address);
        Assert.Null(ok.ServedFromCacheAt);
        Assert.True(File.Exists(_cache));

        // 2nd call: hub is down — cached list is served, flagged stale
        var down = new HubApi(new HttpClient(new StubHandler(_ => throw new HttpRequestException("no route"))), _cache);
        var cached = await down.GetServersAsync();
        Assert.Single(cached);
        Assert.Equal("ss14://a.test:1212", cached[0].Address);
        Assert.NotNull(down.ServedFromCacheAt);
    }

    private static HttpResponseMessage Respond(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    public void Dispose()
    {
        try { File.Delete(_cache); } catch { /* best effort */ }
    }
}
