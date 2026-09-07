using DarkHaven.Launcher.Models;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public class Ss14AddressTests
{
    [Theory]
    [InlineData("ss14://localhost", "http://localhost:1212/info")]
    [InlineData("localhost", "http://localhost:1212/info")]
    [InlineData("ss14://localhost:1234", "http://localhost:1234/info")]
    [InlineData("game.hardlight.space", "http://game.hardlight.space:1212/info")]
    [InlineData("ss14s://server.ss14.org/venus/", "https://server.ss14.org/venus/info")]
    [InlineData("ss14s://server.ss14.org/venus", "https://server.ss14.org/venus/info")]
    [InlineData("ss14s://example.com", "https://example.com/info")]
    public void InfoAddress_is_derived_correctly(string input, string expectedInfo)
    {
        Assert.True(Ss14Address.TryParse(input, out var uri));
        Assert.Equal(expectedInfo, Ss14Address.InfoAddress(uri!).ToString());
    }

    [Theory]
    [InlineData("ss14://1.2.3.4:1212", "udp://1.2.3.4:1212/")]
    [InlineData("ss14://host", "udp://host:1212/")]
    [InlineData("ss14s://host/path/", "udp://host:443/")]
    public void DeriveConnectAddress_uses_api_host_and_port(string input, string expected)
    {
        var uri = Ss14Address.Parse(input);
        Assert.Equal(expected, Ss14Address.DeriveConnectAddress(uri).ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://example.com")]
    [InlineData("ftp://example.com")]
    [InlineData("ss14://")]
    public void TryParse_rejects_invalid(string input)
    {
        Assert.False(Ss14Address.TryParse(input, out _));
    }

    [Fact]
    public void Acz_manifest_urls_are_under_api_base()
    {
        var uri = Ss14Address.Parse("ss14://host:1212");
        Assert.Equal("http://host:1212/manifest.txt", Ss14Address.SelfhostedManifest(uri).ToString());
        Assert.Equal("http://host:1212/download", Ss14Address.SelfhostedManifestDownload(uri).ToString());
        Assert.Equal("http://host:1212/client.zip", Ss14Address.SelfhostedClientZip(uri).ToString());
    }
}
