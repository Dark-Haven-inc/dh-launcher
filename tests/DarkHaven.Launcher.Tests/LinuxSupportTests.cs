using System.Text.Json;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Data;
using DarkHaven.Launcher.Engine;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public class LinuxSupportTests
{
    private static readonly string ThisRid = $"{RidUtility.CurrentOs}-{RidUtility.CurrentArch}";

    private static EngineManager.BundledEngine Parse(string json) =>
        JsonSerializer.Deserialize<EngineManager.BundledEngine>(json, LauncherJson.Options)!;

    [Fact]
    public void A_manifest_from_before_other_platforms_is_the_windows_build()
    {
        var engine = Parse("""{ "file": "275.1.0.zip", "sha256": "AB", "note": "live" }""");
        var build = EngineManager.PickBundledBuild(engine);

        if (ThisRid == "win-x64")
            Assert.Equal("275.1.0.zip", build?.File);
        else
            Assert.Null(build);
    }

    [Fact]
    public void Platform_builds_are_picked_by_rid()
    {
        var engine = Parse($$"""
            {
              "file": "275.1.0.zip", "sha256": "AB", "note": "live",
              "platforms": { "{{ThisRid}}": { "file": "275.1.0_mine.zip", "sha256": "CD" }, "haiku-x64": { "file": "x.zip", "sha256": "EF" } }
            }
            """);
        var build = EngineManager.PickBundledBuild(engine);

        Assert.Equal("275.1.0_mine.zip", build?.File);
        Assert.Equal("CD", build?.Sha256);
        Assert.Equal("live", build?.Note);
    }

    [Fact]
    public void Stored_token_is_encrypted_and_useless_without_its_key()
    {
        var dir = Directory.CreateTempSubdirectory("dh-token-");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.db");
            var db = new SettingsDatabase(path);
            db.Initialize();
            var id = Guid.NewGuid();
            db.UpsertLogin(new StoredLogin(id, "tester", "secret-token", DateTimeOffset.UtcNow.AddDays(30)));

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Assert.DoesNotContain("secret-token", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)));
            Assert.Equal("secret-token", Assert.Single(db.GetLogins()).Token);

            if (OperatingSystem.IsWindows())
                return;

            var key = Path.Combine(dir.FullName, "settings.key");
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(key));

            // The database copied somewhere without its key file.
            var copy = Path.Combine(dir.FullName, "copied.db");
            File.Copy(path, copy);
            Assert.Empty(new SettingsDatabase(copy).GetLogins());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Desktop_entry_exec_survives_awkward_paths()
    {
        Assert.Equal("\"/opt/Frontier 15/launcher\"", UriScheme.QuoteExecArg("/opt/Frontier 15/launcher"));
        // $ and " escaped for the Exec quoting, then the backslashes doubled for the string value.
        Assert.Equal("\"/home/a\\\\$b\\\\\"c\"", UriScheme.QuoteExecArg("/home/a$b\"c"));
        Assert.Equal("\"/100%%/x\"", UriScheme.QuoteExecArg("/100%/x"));

        var entry = UriScheme.DesktopEntry("/apps/F15.AppImage", "/icons/f15.png");
        Assert.Contains("Exec=\"/apps/F15.AppImage\" %u\n", entry);
        Assert.Contains("MimeType=x-scheme-handler/ss14;x-scheme-handler/ss14s;\n", entry);
    }

    [Fact]
    public void Default_handlers_are_set_without_touching_the_rest()
    {
        string[] mimes = ["x-scheme-handler/ss14", "x-scheme-handler/ss14s"];

        Assert.Equal(
            "[Default Applications]\nx-scheme-handler/ss14=f15.desktop;\nx-scheme-handler/ss14s=f15.desktop;\n",
            UriScheme.SetDefaultHandlers("", mimes, "f15.desktop"));

        const string existing = """
            [Added Associations]
            text/plain=gedit.desktop;

            [Default Applications]
            text/html=firefox.desktop;
            x-scheme-handler/ss14=old-launcher.desktop;

            [Removed Associations]
            image/png=gimp.desktop;
            """;
        var updated = UriScheme.SetDefaultHandlers(existing, mimes, "f15.desktop");

        Assert.Equal("""
            [Added Associations]
            text/plain=gedit.desktop;

            [Default Applications]
            text/html=firefox.desktop;
            x-scheme-handler/ss14=f15.desktop;
            x-scheme-handler/ss14s=f15.desktop;

            [Removed Associations]
            image/png=gimp.desktop;

            """.Replace("\r\n", "\n"), updated);

        Assert.Equal(updated, UriScheme.SetDefaultHandlers(updated, mimes, "f15.desktop"));
    }
}
