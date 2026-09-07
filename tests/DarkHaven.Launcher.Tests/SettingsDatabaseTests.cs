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

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch { /* best effort */ }
    }
}
