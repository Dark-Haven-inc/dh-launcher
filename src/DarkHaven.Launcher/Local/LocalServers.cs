using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Serilog;

namespace DarkHaven.Launcher.Local;

/// <summary>
/// ЛОКАЛКА: DH servers on the player's own PC — pick a build, press start, connect. Everything lives
/// under <c>local/</c> in the launcher's data folder: <c>builds/</c> (downloaded server builds),
/// <c>dotnet/</c> (a private .NET runtime, only if the system has none), <c>servers/</c> (one folder
/// per server: profile, config, data, logs).
/// </summary>
public sealed class LocalServers
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, LocalServerHost> _hosts = new();

    public LocalBuildCatalog Catalog { get; }
    public LocalBuildStore Builds { get; }
    public DotnetRuntime Runtime { get; }
    public LocalServerStore Profiles { get; }

    public LocalServers(HttpClient http, string root, string? manifestUrl = null)
    {
        _http = http;
        Catalog = new LocalBuildCatalog(http, manifestUrl ?? LocalBuildCatalog.DefaultManifestUrl, Path.Combine(root, "cdn-manifest.json"));
        Builds = new LocalBuildStore(http, Path.Combine(root, "builds"));
        Runtime = new DotnetRuntime(http, Path.Combine(root, "dotnet"));
        Profiles = new LocalServerStore(Path.Combine(root, "servers"));
    }

    /// <summary>The host for this server — the same object for as long as the launcher runs.</summary>
    public LocalServerHost HostFor(LocalServerProfile p)
    {
        var host = _hosts.GetOrAdd(p.Id, _ => new LocalServerHost(p.Port, _http));
        if (host.Port != p.Port && !host.IsBusy)
            host = _hosts[p.Id] = new LocalServerHost(p.Port, _http);
        return host;
    }

    public bool AnyRunning => _hosts.Values.Any(h => h.IsBusy);

    /// <summary>
    /// Everything between "Запустить" and a server that answers: the build (downloaded if needed),
    /// .NET (downloaded if the PC has none), a free port, a fresh config, then the process itself.
    /// Failures end up on the host (state Crashed + the reason), never as an exception.
    /// </summary>
    public async Task StartAsync(LocalServerProfile p, CancellationToken cancel = default)
    {
        var host = HostFor(p);
        if (host.IsBusy)
            return;

        try
        {
            host.Preparing("выбор сборки…");
            var version = await PickBuildAsync(p, host, cancel);

            var exe = Builds.ServerExecutable(version)
                      ?? throw new InvalidOperationException($"В сборке {Short(version)} нет Robust.Server.exe — удалите её и скачайте заново.");

            var major = Builds.RequiredDotnetMajor(version) ?? 10;
            host.Preparing($"проверка .NET {major}…");
            var dotnetRoot = await Runtime.EnsureAsync(
                major, new Progress<double>(f => host.Preparing($"скачивание .NET {major}: {f:P0}")), cancel);

            if (PortBusy(p.Port))
                throw new InvalidOperationException(
                    $"Порт {p.Port} занят другой программой (может, этот сервер уже запущен вручную). Поменяйте порт в настройках сервера.");

            Profiles.Save(p);
            Directory.CreateDirectory(Profiles.LogsDirFor(p.Id));
            await File.WriteAllTextAsync(Profiles.ConfigPathFor(p.Id), LocalServerConfig.Toml(p, Profiles.LogsDirFor(p.Id)), cancel);
            p.LastStartedAt = DateTimeOffset.UtcNow;
            Profiles.Save(p);

            await host.StartAsync(new LocalServerLaunch(
                exe, Profiles.ConfigPathFor(p.Id), Profiles.DataDirFor(p.Id), dotnetRoot,
                LocalServerConfig.ExtraCvars(p).ToList()), cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            host.Failed("Запуск отменён.");
        }
        catch (Exception e)
        {
            Log.Warning(e, "Local server {Name} failed to start", p.Name);
            host.Failed(e is HttpRequestException ? "Нет связи с сервером сборок — проверьте интернет." : e.Message);
        }
    }

    /// <summary>Stops every local server when the launcher closes — none are left running unseen.</summary>
    public void StopAllOnExit()
    {
        foreach (var host in _hosts.Values)
        {
            try { host.StopOnExit(); }
            catch (Exception e) { Log.Warning(e, "Couldn't stop a local server on exit"); }
        }
    }

    private async Task<string> PickBuildAsync(LocalServerProfile p, LocalServerHost host, CancellationToken cancel)
    {
        // A pinned build that's already here needs nothing from the network.
        if (p.Build is { } pinned && Builds.IsInstalled(pinned))
            return pinned;

        var list = await Catalog.GetAsync(cancel);
        var build = p.Build is { } wanted
            ? list.Builds.FirstOrDefault(b => b.Version == wanted)
              ?? throw new InvalidOperationException($"Сборки {Short(wanted)} больше нет на CDN. Выберите другую в настройках сервера.")
            : list.Builds.FirstOrDefault();

        if (build is null)
        {
            // Offline or nothing published for Windows: the newest build already on disk will do.
            var installed = Builds.Installed()
                .OrderByDescending(v => Directory.GetCreationTimeUtc(Builds.PathFor(v)))
                .FirstOrDefault();
            return installed ?? throw new InvalidOperationException(list.WithoutServer > 0
                ? "На CDN пока нет серверной сборки под Windows — её должна выпустить сборка игры. Как только появится, сервер запустится."
                : "Не удалось получить список сборок, а скачанных пока нет. Проверьте интернет.");
        }

        if (!Builds.IsInstalled(build.Version))
        {
            var size = build.Size is { } bytes ? $" ({bytes / (1024 * 1024)} МБ)" : "";
            await Builds.InstallAsync(build,
                new Progress<double>(f => host.Preparing($"скачивание сборки {build.ShortVersion}{size}: {f:P0}")), cancel);
        }
        return build.Version;
    }

    internal static bool PortBusy(int port)
    {
        try
        {
            var tcp = new TcpListener(IPAddress.Loopback, port);
            tcp.Start();
            tcp.Stop();
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    private static string Short(string v) => v.Length > 8 ? v[..8] : v;
}
