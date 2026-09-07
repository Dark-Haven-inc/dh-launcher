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
    public int Players { get; set; }
    public int SoftMaxPlayers { get; set; }
    public string? Map { get; set; }
    public RunLevel? RunLevel { get; set; }
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary>Set for pinned Dark Haven regions; used for grouping + the sector view.</summary>
    public string? RegionBlurb { get; set; }
    public bool IsDarkHavenRegion => RegionBlurb is not null;

    public string DisplayName => Name ?? Address;

    public void ApplyStatus(ServerStatus status, IReadOnlyList<string>? inferredTags = null)
    {
        Reachability = ServerReachability.Online;
        Name = status.Name ?? Name;
        Players = Math.Max(0, status.Players);
        SoftMaxPlayers = Math.Max(0, status.SoftMaxPlayers);
        Map = status.Map;
        RunLevel = status.RunLevel;
        Tags = (status.Tags ?? []).Concat(inferredTags ?? []).Distinct().ToArray();
    }
}
