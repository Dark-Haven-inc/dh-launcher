using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Models;
using Serilog;

namespace DarkHaven.Launcher.Servers;

/// <summary>One entry of <c>dh-regions.json</c>.</summary>
public sealed record DhRegion
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("address")]
    public required string Address { get; init; }

    [JsonPropertyName("blurb")]
    public string? Blurb { get; init; }
}

/// <summary>
/// The pinned "Dark Haven Sector" list. Loads a bundled fallback and, if configured, refreshes it
/// from a DH-hosted URL. Each region's live population comes from polling its own <c>/status</c>.
/// </summary>
public sealed class DhRegions(HttpClient http, string bundledJsonPath, string? remoteUrl = null)
{
    public IReadOnlyList<DhRegion> Regions { get; private set; } = [];

    public async Task LoadAsync(CancellationToken cancel = default)
    {
        Regions = TryLoadBundled();

        if (!string.IsNullOrEmpty(remoteUrl))
        {
            try
            {
                var remote = await http.GetFromJsonAsync<DhRegion[]>(remoteUrl, LauncherJson.Options, cancel);
                if (remote is { Length: > 0 })
                {
                    Regions = remote;
                    Log.Debug("Loaded {Count} DH regions from {Url}", remote.Length, remoteUrl);
                }
            }
            catch (Exception e)
            {
                Log.Warning(e, "Could not refresh DH regions from {Url}, using bundled list", remoteUrl);
            }
        }
    }

    /// <summary>Builds <see cref="ServerEntry"/> objects and polls each region's <c>/status</c>.</summary>
    public async Task<IReadOnlyList<ServerEntry>> PollAsync(CancellationToken cancel = default)
    {
        var entries = Regions.Select(r => new ServerEntry(r.Address) { Name = r.Name, RegionBlurb = r.Blurb ?? "" }).ToList();

        await Task.WhenAll(entries.Select(async entry =>
        {
            try
            {
                if (!Ss14Address.TryParse(entry.Address, out var uri))
                {
                    entry.Reachability = ServerReachability.Offline;
                    return;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var status = await http.GetFromJsonAsync<ServerStatus>(
                    Ss14Address.StatusAddress(uri), LauncherJson.Options, timeout.Token);
                if (status is not null)
                    entry.ApplyStatus(status);
                else
                    entry.Reachability = ServerReachability.Offline;
            }
            catch (Exception)
            {
                entry.Reachability = ServerReachability.Offline;
            }
        }));

        return entries;
    }

    private IReadOnlyList<DhRegion> TryLoadBundled()
    {
        try
        {
            if (File.Exists(bundledJsonPath))
            {
                var json = File.ReadAllText(bundledJsonPath);
                var parsed = JsonSerializer.Deserialize<DhRegion[]>(json, LauncherJson.Options);
                if (parsed is not null)
                    return parsed;
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to read bundled dh-regions.json");
        }
        return [];
    }
}
