using DarkHaven.Launcher.Api;
using Serilog;

namespace DarkHaven.Launcher.Servers;

/// <summary>
/// Owns the "All Servers" browser list. The hub already aggregates each server's <c>/status</c>, so a
/// refresh is a single hub request — no per-server polling for the list view.
/// </summary>
public sealed class ServerListManager(HubApi hub)
{
    private readonly List<ServerEntry> _servers = [];

    public IReadOnlyList<ServerEntry> Servers => _servers;
    public DateTimeOffset? LastRefresh { get; private set; }

    /// <summary>Set when the last refresh fell back to the on-disk cache (hub unreachable).</summary>
    public DateTimeOffset? StaleSince => hub.ServedFromCacheAt;

    public async Task RefreshAsync(CancellationToken cancel = default)
    {
        var entries = await hub.GetServersAsync(cancel);
        Log.Debug("Hub returned {Count} servers", entries.Count);

        _servers.Clear();
        foreach (var e in entries)
        {
            var entry = new ServerEntry(e.Address);
            if (e.StatusData is { } status)
                entry.ApplyStatus(status, e.InferredTags);
            _servers.Add(entry);
        }
        LastRefresh = DateTimeOffset.UtcNow;
    }
}
