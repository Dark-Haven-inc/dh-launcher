using System.Net;
using System.Net.Http;
using DarkHaven.Launcher.Servers;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public class SlotWatcherTests
{
    /// <summary>Answers /status with the next player count from the queue (the last one repeats).</summary>
    private sealed class StatusQueue(params int[] players) : HttpMessageHandler
    {
        private int _i;
        public int Requests => _i;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var p = players[Math.Min(_i++, players.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"name":"X","players":{{p}},"soft_max_players":30}"""),
            });
        }
    }

    [Theory]
    [InlineData(30, 30, true)]
    [InlineData(31, 30, true)]
    [InlineData(29, 30, false)]
    [InlineData(80, 0, false)] // no cap reported — never "full"
    public void Full_means_at_the_soft_cap(int players, int max, bool full) =>
        Assert.Equal(full, SlotWatcher.IsFull(players, max));

    [Fact]
    public async Task Fires_once_when_a_slot_opens_then_stops()
    {
        var handler = new StatusQueue(30, 30, 29);
        using var watcher = new SlotWatcher(new HttpClient(handler), TimeSpan.FromMilliseconds(10));
        var fired = new TaskCompletionSource<(string, string)>();
        var count = 0;
        watcher.SlotFreed += (n, a) => { count++; fired.TrySetResult((n, a)); };

        watcher.Watch("ss14://example.test:1212", "ХЕЙВЕН");
        Assert.True(watcher.IsWatchingAddress("SS14://EXAMPLE.TEST:1212"));

        var (name, address) = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ХЕЙВЕН", name);
        Assert.Equal("ss14://example.test:1212", address);
        Assert.False(watcher.IsWatching);

        await Task.Delay(100);
        Assert.Equal(1, count);
        Assert.Equal(3, handler.Requests); // no polling after it's done
    }

    [Fact]
    public async Task Stop_means_no_call()
    {
        var handler = new StatusQueue(29);
        using var watcher = new SlotWatcher(new HttpClient(handler), TimeSpan.FromMilliseconds(50));
        var fired = false;
        watcher.SlotFreed += (_, _) => fired = true;

        watcher.Watch("ss14://example.test:1212", "X");
        watcher.Stop();
        await Task.Delay(200);

        Assert.False(fired);
        Assert.False(watcher.IsWatching);
    }

    [Fact]
    public void Ignores_addresses_it_cannot_poll()
    {
        using var watcher = new SlotWatcher(new HttpClient(new StatusQueue(0)));
        watcher.Watch("https://not-a-server.example/", "X");
        Assert.False(watcher.IsWatching);
    }
}
