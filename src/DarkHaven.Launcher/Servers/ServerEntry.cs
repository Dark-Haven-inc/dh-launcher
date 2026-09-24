using DarkHaven.Launcher.Models;

namespace DarkHaven.Launcher.Servers;

public enum ServerReachability { Unknown, Online, Offline }

/// <summary>
/// A server as shown in the browser: its address plus the latest status we have (from the hub
/// aggregate, or a direct <c>/status</c> poll). Mutable so a single instance can be refreshed
/// in place while the UI holds a reference.
/// </summary>
public sealed class ServerEntry(string address)
{
    public string Address { get; } = address;

    public ServerReachability Reachability { get; set; } = ServerReachability.Unknown;
    public string? Name { get; set; }
    /// <summary>The name the server reports in <c>/status</c> (kept even for regions, which display a codename).</summary>
    public string? ServerName { get; set; }
    public int Players { get; set; }
    public int SoftMaxPlayers { get; set; }
    public string? Map { get; set; }
    public RunLevel? RunLevel { get; set; }
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary>Set for pinned Dark Haven regions; used for grouping + the sector view.</summary>
    public string? RegionBlurb { get; set; }
    public bool IsDarkHavenRegion => RegionBlurb is not null;
    public IReadOnlyList<string> RegionNeighbours { get; set; } = [];
    public double RegionX { get; set; } = 0.5;
    public double RegionY { get; set; } = 0.5;
    public bool RegionCentral { get; set; }

    /// <summary>A region shown on the map to sketch the planned network but not yet online / connectable.</summary>
    public bool RegionQuarantine { get; set; }

    /// <summary>The fork id this region's server must report — see <see cref="DhRegion.ExpectFork"/>.</summary>
    public string? ExpectedFork { get; set; }

    /// <summary>Round-trip time to the server's HTTP endpoint, ms — populated by a region poll.</summary>
    public int? PingMs { get; set; }

    public string DisplayName => Name ?? Address;

    public void ApplyStatus(ServerStatus status, IReadOnlyList<string>? inferredTags = null)
    {
        Reachability = ServerReachability.Online;
        // A DH region keeps its codename; a hub server takes the name it reports.
        if (!IsDarkHavenRegion)
            Name = status.Name ?? Name;
        ServerName = status.Name;
        Players = Math.Max(0, status.Players);
        SoftMaxPlayers = Math.Max(0, status.SoftMaxPlayers);
        Map = status.Map;
        RunLevel = status.RunLevel;
        Tags = (status.Tags ?? []).Concat(inferredTags ?? []).Distinct().ToArray();
    }
}
