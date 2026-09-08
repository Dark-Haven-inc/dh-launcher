using System.Text.Json;
using DarkHaven.ContentDb;
using DarkHaven.Launcher;
using DarkHaven.Launcher.Accounts;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Content;
using DarkHaven.Launcher.Data;
using DarkHaven.Launcher.Engine;
using DarkHaven.Launcher.Models;
using DarkHaven.Launcher.Servers;
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
        case "login":
            await Login(positional.ElementAtOrDefault(1));
            break;
        case "accounts":
            await ShowAccounts();
            break;
        case "logout":
            await Logout(positional.ElementAtOrDefault(1));
            break;
        case "servers":
            await ShowServers();
            break;
        case "regions":
            await ShowRegions();
            break;
        default:
            Log.Information("Dark Haven Launcher — dev CLI (data dir: {Dir})", LauncherPaths.DataDir);
            Log.Information("  probe <ss14://addr> [--hub] [--via-hub]   fetch a server's /info");
            Log.Information("  update <ss14://addr>                      download that server's content + engine");
            Log.Information("  connect <ss14://addr> [--guest]           update, then launch the client");
            Log.Information("  login <username> [--password X | env DH_PASSWORD] [--tfa X]");
            Log.Information("  accounts                                  list + refresh stored accounts");
            Log.Information("  logout <username>");
            Log.Information("  servers [--search X] [--rp low,med] [--lang en] [--no-empty]");
            Log.Information("  regions                                   Dark Haven sector, live");
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
    var engines = new EngineManager(http, LauncherPaths.EnginesDir, LauncherPaths.ModulesDir, signing, LocateBundledEngines());
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

    GameAccount? gameAccount = null;
    if (!flags.Contains("--guest"))
    {
        var accounts = AccountManagerFromDisk();
        accounts.Load();
        var active = accounts.Active ?? accounts.Accounts.FirstOrDefault();
        if (active is null)
        {
            if (resolved.Info.Auth.Mode == AuthMode.Required)
            {
                Log.Error("Server requires auth but no account is logged in. Run: dhlauncher login <username>");
                return;
            }
            Log.Warning("No account — connecting as guest");
        }
        else
        {
            gameAccount = await accounts.ToGameAccountAsync(active);
            if (gameAccount is null)
            {
                Log.Error("Account {User} token expired — run: dhlauncher login {User}", active.Username, active.Username);
                if (resolved.Info.Auth.Mode == AuthMode.Required) return;
            }
            else
            {
                Log.Information("Connecting as {User}", gameAccount.Username);
            }
        }
    }

    var loader = LocateLoader();
    Log.Information("Launching client via {Loader}", loader);
    var game = new GameLauncher(loader, LocateSigningKey(), engines, LauncherPaths.ContentDbPath);
    var extraCvars = flags.Contains("--net-debug") ? new[] { "net.logging=true" } : null;
    var proc = game.Start(resolved, manifest, gameAccount, compatMode: flags.Contains("--compat"), redirectOutput: false, extraCvars: extraCvars);
    Log.Information("Client PID {Pid} — waiting for exit", proc.Id);
    await proc.WaitForExitAsync();
    Log.Information("Client exited with code {Code}", proc.ExitCode);
}

AccountManager AccountManagerFromDisk()
{
    var settings = new SettingsDatabase(LauncherPaths.SettingsDbPath);
    return new AccountManager(settings, new AuthApi(http));
}

async Task Login(string? username)
{
    if (username is null) { Log.Error("usage: login <username>"); return; }
    var password = GetFlagValue("--password") ?? Environment.GetEnvironmentVariable("DH_PASSWORD");
    if (string.IsNullOrEmpty(password)) { Log.Error("no password (--password or DH_PASSWORD)"); return; }

    LauncherPaths.EnsureDirectories();
    var accounts = AccountManagerFromDisk();
    accounts.Load();

    var result = await accounts.LoginAsync(username, password, GetFlagValue("--tfa"));
    if (result.IsSuccess)
        Log.Information("Logged in as {User}", result.Login!.Username);
    else
        Log.Error("Login failed ({Code}): {Errors}", result.DenyCode, string.Join("; ", result.Errors));
}

async Task ShowAccounts()
{
    var accounts = AccountManagerFromDisk();
    accounts.Load();
    await accounts.RefreshAllAsync();
    if (accounts.Accounts.Count == 0) { Log.Information("(no accounts)"); return; }
    foreach (var a in accounts.Accounts)
        Log.Information("  {Active} {User}  {Id}  [{Status}] expires {Expires:u}",
            accounts.Active?.UserId == a.UserId ? "*" : " ", a.Username, a.UserId, a.Status, a.Stored.Expires);
}

async Task Logout(string? username)
{
    if (username is null) { Log.Error("usage: logout <username>"); return; }
    var accounts = AccountManagerFromDisk();
    accounts.Load();
    var acc = accounts.Accounts.FirstOrDefault(a => a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
    if (acc is null) { Log.Error("no such account"); return; }
    await accounts.LogoutAsync(acc);
    Log.Information("Logged out {User}", username);
}

string? GetFlagValue(string name)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

async Task ShowServers()
{
    var mgr = new ServerListManager(new HubApi(http));
    await mgr.RefreshAsync();

    var filter = new ServerFilter { Search = GetFlagValue("--search") ?? "", HideEmpty = flags.Contains("--no-empty") };
    foreach (var v in (GetFlagValue("--lang") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        filter.Languages.Add(v);
    foreach (var v in (GetFlagValue("--rp") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        filter.RolePlay.Add(v);

    var shown = filter.Apply(mgr.Servers).ToList();
    Log.Information("{Shown}/{Total} servers", shown.Count, mgr.Servers.Count);
    foreach (var s in shown.Take(40))
        Log.Information("  {P,4}p  {Name,-55}  {Addr}", s.Players, Trunc(s.DisplayName, 55), s.Address);

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}

async Task ShowRegions()
{
    var regions = new DhRegions(http, LauncherPaths.RegionsJsonPath);
    await regions.LoadAsync();
    var entries = await regions.PollAsync();
    Log.Information("Dark Haven Sector — {Count} region(s)", entries.Count);
    foreach (var e in entries)
        Log.Information("  [{State,-7}] {P,3}p  {Name}  ({Addr})", e.Reachability, e.Players, e.DisplayName, e.Address);
}

static string LocateSigningKey()
{
    if (File.Exists(LauncherPaths.SigningKeyPath)) return LauncherPaths.SigningKeyPath;
    var repoKey = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "DarkHaven.Launcher", "Assets", "signing_key");
    if (File.Exists(repoKey)) return Path.GetFullPath(repoKey);
    throw new FileNotFoundException("signing_key not found; expected next to the launcher assets");
}

static string? LocateBundledEngines()
{
    var here = AppContext.BaseDirectory;
    foreach (var candidate in new[]
             {
                 Path.Combine(here, "bundled-engines"),
                 Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", "..", "src", "DarkHaven.App", "bundled-engines")),
             })
    {
        if (File.Exists(Path.Combine(candidate, "manifest.json")))
            return candidate;
    }
    return null;
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
