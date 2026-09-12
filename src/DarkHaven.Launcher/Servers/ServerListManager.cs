using DarkHaven.Launcher.Api;
using Serilog;

namespace DarkHaven.Launcher.Servers;

/// <summary>
/// Owns the "All Servers" browser list. The hub already aggregates each server's <c>/status</c>, so a
/// refresh is a single hub request — no per-server polling for the list view.
///
/// This is the "РУхаб": the browser only ever holds servers self-tagged (or hub-inferred) as
/// <c>lang:ru</c> or <c>lang:uk</c> — every other server is dropped right here, before it ever
/// reaches a ViewModel, so there's no separate toggle to turn this back off.
/// </summary>
public sealed class ServerListManager(HubApi hub)
{
    private static readonly string[] RuHubLanguages = ["ru", "uk"];

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

            if (IsRuHub(entry.Tags))
                _servers.Add(entry);
        }
        Log.Debug("РУхаб: kept {Count} of {Total} hub servers", _servers.Count, entries.Count);
        LastRefresh = DateTimeOffset.UtcNow;
    }

    private static bool IsRuHub(IReadOnlyList<string> tags) =>
        tags.Any(t => t.StartsWith("lang:", StringComparison.OrdinalIgnoreCase)
                      && RuHubLanguages.Contains(t["lang:".Length..], StringComparer.OrdinalIgnoreCase));
}
