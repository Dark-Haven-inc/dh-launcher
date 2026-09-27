using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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

    [Theory]
    [InlineData("win", Architecture.X64, "win-x64")]
    [InlineData("linux", Architecture.X64, "linux-x64")]
    [InlineData("linux", Architecture.Arm64, "linux-arm64")]
    public void The_platform_is_named_the_way_the_manifest_names_it(string os, Architecture arch, string rid) =>
        Assert.Equal(rid, LocalBuildCatalog.RidFor(os, arch));

    [Fact]
    public void A_linux_pc_lists_the_linux_servers()
    {
        var list = LocalBuildCatalog.Parse(Manifest, "linux-x64");
        Assert.Equal(["bbbbbbbb22222222", "aaaaaaaa11111111"], list.Builds.Select(b => b.Version).ToArray());
        Assert.Equal("http://cdn/l2.zip", list.Builds[0].Url);
        Assert.Equal(1, list.WithoutServer); // the windows-only one
    }

    // --- Build download ---

    private sealed class ZipServer(byte[] zip) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
    }

    private static byte[] ServerZip(string exe = "Robust.Server.exe")
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry(exe).Open()))
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
        var store = new LocalBuildStore(new HttpClient(new ZipServer(zip)), _dir, "win-x64");
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
        var store = new LocalBuildStore(new HttpClient(new ZipServer(ServerZip())), _dir, "win-x64");
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
        var store = new LocalBuildStore(new HttpClient(new ZipServer(zip)), _dir, "win-x64");
        var sha = Convert.ToHexString(SHA256.HashData(zip));
        await store.InstallAsync(new LocalBuild("875c455c3c49aaaa", DateTimeOffset.UtcNow, "http://cdn/a.zip", sha, null));

        Assert.EndsWith("875c455c3c49", store.PathFor("875c455c3c49aaaa")); // 12 characters, not 40
        Assert.True(store.IsInstalled("875c455c3c49aaaa"));
        Assert.False(store.IsInstalled("875c455c3c49bbbb")); // same folder, different build
    }

    [Fact]
    public async Task A_linux_build_starts_its_server_without_an_extension()
    {
        var zip = ServerZip("Robust.Server");
        var store = new LocalBuildStore(new HttpClient(new ZipServer(zip)), _dir, "linux-x64");
        var build = new LocalBuild("dddddddd44444444", DateTimeOffset.UtcNow, "http://cdn/l.zip", Convert.ToHexString(SHA256.HashData(zip)), zip.Length);

        await store.InstallAsync(build);

        Assert.True(store.IsInstalled(build.Version));
        var exe = store.ServerExecutable(build.Version);
        Assert.EndsWith("Robust.Server", exe);
        if (!OperatingSystem.IsWindows()) // the zip carries no permissions; the store has to add them
            Assert.True(File.GetUnixFileMode(exe!).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task A_windows_build_on_linux_counts_as_not_installed()
    {
        // What launcher 0.3.9 left on Linux PCs: the Windows server, under the right version.
        var zip = ServerZip();
        await new LocalBuildStore(new HttpClient(new ZipServer(zip)), _dir, "win-x64")
            .InstallAsync(new LocalBuild("eeeeeeee55555555", DateTimeOffset.UtcNow, "http://cdn/w.zip", Convert.ToHexString(SHA256.HashData(zip)), null));

        var linux = new LocalBuildStore(new HttpClient(new ZipServer(zip)), _dir, "linux-x64");
        Assert.False(linux.IsInstalled("eeeeeeee55555555"));
        Assert.Null(linux.ServerExecutable("eeeeeeee55555555"));
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
    public void Picks_the_newest_runtime_archive_for_the_system_from_microsofts_metadata()
    {
        const string releases = """
            {"releases":[
              {"release-version":"10.0.3","runtime":{"version":"10.0.3","files":[
                {"name":"dotnet-apphost-pack-linux-x64.tar.gz","rid":"linux-x64","url":"https://x/apphost.tgz","hash":"A1"},
                {"name":"dotnet-apphost-pack-win-x64.zip","rid":"win-x64","url":"https://x/apphost.zip","hash":"A2"},
                {"name":"dotnet-runtime-win-x64.exe","rid":"win-x64","url":"https://x/new.exe","hash":"E1"},
                {"name":"dotnet-runtime-linux-x64.tar.gz","rid":"linux-x64","url":"https://x/new.tgz","hash":"L1"},
                {"name":"dotnet-runtime-win-x64.zip","rid":"win-x64","url":"https://x/new.zip","hash":"Z1"}]}},
              {"release-version":"10.0.2","runtime":{"version":"10.0.2","files":[
                {"name":"dotnet-runtime-win-x64.zip","rid":"win-x64","url":"https://x/old.zip","hash":"Z0"}]}}
            ]}
            """;
        Assert.Equal(("https://x/new.zip", "Z1"), DotnetRuntime.PickRuntimeArchive(releases, "win-x64"));
        Assert.Equal(("https://x/new.tgz", "L1"), DotnetRuntime.PickRuntimeArchive(releases, "linux-x64"));
    }

    [Fact]
    public void A_runtime_counts_only_with_its_host_and_the_right_major()
    {
        var root = Path.Combine(_dir, "dotnet");
        Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", "9.0.8"));
        Assert.False(DotnetRuntime.HasRuntime(root, 9)); // no host/fxr yet

        var fxr = Path.Combine(root, "host", "fxr", "9.0.8");
        Directory.CreateDirectory(fxr);
        File.WriteAllText(Path.Combine(fxr, OtherSystemsHostFxr), "");
        Assert.False(DotnetRuntime.HasRuntime(root, 9)); // another system's runtime can't run here

        File.WriteAllText(Path.Combine(fxr, DotnetRuntime.HostFxr), "");
        Assert.True(DotnetRuntime.HasRuntime(root, 9));
        Assert.False(DotnetRuntime.HasRuntime(root, 10));

        Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", "10.0.0-rc.2.25502.107"));
        Assert.True(DotnetRuntime.HasRuntime(root, 10));
    }

    private static string OtherSystemsHostFxr => DotnetRuntime.HostFxr == "hostfxr.dll" ? "libhostfxr.so" : "hostfxr.dll";

    /// <summary>Microsoft's CDN: releases.json and one runtime archive for this system.</summary>
    private sealed class MicrosoftCdn(byte[] archive, string archiveUrl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var url = request.RequestUri!.ToString();
            var rid = LocalBuildCatalog.Rid;
            var name = "dotnet-runtime-" + rid + (archiveUrl.EndsWith(".zip") ? ".zip" : ".tar.gz");
            var hash = Convert.ToHexString(SHA512.HashData(archive));
            var body = url.EndsWith("releases.json")
                ? System.Text.Encoding.UTF8.GetBytes(
                    $$$"""{"releases":[{"runtime":{"files":[{"name":"{{{name}}}","rid":"{{{rid}}}","url":"{{{archiveUrl}}}","hash":"{{{hash}}}"}]}}]}""")
                : url == archiveUrl ? archive : throw new InvalidOperationException($"unexpected request {url}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    /// <summary>A pretend runtime in the shape Microsoft ships for this system: a zip on Windows, a tar.gz elsewhere.</summary>
    private static (byte[] Archive, string Url) RuntimeArchive(string version)
    {
        const UnixFileMode executable = (UnixFileMode)0b111_101_101, plain = (UnixFileMode)0b110_100_100;
        (string Path, UnixFileMode Mode)[] files =
        [
            ("dotnet", executable),
            ($"host/fxr/{version}/{DotnetRuntime.HostFxr}", plain),
            ($"shared/Microsoft.NETCore.App/{version}/System.Private.CoreLib.dll", plain),
        ];
        using var ms = new MemoryStream();
        if (LocalBuildCatalog.Rid.StartsWith("win-"))
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (path, _) in files)
                    using (var w = new StreamWriter(zip.CreateEntry(path).Open()))
                        w.Write("x");
            return (ms.ToArray(), "https://ms/dotnet-runtime.zip");
        }

        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz))
            foreach (var (path, mode) in files)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { Mode = mode, DataStream = new MemoryStream("x"u8.ToArray()) });
        return (ms.ToArray(), "https://ms/dotnet-runtime.tar.gz");
    }

    [Fact]
    public async Task Without_a_runtime_it_downloads_microsofts_for_this_system()
    {
        var root = Path.Combine(_dir, "dotnet");
        // Launcher 0.3.9 on Linux unpacked the Windows runtime here: it has to go, not mix with the new one.
        var stray = Path.Combine(root, "shared", "Microsoft.NETCore.App", "10.0.0");
        Directory.CreateDirectory(stray);
        Directory.CreateDirectory(Path.Combine(root, "host", "fxr", "10.0.0"));
        File.WriteAllText(Path.Combine(root, "host", "fxr", "10.0.0", OtherSystemsHostFxr), "");

        var (archive, url) = RuntimeArchive("99.0.1");
        var runtime = new DotnetRuntime(new HttpClient(new MicrosoftCdn(archive, url)), root, trySystem: false);

        Assert.Equal(root, await runtime.EnsureAsync(99));
        Assert.True(DotnetRuntime.HasRuntime(root, 99));
        Assert.False(Directory.Exists(stray));
        Assert.Empty(Directory.GetFiles(root, ".download-*")); // no leftover archive
        if (!OperatingSystem.IsWindows()) // the tar's permissions survive: the host must stay executable
            Assert.True(File.GetUnixFileMode(Path.Combine(root, "dotnet")).HasFlag(UnixFileMode.UserExecute));

        Assert.Equal(root, await runtime.EnsureAsync(99)); // second time: already there, no download
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
    public void Sharing_is_off_for_now_so_every_server_stays_on_this_pc()
    {
        Assert.False(LocalServers.SharingEnabled);
        var toml = LocalServerConfig.Toml(new LocalServerProfile { Shared = true }, "logs");
        Assert.Contains("bindto = \"127.0.0.1\"", toml);
        Assert.DoesNotContain("upnp", toml);
    }

    [Fact]
    public void A_server_open_to_others_is_reachable_but_only_for_whitelisted_accounts()
    {
        var p = new LocalServerProfile { Port = 1252, Shared = true };
        var toml = LocalServerConfig.Toml(p, "logs", shared: true).Replace("\r\n", "\n");

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
