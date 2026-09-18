using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using DarkHaven.Launcher.Update;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public class LaunchErrorsTests
{
    [Fact]
    public void Server_errors_read_as_a_restart_not_as_an_exception()
    {
        var text = LaunchErrors.Describe(new HttpRequestException(
            "Response status code does not indicate success: 503 (Service Unavailable).",
            null, HttpStatusCode.ServiceUnavailable));

        Assert.Contains("503", text);
        Assert.Contains("перезапускается", text);
        Assert.DoesNotContain("Response status code", text);
    }

    [Fact]
    public void Unreachable_and_unknown_hosts_are_told_apart()
    {
        var refused = new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused));
        var unknown = new HttpRequestException("no such host", new SocketException((int)SocketError.HostNotFound));

        Assert.Contains("недоступен", LaunchErrors.Describe(refused));
        Assert.Contains("опечатки", LaunchErrors.Describe(unknown));
    }

    [Fact]
    public void Our_own_messages_pass_through()
    {
        const string ours = "Сервер не сообщил информацию о сборке";
        Assert.Equal(ours, LaunchErrors.Describe(new InvalidOperationException(ours)));
    }
}
