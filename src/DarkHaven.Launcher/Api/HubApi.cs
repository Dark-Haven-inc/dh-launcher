using System.Net.Http.Json;
using System.Text.Json;
using DarkHaven.Launcher.Models;
using Serilog;

namespace DarkHaven.Launcher.Api;

/// <summary>Talks to an SS14 hub (server list aggregator). The list is cached so the browser still
/// has something to show when the hub is unreachable.</summary>
public sealed class HubApi(HttpClient http, string? cachePath = null)
{
    public const string DefaultHub = "https://hub.spacestation14.com/";

    private readonly string _hubBase = DefaultHub;

    /// <summary>When the last returned list came from the on-disk cache (hub was down), its age; else null.</summary>
    public DateTimeOffset? ServedFromCacheAt { get; private set; }

    public async Task<IReadOnlyList<HubServerEntry>> GetServersAsync(CancellationToken cancel = default)
    {
        var url = _hubBase + "api/servers";
        Log.Debug("Fetching hub server list: {Url}", url);

        try
        {
            var json = await http.GetStringAsync(url, cancel);
            var list = JsonSerializer.Deserialize<HubServerEntry[]>(json, LauncherJson.Options)
                       ?? throw new InvalidDataException("Hub server list returned null");

            if (cachePath is not null)
            {
                try { await File.WriteAllTextAsync(cachePath, json, cancel); }
                catch (Exception e) { Log.Debug(e, "Could not write the hub cache"); }
            }

            ServedFromCacheAt = null;
            return list;
        }
        catch (Exception e) when (e is not OperationCanceledException && cachePath is not null && File.Exists(cachePath))
        {
            Log.Warning(e, "Hub unreachable — serving the cached server list");
            var json = await File.ReadAllTextAsync(cachePath, cancel);
            ServedFromCacheAt = new DateTimeOffset(File.GetLastWriteTimeUtc(cachePath), TimeSpan.Zero);
            return JsonSerializer.Deserialize<HubServerEntry[]>(json, LauncherJson.Options) ?? [];
        }
    }

    /// <summary>Hub-proxied <c>/info</c> for a single server (works around direct-connect blocks).</summary>
    public async Task<ServerInfo> GetServerInfoAsync(string serverAddress, CancellationToken cancel = default)
    {
        var url = $"{_hubBase}api/servers/info?url={Uri.EscapeDataString(serverAddress)}";
        return await http.GetFromJsonAsync<ServerInfo>(url, LauncherJson.Options, cancel)
               ?? throw new InvalidDataException("Hub /info proxy returned null");
    }
}
