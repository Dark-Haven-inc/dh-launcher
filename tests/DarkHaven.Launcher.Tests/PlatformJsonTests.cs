using System.Text.Json;
using DarkHaven.Launcher.Api;
using Xunit;

namespace DarkHaven.Launcher.Tests;

/// <summary>The platform's actual response shapes, as the launcher has to read them.</summary>
public class PlatformJsonTests
{
    [Fact]
    public void Reads_the_friends_list()
    {
        const string json = """
        {"friends":[{"id":1,"userId":"e9e66119-b624-4eff-9903-edcbf6d790b9","username":"Cadet_Nova","frame":"gold",
          "avatarUrl":"/api/media/bbc4d7ba0ce84c28991636af9ffc6bf4","online":true,"serverName":"ХЕЙВЕН",
          "serverAddress":"ss14://95.31.51.216:1212"}],
         "incomingRequests":[],"outgoingRequests":[]}
        """;
        var list = JsonSerializer.Deserialize<PlatformFriends>(json, LauncherJson.Options)!;
        var f = Assert.Single(list.Friends);
        Assert.Equal(("Cadet_Nova", "/api/media/bbc4d7ba0ce84c28991636af9ffc6bf4", "ХЕЙВЕН"), (f.Username, f.AvatarUrl, f.ServerName));
    }
}
