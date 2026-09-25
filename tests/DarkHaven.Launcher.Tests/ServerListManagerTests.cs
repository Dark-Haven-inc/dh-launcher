using System.Net;
using System.Text;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Servers;
using Xunit;

namespace DarkHaven.Launcher.Tests;

/// <summary>
/// СЕРВЕРЫ is the platform's approved list, each server asked for its own /status. No network: a
/// stub answers for the platform and for two servers, one up and one down.
/// </summary>
public sealed class ServerListManagerTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), $"dh-servers-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_cache)) File.Delete(_cache);
    }

    private sealed class Stub(bool platformUp) : HttpMessageHandler
    {
        private const string List = """
            [{"id":1,"name":"Рассвет","address":"ss14://203.0.113.1:1212","description":"РП-фронтир"},
             {"id":2,"name":"Спящий","address":"ss14://203.0.113.2:1212","description":null}]
            """;
        private const string Status = """{"name":"MyServer","players":7,"soft_max_players":40,"map":"Сектор","tags":[]}""";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var uri = request.RequestUri!;
            HttpResponseMessage Json(string body) =>
                new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

            if (uri.Host == "platform.test")
                return Task.FromResult(platformUp ? Json(List) : new HttpResponseMessage(HttpStatusCode.BadGateway));
            if (uri.Host == "203.0.113.1" && uri.AbsolutePath == "/status")
                return Task.FromResult(Json(Status));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private ServerListManager Manager(bool platformUp)
    {
        var http = new HttpClient(new Stub(platformUp));
        return new ServerListManager(new PlatformApi(http, "https://platform.test"), http, _cache);
    }

    [Fact]
    public async Task Shows_the_approved_servers_with_their_live_status()
    {
        var mgr = Manager(platformUp: true);
        await mgr.RefreshAsync();

        Assert.Null(mgr.StaleSince);
        var up = mgr.Servers.Single(s => s.Address == "ss14://203.0.113.1:1212");
        Assert.Equal(ServerReachability.Online, up.Reachability);
        Assert.Equal(7, up.Players);
        Assert.Equal("Рассвет", up.Name); // the approved name, not the "MyServer" it reports
        Assert.Equal("РП-фронтир", up.Description);

        var down = mgr.Servers.Single(s => s.Address == "ss14://203.0.113.2:1212");
        Assert.Equal(ServerReachability.Offline, down.Reachability);
        Assert.Equal("Спящий", down.Name);
    }

    [Fact]
    public async Task When_the_platform_is_down_the_last_list_is_still_there()
    {
        await Manager(platformUp: true).RefreshAsync();

        var offline = Manager(platformUp: false);
        await offline.RefreshAsync();

        Assert.NotNull(offline.StaleSince);
        Assert.Equal(["Рассвет", "Спящий"], offline.Servers.Select(s => s.Name).ToArray());
    }

    [Fact]
    public async Task No_platform_and_no_copy_means_an_empty_list_not_a_crash()
    {
        var mgr = Manager(platformUp: false);
        await mgr.RefreshAsync();
        Assert.Empty(mgr.Servers);
    }
}
