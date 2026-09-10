using System.Net.Http.Json;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Data;
using DarkHaven.Launcher.Models;
using Serilog;

namespace DarkHaven.Launcher.Servers;

/// <summary>
/// Watches a small set of Dark Haven regions in the background and raises <see cref="CameOnline"/>
/// the moment one of them flips offline → online. Lets a player arm "поднимется — скажи мне" on
/// ХЕЙВЕН and go do something else. No backend — it just polls each region's <c>/status</c>.
/// </summary>
public sealed class RegionWatcher : IDisposable
{
    private const string ConfigKey = "WatchedRegions";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(45);

    private readonly HttpClient _http;
    private readonly SettingsDatabase _settings;
    private readonly Func<IReadOnlyList<DhRegion>> _regions;

    private readonly HashSet<string> _watched;
    private readonly Dictionary<string, bool> _lastOnline = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;

    /// <summary>(region name, ss14 address) of a region that just came online.</summary>
    public event Action<string, string>? CameOnline;

    public RegionWatcher(HttpClient http, SettingsDatabase settings, Func<IReadOnlyList<DhRegion>> regions)
    {
        _http = http;
        _settings = settings;
        _regions = regions;
        _watched = new HashSet<string>(
            (settings.GetConfig(ConfigKey) ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
    }

    public bool IsWatched(string regionName) => _watched.Contains(regionName);

    public bool AnyWatched => _watched.Count > 0;

    public void SetWatched(string regionName, bool on)
    {
        if (on ? _watched.Add(regionName) : _watched.Remove(regionName))
        {
            _settings.SetConfig(ConfigKey, string.Join(",", _watched));
            if (!on) _lastOnline.Remove(regionName);
            if (on) EnsureRunning();
        }
    }

    /// <summary>Start the poll loop if anything is armed. Safe to call repeatedly.</summary>
    public void EnsureRunning()
    {
        if (_cts is not null || !AnyWatched)
            return;

        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    private async Task LoopAsync(CancellationToken cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested && AnyWatched)
            {
                await PollOnceAsync(cancel);
                await Task.Delay(Interval, cancel);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception e) { Log.Warning(e, "Region watcher loop stopped"); }
        finally
        {
            // Let EnsureRunning() spin a fresh loop next time something is armed.
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task PollOnceAsync(CancellationToken cancel)
    {
        var targets = _regions()
            .Where(r => _watched.Contains(r.Name) && !r.IsQuarantine && !string.IsNullOrWhiteSpace(r.Address))
            .ToList();

        foreach (var region in targets)
        {
            bool online = false;
            try
            {
                if (Ss14Address.TryParse(region.Address, out var uri))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    timeout.CancelAfter(TimeSpan.FromSeconds(6));
                    var status = await _http.GetFromJsonAsync<ServerStatus>(
                        Ss14Address.StatusAddress(uri), LauncherJson.Options, timeout.Token);
                    online = status is not null;
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
            catch { online = false; }

            var hadPrior = _lastOnline.TryGetValue(region.Name, out var wasOnline);
            _lastOnline[region.Name] = online;

            // Fire only on a genuine offline → online edge. The first poll just records state,
            // so arming the watch while the region is already up never pings.
            if (online && hadPrior && !wasOnline)
            {
                Log.Information("Watched region {Name} came online", region.Name);
                CameOnline?.Invoke(region.Name, region.Address);
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
