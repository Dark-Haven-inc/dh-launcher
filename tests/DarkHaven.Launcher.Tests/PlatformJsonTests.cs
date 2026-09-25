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

    [Fact]
    public void Reads_a_player_card_with_the_game_servers_own_bans_and_rank()
    {
        const string json = """
        {"userId":"e9e66119-b624-4eff-9903-edcbf6d790b9","username":"Cadet_Nova",
         "memberSince":"2026-09-01T10:00:00+00:00","lastSeen":"2026-09-24T18:00:00+00:00","role":"moderator",
         "totalPlaytimeSeconds":7200,"launcherBanned":false,"launcherBanReason":null,"launcherBanExpires":null,
         "gameBans":[{"reason":"griefing","at":"2026-09-10T12:00:00+00:00","expiresAt":null,"isRoleBan":false,"lifted":true}],
         "gameAdminRank":"Модератор"}
        """;
        var who = JsonSerializer.Deserialize<PlatformPlayerInfo>(json, LauncherJson.Options)!;
        Assert.Equal("Модератор", who.GameAdminRank);
        var ban = Assert.Single(who.GameBans!);
        Assert.Equal(("griefing", false, true), (ban.Reason, ban.IsRoleBan, ban.Lifted));
    }

    /// <summary>An older platform (before game-access) simply doesn't send those two fields.</summary>
    [Fact]
    public void Reads_a_player_card_from_a_platform_that_knows_nothing_about_game_access()
    {
        const string json = """
        {"userId":"e9e66119-b624-4eff-9903-edcbf6d790b9","username":"Cadet_Nova",
         "memberSince":"2026-09-01T10:00:00+00:00","lastSeen":"2026-09-24T18:00:00+00:00","role":null,
         "totalPlaytimeSeconds":0,"launcherBanned":false,"launcherBanReason":null,"launcherBanExpires":null}
        """;
        var who = JsonSerializer.Deserialize<PlatformPlayerInfo>(json, LauncherJson.Options)!;
        Assert.Null(who.GameBans);
        Assert.Null(who.GameAdminRank);
    }

    [Fact]
    public void Reads_the_game_access_payload()
    {
        const string json = """
        {"enabled":true,
         "ranks":[{"id":1,"name":"Админ"},{"id":2,"name":"Модератор"}],
         "admins":[{"userId":"e9e66119-b624-4eff-9903-edcbf6d790b9","username":"Cadet_Nova","title":"Ведущий",
                    "rankId":1,"rankName":"Админ","suspended":false,"deadminned":true}]}
        """;
        var access = JsonSerializer.Deserialize<PlatformGameAccess>(json, LauncherJson.Options)!;
        Assert.True(access.Enabled);
        Assert.Equal(2, access.Ranks.Length);
        var a = Assert.Single(access.Admins);
        Assert.Equal(("Cadet_Nova", "Админ", true), (a.Username, a.RankName, a.Deadminned));
    }

    [Fact]
    public void Reads_how_many_people_have_the_launcher_open()
    {
        const string json = """
        {"at":"2026-09-25T01:00:00+00:00","total":37,"inGame":21,
         "peakDay":52,"peakDayAt":"2026-09-24T19:30:00+00:00",
         "peakEver":140,"peakEverAt":"2026-09-20T20:00:00+00:00",
         "regions":[{"name":"ХЕЙВЕН","players":21}]}
        """;
        var now = JsonSerializer.Deserialize<LauncherOnline>(json, LauncherJson.Options)!;
        Assert.Equal((37, 21, 52, 140), (now.Total, now.InGame, now.PeakDay, now.PeakEver));
        Assert.Equal(("ХЕЙВЕН", 21), (now.Regions[0].Name, now.Regions[0].Players));
    }

    [Fact]
    public void Reads_the_launcher_graph_including_the_in_game_line()
    {
        const string json = """
        {"range":"24h","bucketMinutes":5,
         "points":[{"at":"2026-09-25T00:55:00+00:00","average":30.5,"peak":37,"uptime":1}],
         "inGamePoints":[{"at":"2026-09-25T00:55:00+00:00","average":18.0,"peak":21,"uptime":1}],
         "summary":{"peak":37,"peakAt":"2026-09-25T00:55:00+00:00","average":30.5,"uptimePercent":100,"samples":288}}
        """;
        var h = JsonSerializer.Deserialize<LauncherHistory>(json, LauncherJson.Options)!;
        Assert.Equal(37, h.Points[0].Peak);
        Assert.Equal(21, h.InGamePoints[0].Peak);
        Assert.Equal(37, h.Summary.Peak);
    }

    [Fact]
    public void Reads_the_roles_list_with_who_gave_it_and_the_primary_owner()
    {
        const string json = """
        [{"userId":"e9e66119-b624-4eff-9903-edcbf6d790b9","username":"GODWINCH","role":"owner",
          "grantedByName":null,"grantedAt":"2026-09-25T10:00:00+00:00","isPrimary":true},
         {"userId":"a1e66119-b624-4eff-9903-edcbf6d790b9","username":"Cadet_Nova","role":"moderator",
          "grantedByName":"GODWINCH","grantedAt":"2026-09-25T11:00:00+00:00","isPrimary":false}]
        """;
        var roles = JsonSerializer.Deserialize<PlatformRole[]>(json, LauncherJson.Options)!;
        Assert.True(roles[0].IsPrimary);
        Assert.Equal(("moderator", "GODWINCH", false), (roles[1].Role, roles[1].GrantedByName, roles[1].IsPrimary));
    }

    /// <summary>An older platform sends only userId/username/role — still readable.</summary>
    [Fact]
    public void Reads_a_roles_list_from_an_older_platform()
    {
        const string json = """[{"userId":"e9e66119-b624-4eff-9903-edcbf6d790b9","username":"GODWINCH","role":"owner"}]""";
        var r = Assert.Single(JsonSerializer.Deserialize<PlatformRole[]>(json, LauncherJson.Options)!);
        Assert.Equal(("owner", false, (string?)null), (r.Role, r.IsPrimary, r.GrantedByName));
    }

    [Fact]
    public void Reads_the_linked_discord_on_the_profile_and_the_admin_card()
    {
        const string profile = """
        {"userId":"e9e66119-b624-4eff-9903-edcbf6d790b9","username":"GODWINCH","avatarUrl":null,"frame":"blue","title":null,
         "discordId":"1234567890","discordName":"godwinch","discordLinkedAt":"2026-09-25T20:00:00+00:00","discordAvatar":null,
         "memberSince":"2026-09-01T00:00:00+00:00","totalPlaytimeSeconds":0,"playtimeSource":"unavailable",
         "launcherBanned":false,"launcherBanReason":null,"launcherBanExpires":null}
        """;
        var p = JsonSerializer.Deserialize<PlatformProfile>(profile, LauncherJson.Options)!;
        Assert.Equal(("1234567890", "godwinch"), (p.DiscordId, p.DiscordName));

        const string card = """
        {"userId":"e9e66119-b624-4eff-9903-edcbf6d790b9","username":"GODWINCH",
         "memberSince":"2026-09-01T00:00:00+00:00","lastSeen":"2026-09-25T00:00:00+00:00","role":"owner",
         "totalPlaytimeSeconds":0,"launcherBanned":false,"launcherBanReason":null,"launcherBanExpires":null,
         "discord":{"discordId":"1234567890","discordName":null,"linkedAt":"2026-09-25T20:00:00+00:00"}}
        """;
        var who = JsonSerializer.Deserialize<PlatformPlayerInfo>(card, LauncherJson.Options)!;
        Assert.Equal("1234567890", who.Discord!.Display); // no name → the id
    }
}
