using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Data;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public class SettingsDatabaseTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dh-settings-{Guid.NewGuid():N}.db");

    [Fact]
    public void Login_round_trips_and_upserts()
    {
        var db = new SettingsDatabase(_path);
        db.Initialize();

        var id = Guid.NewGuid();
        var expires = DateTimeOffset.UtcNow.AddDays(30);
        db.UpsertLogin(new StoredLogin(id, "GODWINCH", "tok-1", expires));

        var loaded = Assert.Single(db.GetLogins());
        Assert.Equal("GODWINCH", loaded.UserName);
        Assert.Equal("tok-1", loaded.Token);
        Assert.Equal(id, loaded.UserId);

        db.UpsertLogin(new StoredLogin(id, "GODWINCH", "tok-2", expires));
        Assert.Equal("tok-2", Assert.Single(db.GetLogins()).Token);

        db.DeleteLogin(id);
        Assert.Empty(db.GetLogins());
    }

    [Fact]
    public void Config_kv_round_trips()
    {
        var db = new SettingsDatabase(_path);
        db.Initialize();

        Assert.Null(db.GetConfig("SelectedLogin"));
        db.SetConfig("SelectedLogin", "abc");
        Assert.Equal("abc", db.GetConfig("SelectedLogin"));
        db.SetConfig("SelectedLogin", null);
        Assert.Null(db.GetConfig("SelectedLogin"));
    }

    [Fact]
    public void Initialize_is_idempotent()
    {
        var db = new SettingsDatabase(_path);
        db.Initialize();
        db.Initialize();
        db.SetConfig("x", "1");
        db.Initialize();
        Assert.Equal("1", db.GetConfig("x"));
    }

    [Theory]
    [InlineData(-1, true, true)]     // already expired
    [InlineData(5, false, true)]     // inside the 15-day refresh window
    [InlineData(40, false, false)]   // fresh
    public void AuthToken_time_logic(int daysFromNow, bool expired, bool shouldRefresh)
    {
        var token = new AuthToken("t", DateTimeOffset.UtcNow.AddDays(daysFromNow));
        Assert.Equal(expired, token.IsTimeExpired);
        Assert.Equal(shouldRefresh, token.ShouldRefresh);
    }

    [Fact]
    public void Recent_bumps_dedupes_and_trims_to_twelve()
    {
        var db = new SettingsDatabase(_path);
        db.Initialize();

        for (var i = 0; i < 15; i++)
            db.RecordRecent($"ss14://s{i}", $"Server {i}", isRegion: i == 0);

        var recent = db.GetRecent();
        Assert.Equal(12, recent.Count);
        Assert.Equal("ss14://s14", recent[0].Address);      // newest first
        Assert.DoesNotContain(recent, r => r.Address == "ss14://s0");

        db.RecordRecent("ss14://s10", "Server 10 renamed", isRegion: true);
        recent = db.GetRecent();
        Assert.Equal("ss14://s10", recent[0].Address);       // bumped to front
        Assert.Equal("Server 10 renamed", recent[0].Name);
        Assert.True(recent[0].IsRegion);
        Assert.Equal(12, recent.Count);                      // still deduped
    }

    [Fact]
    public void Favorites_toggle_round_trips()
    {
        var db = new SettingsDatabase(_path);
        db.Initialize();

        Assert.False(db.IsFavorite("ss14://x"));
        Assert.True(db.ToggleFavorite("ss14://x", "X server"));
        Assert.True(db.IsFavorite("ss14://x"));
        Assert.Equal("X server", Assert.Single(db.GetFavorites()).Name);

        Assert.False(db.ToggleFavorite("ss14://x", "X server"));
        Assert.Empty(db.GetFavorites());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch { /* best effort */ }
    }
}
