using System.Text.Json;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Models;
using Serilog;
using Serilog.Events;

var minLevel = args.Contains("-v") || args.Contains("--verbose")
    ? LogEventLevel.Debug
    : LogEventLevel.Information;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(minLevel)
    .WriteTo.Console(outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var positional = args.Where(a => !a.StartsWith('-')).ToArray();
var command = positional.FirstOrDefault();

using var http = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(30),
    DefaultRequestHeaders = { { "User-Agent", "DarkHavenLauncher/0.0 (probe)" } },
};

var jsonDump = new JsonSerializerOptions { WriteIndented = true };

try
{
    switch (command)
    {
        case "probe":
        {
            var target = positional.ElementAtOrDefault(1);
            if (args.Contains("--hub"))
            {
                var hub = new HubApi(http);
                var servers = await hub.GetServersAsync();
                Log.Information("Hub returned {Count} servers", servers.Count);
                foreach (var s in servers.Take(15))
                    Log.Information("  {Players,3}p  {Name}  {Address}",
                        s.StatusData?.Players ?? -1, s.StatusData?.Name ?? "?", s.Address);
                if (target is null)
                    break;
            }

            if (target is null)
            {
                Log.Error("Usage: dhlauncher probe <ss14://addr> [--hub] [--via-hub]");
                return 2;
            }

            var infoApi = new ServerInfoApi(http);
            ResolvedServerInfo resolved;

            if (args.Contains("--via-hub"))
            {
                // The hub matches ?url= against its listed address string verbatim, so pass it raw.
                var hub = new HubApi(http);
                var uri = Ss14Address.Parse(target);
                var info = await hub.GetServerInfoAsync(target);
                resolved = new ResolvedServerInfo(uri, Ss14Address.InfoAddress(uri),
                    string.IsNullOrEmpty(info.ConnectAddress)
                        ? Ss14Address.DeriveConnectAddress(uri)
                        : new Uri(info.ConnectAddress),
                    info);
            }
            else
            {
                resolved = await infoApi.GetAsync(target);
            }

            Log.Information("Server URI       : {Uri}", resolved.ServerUri);
            Log.Information("Info address     : {Addr}", resolved.InfoAddress);
            Log.Information("Connect address  : {Addr}", resolved.ConnectAddress);
            Log.Information("Auth mode        : {Mode}  (pubkey: {HasKey})",
                resolved.Info.Auth.Mode, resolved.Info.Auth.PublicKey is not null);
            var b = resolved.Info.Build;
            if (b is not null)
            {
                Log.Information("Engine version   : {Engine}", b.EngineVersion);
                Log.Information("Fork / version   : {Fork} / {Version}", b.ForkId, b.Version);
                Log.Information("ACZ              : {Acz}", b.Acz);
                Log.Information("Manifest URL     : {Url}", b.ManifestUrl);
                Log.Information("Manifest DL URL  : {Url}", b.ManifestDownloadUrl);
                Log.Information("Manifest hash    : {Hash}", b.ManifestHash);
            }
            Log.Information("Description      : {Desc}", resolved.Info.Desc);
            Console.WriteLine();
            Console.WriteLine(JsonSerializer.Serialize(resolved.Info, jsonDump));
            break;
        }

        default:
            Log.Information("Dark Haven Launcher — dev CLI");
            Log.Information("Commands:");
            Log.Information("  probe <ss14://addr>   fetch and print a server's /info");
            Log.Information("  probe --hub           list servers from the public hub");
            Log.Information("  probe <addr> --via-hub  fetch /info through the hub proxy");
            Log.Information("  (-v / --verbose for debug logging)");
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
