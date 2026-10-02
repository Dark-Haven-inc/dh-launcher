using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Content;
using DarkHaven.Launcher.Engine;
using DarkHaven.Launcher.Models;
using DarkHaven.Launcher.Security;
using DarkHaven.Launcher.Update;
using Xunit;

namespace DarkHaven.Launcher.Tests;

/// <summary>
/// What a player's (or a previous game's) environment might carry: none of it may reach the game, but a variable that
/// is not about the launch must. Names only these tests use, so the rest of the suite does not notice.
/// <para>
/// Set in the environment the guard actually reads. On Unix <see cref="Environment.SetEnvironmentVariable(string, string)"/>
/// changes only .NET's own copy, which dh_guard never sees (it inherits libc's), so a variable set that way would be
/// "stripped" whatever the guard does.
/// </para>
/// </summary>
public sealed partial class LauncherEnvironment : IDisposable
{
    public const string Kept = "DH_TEST_KEPT";

    private static readonly (string Name, string Value)[] Variables =
    [
        ("ROBUST_MODULE_DH_TEST_STRAY", "/tmp/stray-module"),
        ("SS14_DISABLE_SIGNING", "true"),
        ("DH_LAUNCH_BROKER", "unix:/tmp/stale.sock"),
        ("DH_LAUNCH_PROOF", "stale"),
        (Kept, "passed through"),
    ];

    public LauncherEnvironment()
    {
        foreach (var (name, value) in Variables)
            Set(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, _) in Variables)
            Set(name, null);
    }

    private static void Set(string name, string? value)
    {
        if (OperatingSystem.IsWindows())
        {
            Environment.SetEnvironmentVariable(name, value); // the process environment itself there
            return;
        }
        if ((value is null ? unsetenv(name) : setenv(name, value, 1)) != 0)
            throw new InvalidOperationException($"could not set {name} in the process environment");
    }

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int setenv(string name, string value, int overwrite);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int unsetenv(string name);
}

/// <summary>
/// The launch end to end through the development guard, with a shell script standing in for the loader: it prints its
/// arguments and the environment variables the launch is about, one per line, and exits with a code of its choosing.
/// </summary>
public sealed class GameLauncherTests : IDisposable, IClassFixture<LauncherEnvironment>
{
    private const string EngineVersion = "275.1.0";

    private static readonly string[] Watched =
    [
        "SS14_LOADER_CONTENT_DB", "SS14_LOADER_CONTENT_VERSION", "SS14_LAUNCHER_PATH",
        "DOTNET_MULTILEVEL_LOOKUP", "DOTNET_TieredPGO", "DOTNET_ReadyToRun",
        "DOTNET_EnableDiagnostics_IPC", "DOTNET_EnableDiagnostics_Debugger", "DOTNET_EnableDiagnostics_Profiler",
        "ROBUST_AUTH_TOKEN", "ROBUST_AUTH_USERID", "ROBUST_AUTH_PUBKEY", "ROBUST_AUTH_SERVER",
        "ROBUST_MODULE_ROBUST_CLIENT_WEBVIEW", "ROBUST_MODULE_DH_TEST_STRAY",
        "DH_LAUNCH_BROKER", "DH_LAUNCH_PROOF", "SS14_DISABLE_SIGNING", LauncherEnvironment.Kept,
    ];

    private static readonly Guid User = Guid.Parse("ABCDEF01-2345-6789-ABCD-EF0123456789");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dh-game-{Guid.NewGuid():N}");
    private readonly EngineManager _engines;

    public GameLauncherTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "engines"));
        File.WriteAllText(Path.Combine(_dir, "engines", $"{EngineVersion}.zip.sig"), "sha256:00ff");
        _engines = new EngineManager(new HttpClient(), Path.Combine(_dir, "engines"), Path.Combine(_dir, "modules"),
            new EngineSignature(LauncherPaths.SigningKeyPath));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private string Loader(string body)
    {
        var path = Path.Combine(_dir, $"loader-{Guid.NewGuid():N}.sh");
        var env = string.Join(' ', Watched);
        File.WriteAllText(path, $$"""
            #!/bin/sh
            for a in "$@"; do printf 'arg %s\n' "$a"; done
            for v in {{env}}; do eval "printf 'env %s=%s\n' \"\$v\" \"\${$v-(unset)}\""; done
            {{body}}
            """.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private GameLauncher Launcher(string loader) =>
        new(loader, "/opt/dh/Assets/signing_key", _engines, Path.Combine(_dir, "content.db"));

    private static ResolvedServerInfo Server(AuthMode auth = AuthMode.Required) =>
        new(new Uri("ss14://game.example:1212"), new Uri("http://game.example:1212/info"),
            new Uri("udp://game.example:1212"),
            new ServerInfo
            {
                Auth = new ServerAuthInfo { Mode = auth, PublicKey = "c2VydmVyLWtleQ==" },
                Build = new ServerBuildInfo
                {
                    EngineVersion = EngineVersion,
                    Version = "",
                    ForkId = "frontier",
                    ManifestHash = "ABCD",
                    ManifestDownloadUrl = "https://cdn.example/manifest",
                },
            });

    private static LaunchManifest Manifest => new(42, EngineVersion, [("Robust.Client.WebView", "1.2.3")]);

    private static GameAccount Account => new("Player", "secret-token", User);

    private static async Task<(int Code, List<string> Lines)> RunAsync(GameProcess game)
    {
        var lines = new ConcurrentQueue<string>();
        game.BeginOutputReadLine(lines.Enqueue);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await game.WaitForExitAsync(timeout.Token);
        return (game.ExitCode, lines.ToList());
    }

    private static List<string> Args(List<string> lines) =>
        lines.Where(l => l.StartsWith("arg ")).Select(l => l[4..]).ToList();

    private static Dictionary<string, string> Env(List<string> lines) =>
        lines.Where(l => l.StartsWith("env ")).Select(l => l[4..].Split('=', 2)).ToDictionary(p => p[0], p => p[1]);

    [DevGuardLinuxFact]
    public async Task TheLoaderGetsTheReferenceCommandLineAndEnvironment()
    {
        var launcher = Launcher(Loader("echo 'to stderr' >&2\nexit 7"));
        using var game = launcher.Start(Server(), Manifest, Account, compatMode: true, redirectOutput: true,
            extraCvars: ["net.logging=true"]);

        Assert.True(game.Id > 0);
        Assert.True(game.RedirectsOutput);
        Assert.Equal(GuardBrokerState.NotRequested, game.BrokerState);

        var (code, lines) = await RunAsync(game);
        Assert.Equal(7, code);
        Assert.True(game.HasExited);
        Assert.Equal(0, game.Signed);
        Assert.Contains("to stderr", lines);

        var server = Server();
        Assert.Equal(new[]
        {
            Path.Combine(_dir, "engines", $"{EngineVersion}.zip"), "sha256:00ff", "/opt/dh/Assets/signing_key",
            "--username", "Player",
            "--cvar", "display.compat=true",
            "--cvar", "launch.launcher=true",
            "--cvar", "net.logging=true",
            "--launcher",
            "--connect-address", server.ConnectAddress.ToString(),
            "--ss14-address", server.ServerUri.ToString(),
            "--cvar", $"build.engine_version={EngineVersion}",
            "--cvar", "build.fork_id=frontier",
            "--cvar", "build.manifest_hash=ABCD",
            "--cvar", "build.manifest_download_url=https://cdn.example/manifest",
        }, Args(lines));

        var env = Env(lines);
        Assert.Equal(Path.Combine(_dir, "content.db"), env["SS14_LOADER_CONTENT_DB"]);
        Assert.Equal("42", env["SS14_LOADER_CONTENT_VERSION"]);
        Assert.Equal(LauncherInfo.ExecutablePath ?? "", env["SS14_LAUNCHER_PATH"]);
        Assert.Equal("0", env["DOTNET_MULTILEVEL_LOOKUP"]);
        Assert.Equal("1", env["DOTNET_TieredPGO"]);
        Assert.Equal("0", env["DOTNET_ReadyToRun"]);
        Assert.Equal("0", env["DOTNET_EnableDiagnostics_IPC"]);
        Assert.Equal("0", env["DOTNET_EnableDiagnostics_Debugger"]);
        Assert.Equal("0", env["DOTNET_EnableDiagnostics_Profiler"]);
        Assert.Equal("secret-token", env["ROBUST_AUTH_TOKEN"]);
        Assert.Equal("abcdef01-2345-6789-abcd-ef0123456789", env["ROBUST_AUTH_USERID"]);
        Assert.Equal("c2VydmVyLWtleQ==", env["ROBUST_AUTH_PUBKEY"]);
        Assert.Equal("https://auth.spacestation14.com/", env["ROBUST_AUTH_SERVER"]);
        Assert.Equal(Path.Combine(_dir, "modules", "Robust.Client.WebView", "1.2.3"),
            env["ROBUST_MODULE_ROBUST_CLIENT_WEBVIEW"]);

        // The inherited environment reaches the game (LauncherEnvironment set it where the guard reads it), minus what
        // is stripped from it; a development guard runs no broker and signs no v1 proof.
        Assert.Equal("passed through", env[LauncherEnvironment.Kept]);
        Assert.Equal("(unset)", env["ROBUST_MODULE_DH_TEST_STRAY"]);
        Assert.Equal("(unset)", env["SS14_DISABLE_SIGNING"]);
        Assert.Equal("(unset)", env["DH_LAUNCH_BROKER"]);
        Assert.Equal("(unset)", env["DH_LAUNCH_PROOF"]);
    }

    [DevGuardLinuxFact]
    public async Task AGuestGetsTheDefaultNameAndNoAuth()
    {
        using var game = Launcher(Loader("exit 0")).Start(Server(AuthMode.Optional), Manifest, account: null,
            redirectOutput: true);
        var (code, lines) = await RunAsync(game);

        Assert.Equal(0, code);
        var args = Args(lines);
        Assert.Equal("JoeGenero", args[args.IndexOf("--username") + 1]);
        Assert.Contains("display.compat=false", args);
        var env = Env(lines);
        Assert.Equal("(unset)", env["ROBUST_AUTH_TOKEN"]);
        Assert.Equal("(unset)", env["ROBUST_AUTH_USERID"]);
    }

    [DevGuardLinuxFact]
    public async Task WithAuthDisabledTheAccountNameGoesButNotItsToken()
    {
        using var game = Launcher(Loader("exit 0")).Start(Server(AuthMode.Disabled), Manifest, Account,
            redirectOutput: true);
        var (_, lines) = await RunAsync(game);

        var args = Args(lines);
        Assert.Equal("Player", args[args.IndexOf("--username") + 1]);
        Assert.Equal("(unset)", Env(lines)["ROBUST_AUTH_TOKEN"]);
        Assert.Equal(GuardBrokerState.NotRequested, game.BrokerState);
    }

    [DevGuardLinuxFact]
    public async Task KillEndsTheGame()
    {
        using var game = Launcher(Loader("echo started\nexec sleep 30")).Start(Server(), Manifest, Account,
            redirectOutput: true);
        var started = new TaskCompletionSource();
        var exited = new TaskCompletionSource();
        game.Exited += (_, _) => exited.TrySetResult();
        game.BeginOutputReadLine(line =>
        {
            if (line == "started")
                started.TrySetResult();
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(game.HasExited);
        Assert.Throws<InvalidOperationException>(() => game.ExitCode);

        game.Kill();
        await game.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(128 + 9, game.ExitCode); // SIGKILL, as Process.ExitCode reports it
        game.Kill(); // gone already: nothing to do
    }

    [DevGuardLinuxFact]
    public async Task WithoutRedirectTheGameSharesOurOutput()
    {
        using var game = Launcher(Loader("exit 3")).Start(Server(), Manifest, Account);
        Assert.False(game.RedirectsOutput);
        Assert.Throws<InvalidOperationException>(() => game.BeginOutputReadLine(_ => { }));

        await game.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(3, game.ExitCode);
    }

    [DevGuardLinuxFact]
    public void TheGuardRefusesWhatItDoesNotAllow()
    {
        var launcher = Launcher(Loader("exit 0"));

        var cvar = Assert.Throws<GuardException>(() =>
            launcher.Start(Server(), Manifest, Account, extraCvars: ["sys.evil=1"]));
        Assert.Equal(GuardStatus.Request, cvar.Status);
        Assert.Contains("sys.evil=1", cvar.Message);

        var module = Assert.Throws<GuardException>(() =>
            launcher.Start(Server(), new LaunchManifest(42, EngineVersion, [("..", "1")]), Account));
        Assert.Equal(GuardStatus.Request, module.Status);
    }

    [DevGuardLinuxFact]
    public void AMissingLoaderIsAnOsError()
    {
        var e = Assert.Throws<GuardException>(() =>
            Launcher(Path.Combine(_dir, "no-such-loader")).Start(Server(), Manifest, Account));
        Assert.Equal(GuardStatus.Os, e.Status);
        Assert.Contains("no-such-loader", e.Message);
    }

    [DevGuardLinuxFact]
    public async Task DisposingReleasesTheHandleButNotTheGame()
    {
        var marker = Path.Combine(_dir, "finished");
        var game = Launcher(Loader($"sleep 1\ntouch '{marker}'")).Start(Server(), Manifest, Account);
        game.Dispose();
        Assert.Throws<ObjectDisposedException>(() => game.Kill());

        for (var i = 0; i < 100 && !File.Exists(marker); i++)
            await Task.Delay(100);
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void TheRequestCarriesTheAccountOnlyWhenTheServerUsesAuth()
    {
        var launcher = Launcher("/opt/dh/loader/DarkHaven.Loader");

        var required = launcher.CreateRequest(Server(), Manifest, Account, false, false, null);
        Assert.Equal("Player", required.Username);
        Assert.Equal(new GuardAccount("Player", "secret-token", User, "c2VydmVyLWtleQ=="), required.Account);
        Assert.Equal(new[] { new GuardModule("Robust.Client.WebView", "1.2.3") }, required.Modules);
        Assert.Empty(required.ExtraCvars);

        var disabled = launcher.CreateRequest(Server(AuthMode.Disabled), Manifest, Account, false, false, null);
        Assert.Equal("Player", disabled.Username);
        Assert.Null(disabled.Account);

        var guest = launcher.CreateRequest(Server(), Manifest, null, false, false, null);
        Assert.Null(guest.Username);
        Assert.Null(guest.Account);
    }
}
