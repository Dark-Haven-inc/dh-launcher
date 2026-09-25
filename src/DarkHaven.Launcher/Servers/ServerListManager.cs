using System.Net.Http.Json;
using System.Text.Json;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Models;
using Serilog;

namespace DarkHaven.Launcher.Servers;

/// <summary>
/// Owns the СЕРВЕРЫ list. It used to be the whole public SS14 hub; now it's only the servers owners
/// or admins let in on the platform (<c>GET /api/servers</c>) — anything else a player wants to
/// reach they can still type in by address, or run as a local server.
///
/// The platform gives names and addresses; each server's own <c>/status</c> is asked for players,
/// map and ping, in parallel. The last list that loaded is kept on disk, so a platform that's down
/// (it runs on a home machine) still leaves players with the servers they had.
/// </summary>
public sealed class ServerListManager(PlatformApi platform, HttpClient http, string cachePath)
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(5);

    private readonly List<ServerEntry> _servers = [];

    public IReadOnlyList<ServerEntry> Servers => _servers;
    public DateTimeOffset? LastRefresh { get; private set; }

    /// <summary>Set when the last refresh fell back to the on-disk copy (platform unreachable).</summary>
    public DateTimeOffset? StaleSince { get; private set; }

    public async Task RefreshAsync(CancellationToken cancel = default)
    {
        IReadOnlyList<PlatformListedServer>? listed = await platform.GetListedServersAsync(cancel);
        if (listed is null)
        {
            listed = ReadCache(out var savedAt);
            StaleSince = savedAt;
        }
        else
        {
            StaleSince = null;
            WriteCache(listed);
        }

        var entries = listed.Select(s => new ServerEntry(s.Address) { Name = s.Name, Description = s.Description }).ToList();
        await Task.WhenAll(entries.Select(e => PollAsync(e, cancel)));

        _servers.Clear();
        _servers.AddRange(entries);
        Log.Debug("СЕРВЕРЫ: {Count} approved servers ({Online} answering)",
            entries.Count, entries.Count(e => e.Reachability == ServerReachability.Online));
        LastRefresh = DateTimeOffset.UtcNow;
    }

    private async Task PollAsync(ServerEntry entry, CancellationToken cancel)
    {
        if (!Ss14Address.TryParse(entry.Address, out var uri))
        {
            entry.Reachability = ServerReachability.Offline;
            return;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(StatusTimeout);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var status = await http.GetFromJsonAsync<ServerStatus>(Ss14Address.StatusAddress(uri), LauncherJson.Options, timeout.Token);
            if (status is null)
            {
                entry.Reachability = ServerReachability.Offline;
                return;
            }
            // The list shows the name staff approved, not whatever the server calls itself today.
            var approvedName = entry.Name;
            entry.ApplyStatus(status);
            entry.Name = approvedName;
            entry.PingMs = (int)sw.ElapsedMilliseconds;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            entry.Reachability = ServerReachability.Offline;
        }
    }

    private sealed record Cache(DateTimeOffset SavedAt, PlatformListedServer[] Servers);

    private void WriteCache(IReadOnlyList<PlatformListedServer> servers)
    {
        try
        {
            File.WriteAllText(cachePath, JsonSerializer.Serialize(new Cache(DateTimeOffset.UtcNow, [.. servers]), LauncherJson.Options));
        }
        catch (Exception e)
        {
            Log.Debug(e, "Could not cache the server list to {Path}", cachePath);
        }
    }

    private IReadOnlyList<PlatformListedServer> ReadCache(out DateTimeOffset? savedAt)
    {
        savedAt = null;
        try
        {
            if (File.Exists(cachePath) && JsonSerializer.Deserialize<Cache>(File.ReadAllText(cachePath), LauncherJson.Options) is { } c)
            {
                savedAt = c.SavedAt;
                return c.Servers;
            }
        }
        catch (Exception e)
        {
            Log.Debug(e, "Could not read the cached server list");
        }
        return [];
    }
}
