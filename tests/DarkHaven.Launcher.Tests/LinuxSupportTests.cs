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
    public void A_copied_launcher_folder_is_no_good_on_another_machine()
    {
        // The Linux scheme, whatever system runs the test; each database stands for one machine.
        var dir = Directory.CreateTempSubdirectory("dh-token-");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.db");
            var mine = new SettingsDatabase(path, useDpapi: false, machineId: () => "machine-a");
            mine.Initialize();
            var id = Guid.NewGuid();
            mine.UpsertLogin(new StoredLogin(id, "tester", "secret-token", DateTimeOffset.UtcNow.AddDays(30)));
            Assert.Equal("secret-token", Assert.Single(mine.GetLogins()).Token);

            // settings.db and settings.key both taken, opened on another machine.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Assert.Empty(new SettingsDatabase(path, useDpapi: false, machineId: () => "machine-b").GetLogins());
            Assert.Empty(new SettingsDatabase(path, useDpapi: false, machineId: () => null).GetLogins());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_token_saved_before_the_binding_still_signs_in_and_is_saved_bound()
    {
        var dir = Directory.CreateTempSubdirectory("dh-token-");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.db");
            var db = new SettingsDatabase(path, useDpapi: false, machineId: () => "machine-a");
            db.Initialize();

            // What launcher 0.3.9 wrote: AES-GCM with the file key alone.
            var fileKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(Path.Combine(dir.FullName, "settings.key"), fileKey);
            var plain = System.Text.Encoding.UTF8.GetBytes("old-token");
            var blob = new byte[12 + plain.Length + 16];
            System.Security.Cryptography.RandomNumberGenerator.Fill(blob.AsSpan(0, 12));
            using (var aes = new System.Security.Cryptography.AesGcm(fileKey, 16))
                aes.Encrypt(blob.AsSpan(0, 12), plain, blob.AsSpan(12, plain.Length), blob.AsSpan(12 + plain.Length));
            using (var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                con.Open();
                using var cmd = con.CreateCommand();
                cmd.CommandText = "INSERT INTO Login (UserId, UserName, Token, Expires) VALUES ($id, 'tester', $token, $expires)";
                cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("$token", "aes1:" + Convert.ToBase64String(blob));
                cmd.Parameters.AddWithValue("$expires", DateTime.UtcNow.AddDays(30));
                cmd.ExecuteNonQuery();
            }

            Assert.Equal("old-token", Assert.Single(db.GetLogins()).Token);

            // Rewritten bound: from now on another machine can't read it either.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Assert.Empty(new SettingsDatabase(path, useDpapi: false, machineId: () => "machine-b").GetLogins());
            Assert.Equal("old-token", Assert.Single(db.GetLogins()).Token);
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
