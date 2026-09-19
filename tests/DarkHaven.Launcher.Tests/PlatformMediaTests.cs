using System.Net.Http;
using DarkHaven.Launcher.Api;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public class PlatformMediaTests
{
    private static readonly PlatformApi Api = new(new HttpClient(), "https://api.example.test/");

    [Fact]
    public void Media_paths_resolve_against_the_platform()
    {
        Assert.Equal(new Uri("https://api.example.test/api/media/0123abcd"), Api.MediaUri("/api/media/0123abcd"));
    }

    [Theory]
    [InlineData("https://tracker.example/pixel.png")] // someone else's host — an IP logger
    [InlineData("//tracker.example/api/media/x")]     // protocol-relative
    [InlineData("/api/profile/me")]                    // a platform path, but not media
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_refused(string? path) => Assert.Null(Api.MediaUri(path));

    [Fact]
    public void Nothing_without_a_platform() =>
        Assert.Null(new PlatformApi(new HttpClient(), null).MediaUri("/api/media/0123abcd"));
}
