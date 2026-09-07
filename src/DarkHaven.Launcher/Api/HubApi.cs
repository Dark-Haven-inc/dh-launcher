using System.Net.Http.Json;
using DarkHaven.Launcher.Models;
using Serilog;

namespace DarkHaven.Launcher.Api;

/// <summary>Talks to an SS14 hub (server list aggregator).</summary>
public sealed class HubApi(HttpClient http)
{
    public const string DefaultHub = "https://hub.spacestation14.com/";

    private readonly string _hubBase = DefaultHub;

    public async Task<IReadOnlyList<HubServerEntry>> GetServersAsync(CancellationToken cancel = default)
    {
        var url = _hubBase + "api/servers";
        Log.Debug("Fetching hub server list: {Url}", url);
        var list = await http.GetFromJsonAsync<HubServerEntry[]>(url, LauncherJson.Options, cancel);
        return list ?? throw new InvalidDataException("Hub server list returned null");
    }

    /// <summary>Hub-proxied <c>/info</c> for a single server (works around direct-connect blocks).</summary>
    public async Task<ServerInfo> GetServerInfoAsync(string serverAddress, CancellationToken cancel = default)
    {
        var url = $"{_hubBase}api/servers/info?url={Uri.EscapeDataString(serverAddress)}";
        return await http.GetFromJsonAsync<ServerInfo>(url, LauncherJson.Options, cancel)
               ?? throw new InvalidDataException("Hub /info proxy returned null");
    }
}
