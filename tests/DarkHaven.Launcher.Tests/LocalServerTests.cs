using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DarkHaven.Launcher.Local;
using Xunit;

namespace DarkHaven.Launcher.Tests;

/// <summary>
/// ЛОКАЛКА without a network or a real server: the CDN manifest, Microsoft's runtime metadata, the
/// config the server is started with, profiles on disk, and a build download that must match its hash.
/// </summary>
public sealed class LocalServerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dh-local-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // --- The CDN manifest ---

    private const string Manifest = """
        {"builds":{
          "aaaaaaaa11111111":{"time":"2026-09-20T09:59:45Z",
            "client":{"url":"http://cdn/c.zip","sha256":"00"},
            "server":{"linux-x64":{"url":"http://cdn/l.zip","sha256":"11","size":5}}},
          "bbbbbbbb22222222":{"time":"2026-09-26T10:00:00Z",
            "server":{"linux-x64":{"url":"http://cdn/l2.zip","sha256":"22"},
                      "win-x64":{"url":"http://cdn/w2.zip","sha256":"AB","size":55029854}}},
          "cccccccc33333333":{"time":"2026-09-25T10:00:00Z",
            "server":{"win-x64":{"url":"http://cdn/w3.zip","sha256":"CD"}}}
        }}
        """;

    [Fact]
    public void Lists_only_builds_with_a_windows_server_newest_first()
    {
        var list = LocalBuildCatalog.Parse(Manifest, "win-x64");

        Assert.Equal(["bbbbbbbb22222222", "cccccccc33333333"], list.Builds.Select(b => b.Version).ToArray());
        Assert.Equal(1, list.WithoutServer); // the linux-only one
        var newest = list.Builds[0];
        Assert.Equal(("http://cdn/w2.zip", "AB", 55029854L, "bbbbbbbb"), (newest.Url, newest.Sha256, newest.Size!.Value, newest.ShortVersion));
        Assert.Null(list.Builds[1].Size);
    }

    [Fact]
    public void Todays_linux_only_cdn_means_no_builds_and_says_why()
    {
        const string today = """{"builds":{"875c455c":{"time":"2026-09-20T09:59:45Z","server":{"linux-x64":{"url":"u","sha256":"s"}}}}}""";
        var list = LocalBuildCatalog.Parse(today, "win-x64");
        Assert.Empty(list.Builds);
        Assert.Equal(1, list.WithoutServer);
    }

    // --- Build download ---

    private sealed class ZipServer(byte[] zip) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
    }

    private static byte[] ServerZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("Robust.Server.exe").Open()))
                w.Write("not really an exe");
            using (var w = new StreamWriter(zip.CreateEntry("Robust.Server.runtimeconfig.json").Open()))
                w.Write("""{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}""");
            using (var w = new StreamWriter(zip.CreateEntry("Resources/Prototypes/x.yml").Open()))
                w.Write("- type: entity");
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task A_build_that_matches_its_hash_is_installed_and_knows_what_dotnet_it_needs()
    {
        var zip = ServerZip();
        var store = new LocalBuildStore(new HttpClient(new ZipServer(zip)), _dir);
        var build = new LocalBuild("bbbbbbbb22222222", DateTimeOffset.UtcNow, "http://cdn/w2.zip", Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant(), zip.Length);

        await store.InstallAsync(build);

        Assert.True(store.IsInstalled(build.Version));
        Assert.Equal(["bbbbbbbb22222222"], store.Installed().ToArray());
        Assert.EndsWith("Robust.Server.exe", store.ServerExecutable(build.Version));
        Assert.Equal(10, store.RequiredDotnetMajor(build.Version));
        Assert.Empty(Directory.GetFiles(_dir)); // no leftover download
    }

    [Fact]
    public async Task A_build_that_doesnt_match_its_hash_is_never_installed()
    {
        var store = new LocalBuildStore(new HttpClient(new ZipServer(ServerZip())), _dir);
        var build = new LocalBuild("bbbbbbbb22222222", DateTimeOffset.UtcNow, "http://cdn/w2.zip", new string('0', 64), null);

        var e = await Assert.ThrowsAsync<InvalidDataException>(() => store.InstallAsync(build));
        Assert.Contains("повреждённой", e.Message);
        Assert.False(store.IsInstalled(build.Version));
        Assert.Empty(store.Installed());
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Builds_sharing_a_folder_prefix_are_not_mistaken_for_each_other()
    {
        var zip = ServerZip();
        var store = new LocalBuildStore(new HttpClient(new ZipServer(zip)), _dir);
        var sha = Convert.ToHexString(SHA256.HashData(zip));
        await store.InstallAsync(new LocalBuild("875c455c3c49aaaa", DateTimeOffset.UtcNow, "http://cdn/a.zip", sha, null));

        Assert.EndsWith("875c455c3c49", store.PathFor("875c455c3c49aaaa")); // 12 characters, not 40
        Assert.True(store.IsInstalled("875c455c3c49aaaa"));
        Assert.False(store.IsInstalled("875c455c3c49bbbb")); // same folder, different build
    }

    [Fact]
    public void A_build_too_deep_for_windows_is_refused_with_a_reason()
    {
        var zipPath = Path.Combine(_dir, "deep.zip");
        Directory.CreateDirectory(_dir);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            zip.CreateEntry("Resources/Locale/ru-RU/" + new string('x', 130) + ".ftl");

        var deep = Path.Combine(_dir, new string('d', 120));
        var e = Assert.Throws<InvalidOperationException>(() => LocalBuildStore.EnsurePathsFit(zipPath, deep, longPathsEnabled: false));
        Assert.Contains("260 символов", e.Message);

        LocalBuildStore.EnsurePathsFit(zipPath, deep, longPathsEnabled: true); // Windows set up for long paths: fine
        LocalBuildStore.EnsurePathsFit(zipPath, Path.Combine(_dir, "b"), longPathsEnabled: false); // short enough: fine
    }

    [Theory]
    [InlineData("875c455c3c4961f63c2d555bfe96743f9a180fb3", "875c455c3c4961f63c2d555bfe96743f9a180fb3")]
    [InlineData("../../evil", ".._.._evil")]
    [InlineData("..", "_")]
    [InlineData("C:\\Windows", "C__Windows")]
    public void Versions_become_safe_folder_names(string version, string folder) =>
        Assert.Equal(folder, LocalBuildStore.Safe(version));

    [Fact]
    public void Reads_the_dotnet_version_from_either_runtimeconfig_shape()
    {
        Assert.Equal(10, LocalBuildStore.ReadDotnetMajor(
            """{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}"""));
        Assert.Equal(9, LocalBuildStore.ReadDotnetMajor(
            """{"runtimeOptions":{"frameworks":[{"name":"Microsoft.AspNetCore.App","version":"9.0.0"},{"name":"Microsoft.NETCore.App","version":"9.0.1"}]}}"""));
        Assert.Null(LocalBuildStore.ReadDotnetMajor("""{"runtimeOptions":{"includedFrameworks":[]}}"""));
    }

    // --- .NET runtime ---

    [Fact]
    public void Picks_the_newest_windows_runtime_zip_from_microsofts_metadata()
    {
        const string releases = """
            {"releases":[
              {"release-version":"10.0.3","runtime":{"version":"10.0.3","files":[
                {"name":"dotnet-runtime-win-x64.exe","rid":"win-x64","url":"https://x/new.exe","hash":"E1"},
                {"name":"dotnet-runtime-linux-x64.tar.gz","rid":"linux-x64","url":"https://x/new.tgz","hash":"L1"},
                {"name":"dotnet-runtime-win-x64.zip","rid":"win-x64","url":"https://x/new.zip","hash":"Z1"}]}},
              {"release-version":"10.0.2","runtime":{"version":"10.0.2","files":[
                {"name":"dotnet-runtime-win-x64.zip","rid":"win-x64","url":"https://x/old.zip","hash":"Z0"}]}}
            ]}
            """;
        Assert.Equal(("https://x/new.zip", "Z1"), DotnetRuntime.PickRuntimeZip(releases, "win-x64"));
    }

    [Fact]
    public void A_runtime_counts_only_with_its_host_and_the_right_major()
    {
        var root = Path.Combine(_dir, "dotnet");
        Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", "9.0.8"));
        Assert.False(DotnetRuntime.HasRuntime(root, 9)); // no host/fxr yet

        Directory.CreateDirectory(Path.Combine(root, "host", "fxr", "9.0.8"));
        Assert.True(DotnetRuntime.HasRuntime(root, 9));
        Assert.False(DotnetRuntime.HasRuntime(root, 10));

        Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", "10.0.0-rc.2.25502.107"));
        Assert.True(DotnetRuntime.HasRuntime(root, 10));
    }

    // --- The server's config ---

    [Fact]
    public void A_local_server_listens_only_on_this_pc_and_stays_off_the_hub()
    {
        var p = new LocalServerProfile { Name = "Тест", Port = 1251, MaxPlayers = 4, Mode = LocalServerMode.Play };
        var toml = LocalServerConfig.Toml(p, @"C:\logs");

        Assert.Contains("port = 1251", toml);
        Assert.Contains("bindto = \"127.0.0.1\"", toml);
        Assert.Contains("bind = \"127.0.0.1:1251\"", toml);
        Assert.Contains("[hub]\nadvertise = false", toml.Replace("\r\n", "\n"));
        Assert.Contains("[auth]\nmode = 0", toml.Replace("\r\n", "\n")); // Optional: works offline too
        Assert.Contains("hostname = \"Локалка: Тест\"", toml);
        Assert.Contains("soft_max_players = 4", toml);
        Assert.Contains("loginlocal = true", toml);
        Assert.Contains("path = \"C:\\\\logs\"", toml);
        Assert.DoesNotContain("presets", toml); // a play server is the normal game
        Assert.DoesNotContain("map =", toml);
    }

    [Fact]
    public void A_server_open_to_others_is_reachable_but_only_for_whitelisted_accounts()
    {
        var p = new LocalServerProfile { Port = 1252, Shared = true };
        var toml = LocalServerConfig.Toml(p, "logs").Replace("\r\n", "\n");

        Assert.Contains("bindto = \"::,0.0.0.0\"", toml);
        Assert.Contains("upnp = true", toml);
        Assert.Contains("bind = \"*:1252\"", toml);
        Assert.Contains("[auth]\nmode = 1", toml); // Required: real accounts only
        Assert.Contains("[whitelist]\nenabled = true", toml);
        Assert.DoesNotContain("127.0.0.1", toml);
    }

    [Theory]
    [InlineData("[INFO] net.upnp: Peer 0.0.0.0:1250: Successfully UPnP port forwarded 1250/udp and 1250/tcp", UpnpState.Forwarded)]
    [InlineData("[WARN] net.upnp: Peer 0.0.0.0:1250: Failed UPnP port forwarding, your server may not be accessible.", UpnpState.Failed)]
    [InlineData("[WARN] net.upnp: Can't UPnP forward: No IPv4-compatible NetPeers available.", UpnpState.Failed)]
    [InlineData("[WARN] net.upnp: UPnP threw an exception: System.Exception", UpnpState.Failed)]
    [InlineData("[INFO] root: Server Version 275.1.0.0 -> Ready", null)]
    public void Reads_the_routers_answer_from_the_server_console(string line, UpnpState? expected) =>
        Assert.Equal(expected, LocalServerHost.ReadUpnp(line));

    [Theory]
    [InlineData("GODWINCH", true)]
    [InlineData("Cadet_Nova_2", true)]
    [InlineData("name; shutdown", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    [InlineData("имя", false)]
    public void Only_real_account_names_reach_the_server_console(string name, bool ok) =>
        Assert.Equal(ok, LocalServerHost.IsUsername(name));

    [Fact]
    public void Mapping_mode_loads_the_games_developer_preset_and_a_chosen_map()
    {
        var p = new LocalServerProfile { Mode = LocalServerMode.Develop, Map = " MyShuttle " };
        var toml = LocalServerConfig.Toml(p, "logs");
        Assert.Contains("presets = \"Build/development\"", toml);
        Assert.Contains("map = \"MyShuttle\"", toml);
    }

    [Fact]
    public void A_name_cant_break_out_of_its_toml_string()
    {
        var p = new LocalServerProfile { Name = "a\"\nport = 1\\" };
        var toml = LocalServerConfig.Toml(p, "logs");
        Assert.Contains("hostname = \"Локалка: a\\\"\\nport = 1\\\\\"", toml);
        Assert.Single(toml.Split('\n'), l => l.StartsWith("port = "));
    }

    [Fact]
    public void Only_well_formed_cvars_are_passed_on()
    {
        var p = new LocalServerProfile
        {
            Cvars = new()
            {
                ["game.lobbyduration"] = "30",
                ["bad name"] = "1",
                ["--config-file"] = "x",
                ["log.level"] = "1\n--cvar evil=1",
            },
        };
        Assert.Equal(["game.lobbyduration=30"], LocalServerConfig.ExtraCvars(p).ToArray());
    }

    // --- Profiles on disk ---

    [Fact]
    public void Profiles_survive_a_restart_and_new_ones_get_a_free_port()
    {
        var store = new LocalServerStore(_dir);
        var a = store.New("Первый", LocalServerMode.Play);
        a.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1); // the clock may not tick between the two
        store.Save(a);
        var b = store.New("Второй", LocalServerMode.Develop);
        store.Save(b);

        Assert.Equal(LocalServerStore.FirstPort, a.Port);
        Assert.Equal(LocalServerStore.FirstPort + 1, b.Port);

        var again = new LocalServerStore(_dir).List();
        Assert.Equal(["Первый", "Второй"], again.Select(p => p.Name).ToArray());
        Assert.Equal(LocalServerMode.Develop, again[1].Mode);
        Assert.True(Directory.Exists(store.DataDirFor(b.Id)));

        store.Delete(a.Id);
        Assert.Equal(["Второй"], store.List().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void A_broken_profile_doesnt_hide_the_others()
    {
        var store = new LocalServerStore(_dir);
        store.Save(store.New("Живой", LocalServerMode.Play));
        var broken = Path.Combine(_dir, "broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "server.json"), "{ не json");

        Assert.Equal(["Живой"], store.List().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void A_port_someone_else_holds_is_reported_busy()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.True(LocalServers.PortBusy(port));
        }
        finally
        {
            listener.Stop();
        }
    }
}
