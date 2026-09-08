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
    /// <summary>Short codename shown on the map, e.g. "ХЕЙВЕН".</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("address")]
    public required string Address { get; init; }

    /// <summary>One-line description, e.g. "Центральная станция".</summary>
    [JsonPropertyName("blurb")]
    public string? Blurb { get; init; }

    /// <summary>Codenames of adjacent regions reachable by gate.</summary>
    [JsonPropertyName("neighbours")]
    public string[] Neighbours { get; init; } = [];

    /// <summary>Position on the "КАРТА СЕТИ" (0..1 in both axes). Optional.</summary>
    [JsonPropertyName("x")] public double X { get; init; } = 0.5;
    [JsonPropertyName("y")] public double Y { get; init; } = 0.5;

    /// <summary>The hub-central station. Rendered larger / glowing.</summary>
    [JsonPropertyName("central")] public bool Central { get; init; }
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
        var entries = Regions.Select(r => new ServerEntry(r.Address)
        {
            Name = r.Name,
            RegionBlurb = r.Blurb ?? "",
            RegionNeighbours = r.Neighbours,
            RegionX = r.X,
            RegionY = r.Y,
            RegionCentral = r.Central,
        }).ToList();

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
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var status = await http.GetFromJsonAsync<ServerStatus>(
                    Ss14Address.StatusAddress(uri), LauncherJson.Options, timeout.Token);
                sw.Stop();
                if (status is not null)
                {
                    entry.ApplyStatus(status);
                    entry.PingMs = (int)sw.ElapsedMilliseconds;
                }
                else
                {
                    entry.Reachability = ServerReachability.Offline;
                }
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
