using System.Text.Json;
using DarkHaven.ContentDb;
using DarkHaven.Launcher;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Content;
using DarkHaven.Launcher.Engine;
using DarkHaven.Launcher.Models;
using DarkHaven.Launcher.Update;
using Serilog;
using Serilog.Events;

var flags = args.Where(a => a.StartsWith('-')).ToHashSet();
var positional = args.Where(a => !a.StartsWith('-')).ToArray();
var command = positional.FirstOrDefault();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(flags.Contains("-v") || flags.Contains("--verbose") ? LogEventLevel.Debug : LogEventLevel.Information)
    .WriteTo.Console(outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

using var http = new HttpClient
{
    Timeout = TimeSpan.FromMinutes(10),
    DefaultRequestHeaders = { { "User-Agent", "DarkHavenLauncher/0.0 (dev cli)" } },
};

var jsonDump = new JsonSerializerOptions { WriteIndented = true };

try
{
    switch (command)
    {
        case "probe":
            await Probe(positional.ElementAtOrDefault(1));
            break;
        case "update":
            await Update(positional.ElementAtOrDefault(1), launch: false);
            break;
        case "connect":
            await Update(positional.ElementAtOrDefault(1), launch: true);
            break;
        default:
            Log.Information("Dark Haven Launcher — dev CLI (data dir: {Dir})", LauncherPaths.DataDir);
            Log.Information("  probe <ss14://addr> [--hub] [--via-hub]   fetch a server's /info");
            Log.Information("  update <ss14://addr>                      download that server's content + engine");
            Log.Information("  connect <ss14://addr>                     update, then launch the client (guest)");
            Log.Information("  -v for debug logging");
            break;
    }
}
catch (Exception e)
{
    Log.Error(e, "Command failed");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;

async Task Probe(string? target)
{
    if (flags.Contains("--hub"))
    {
        var servers = await new HubApi(http).GetServersAsync();
        Log.Information("Hub: {Count} servers", servers.Count);
        foreach (var s in servers.Take(15))
            Log.Information("  {P,3}p  {Name}  {Addr}", s.StatusData?.Players ?? -1, s.StatusData?.Name, s.Address);
        if (target is null) return;
    }

    if (target is null) { Log.Error("need an address"); return; }

    var resolved = flags.Contains("--via-hub")
        ? ViaHub(await new HubApi(http).GetServerInfoAsync(target), target)
        : await new ServerInfoApi(http).GetAsync(target);

    var b = resolved.Info.Build;
    Log.Information("connect={Addr}  auth={Auth}  engine={Engine}  fork={Fork}/{Ver}  acz={Acz}",
        resolved.ConnectAddress, resolved.Info.Auth.Mode, b?.EngineVersion, b?.ForkId, b?.Version, b?.Acz);
    Console.WriteLine(JsonSerializer.Serialize(resolved.Info, jsonDump));

    static ResolvedServerInfo ViaHub(ServerInfo info, string addr)
    {
        var uri = Ss14Address.Parse(addr);
        return new ResolvedServerInfo(uri, Ss14Address.InfoAddress(uri),
            string.IsNullOrEmpty(info.ConnectAddress) ? Ss14Address.DeriveConnectAddress(uri) : new Uri(info.ConnectAddress),
            info);
    }
}

async Task Update(string? target, bool launch)
{
    if (target is null) { Log.Error("need an address"); return; }

    LauncherPaths.EnsureDirectories();

    var resolved = await new ServerInfoApi(http).GetAsync(target);
    var build = resolved.Info.Build ?? throw new InvalidOperationException("server provided no build info");
    Log.Information("Target: {Fork}/{Ver} engine {Engine} (acz={Acz})", build.ForkId, build.Version, build.EngineVersion, build.Acz);

    var contentDb = new ContentDatabase(LauncherPaths.ContentDbPath);
    var signing = new EngineSignature(LocateSigningKey());
    var engines = new EngineManager(http, LauncherPaths.EnginesDir, LauncherPaths.ModulesDir, signing);
    var downloader = new ManifestDownloader(http);
    var updater = new ContentUpdater(contentDb, downloader, engines);

    var lastPct = -1;
    void Progress(long done, long total, string unit)
    {
        if (total <= 0) return;
        var pct = (int)(done * 100 / total);
        if (pct != lastPct && pct % 5 == 0) { Log.Information("  {Pct,3}%  ({Done}/{Total} {Unit})", pct, done, total, unit); lastPct = pct; }
    }

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var manifest = await updater.UpdateAsync(build, Progress);
    Log.Information("Update done in {Elapsed}: version {Id}, engine {Engine}, modules [{Mods}]",
        sw.Elapsed, manifest.VersionId, manifest.EngineVersion, string.Join(", ", manifest.Modules.Select(m => m.Name)));

    if (!launch) return;

    var loader = LocateLoader();
    Log.Information("Launching client via {Loader}", loader);
    var game = new GameLauncher(loader, LocateSigningKey(), engines, LauncherPaths.ContentDbPath);
    var proc = game.Start(resolved, manifest, account: null, compatMode: flags.Contains("--compat"), redirectOutput: false);
    Log.Information("Client PID {Pid} — waiting for exit", proc.Id);
    await proc.WaitForExitAsync();
    Log.Information("Client exited with code {Code}", proc.ExitCode);
}

static string LocateSigningKey()
{
    if (File.Exists(LauncherPaths.SigningKeyPath)) return LauncherPaths.SigningKeyPath;
    var repoKey = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "DarkHaven.Launcher", "Assets", "signing_key");
    if (File.Exists(repoKey)) return Path.GetFullPath(repoKey);
    throw new FileNotFoundException("signing_key not found; expected next to the launcher assets");
}

static string LocateLoader()
{
    var here = AppContext.BaseDirectory;
    foreach (var cfg in new[] { "Release", "Debug" })
    {
        var p = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", "..",
            "src", "DarkHaven.Loader", "bin", cfg, "net10.0", "DarkHaven.Loader.exe"));
        if (File.Exists(p)) return p;
    }
    throw new FileNotFoundException("DarkHaven.Loader.exe not built — run: dotnet build src/DarkHaven.Loader");
}
