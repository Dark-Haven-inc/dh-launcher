using System.Net.Http.Json;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Models;
using Serilog;

namespace DarkHaven.Launcher.Servers;

/// <summary>
/// "Ждать свободного места": polls one full server's <c>/status</c> until a player slot opens, then
/// raises <see cref="SlotFreed"/> once and stops. One server at a time, nothing persisted — unlike
/// <see cref="RegionWatcher"/>, this is a "right now" thing. Events fire off the UI thread.
/// </summary>
public sealed class SlotWatcher(HttpClient http, TimeSpan? interval = null) : IDisposable
{
    /// <summary>Slots on a busy server go in seconds; 15 s is quick enough and still gentle on it.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(15);

    private readonly TimeSpan _interval = interval ?? DefaultInterval;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    public string? Address { get; private set; }
    public string? Name { get; private set; }
    public bool IsWatching => _cts is not null;

    /// <summary>(name, address) — a slot just opened; the watch is already over.</summary>
    public event Action<string, string>? SlotFreed;

    /// <summary>A watch started or ended — for buttons and banners to re-read their state.</summary>
    public event Action? Changed;

    /// <summary>
    /// Full by the server's own soft cap. SS14 turns non-admins away at <c>soft_max_players</c>;
    /// a server that doesn't report one is never "full".
    /// </summary>
    public static bool IsFull(int players, int softMaxPlayers) => softMaxPlayers > 0 && players >= softMaxPlayers;

    public bool IsWatchingAddress(string address) =>
        string.Equals(Address, address, StringComparison.OrdinalIgnoreCase);

    public void Watch(string address, string name)
    {
        if (!Ss14Address.TryParse(address, out var uri))
            return;

        CancellationTokenSource cts;
        lock (_lock)
        {
            CancelLocked();
            cts = _cts = new CancellationTokenSource();
            Address = address;
            Name = name;
        }
        Changed?.Invoke();
        _ = LoopAsync(uri, address, name, cts);
    }

    public void Stop()
    {
        bool had;
        lock (_lock)
        {
            had = _cts is not null;
            CancelLocked();
        }
        if (had) Changed?.Invoke();
    }

    private void CancelLocked()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        Address = null;
        Name = null;
    }

    private async Task LoopAsync(Uri uri, string address, string name, CancellationTokenSource cts)
    {
        var cancel = cts.Token;
        try
        {
            while (true)
            {
                await Task.Delay(_interval, cancel);

                ServerStatus? status;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    timeout.CancelAfter(TimeSpan.FromSeconds(6));
                    status = await http.GetFromJsonAsync<ServerStatus>(
                        Ss14Address.StatusAddress(uri), LauncherJson.Options, timeout.Token);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
                catch (Exception) { continue; } // a blip or a restart — keep waiting

                if (status is null || IsFull(status.Players, status.SoftMaxPlayers))
                    continue;

                // Only the watch that is still current may finish — a newer Watch() replaced this one.
                lock (_lock)
                {
                    if (_cts != cts)
                        return;
                    CancelLocked();
                }
                Log.Information("Slot opened on {Name} ({Players}/{Max})", name, status.Players, status.SoftMaxPlayers);
                Changed?.Invoke();
                SlotFreed?.Invoke(name, address);
                return;
            }
        }
        catch (OperationCanceledException) { /* stopped, or replaced by another watch */ }
    }

    public void Dispose() => Stop();
}
