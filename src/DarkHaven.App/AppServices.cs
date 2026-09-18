using Avalonia.Threading;
using System.Net.Http;
using DarkHaven.ContentDb;
using DarkHaven.Launcher;
using DarkHaven.Launcher.Accounts;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Content;
using DarkHaven.Launcher.Data;
using DarkHaven.Launcher.Engine;
using DarkHaven.Launcher.Servers;
using DarkHaven.Launcher.Update;

namespace DarkHaven.App;

/// <summary>Hand-rolled composition root. Small enough that a DI container would just be ceremony.</summary>
public sealed class AppServices : IDisposable
{
    public HttpClient Http { get; }
    public SettingsDatabase Settings { get; }
    public ContentDatabase ContentDb { get; }
    public AuthApi Auth { get; }
    public AccountManager Accounts { get; }
    public EngineManager Engines { get; }
    public HubApi Hub { get; }
    public ServerListManager ServerList { get; }
    public DhRegions Regions { get; }
    public RegionWatcher RegionWatch { get; }
    public DhNews News { get; }
    public PlatformApi Platform { get; }
    public ServerInfoApi ServerInfo { get; }
    public ContentUpdater Content { get; }
    public GameLauncher Game { get; }
    public LaunchCoordinator Launch { get; }
    public LauncherUpdater Updater { get; }
    public DiscordPresence Discord { get; }
    public SlotWatcher SlotWatch { get; }

    /// <summary>The <c>ss14://</c> address of the server the player is currently in, or null.</summary>
    public string? CurrentGameAddress { get; private set; }
    public string? CurrentGameName { get; private set; }
    public event Action? GameSessionChanged;

    private long _playSessionId;
    private DateTime _playSessionStart;

    public void SetGameSession(string? address, string? name = null, bool isRegion = false)
    {
        if (address is not null)
        {
            try
            {
                _playSessionId = Settings.StartPlaySession(address, string.IsNullOrWhiteSpace(name) ? address : name, isRegion);
                _playSessionStart = DateTime.UtcNow;
            }
            catch { _playSessionId = 0; }
        }
        else if (_playSessionId > 0)
        {
            try { Settings.EndPlaySession(_playSessionId, (long)(DateTime.UtcNow - _playSessionStart).TotalSeconds); }
            catch { /* non-fatal */ }
            _playSessionId = 0;
        }

        CurrentGameAddress = address;
        CurrentGameName = address is null ? null : name;
        GameSessionChanged?.Invoke();
    }

    /// <summary>
    /// (Re)establishes the platform session for whichever account is active — call after login,
    /// logout, or switching accounts. A no-op if no <c>PlatformApiUrl</c> is configured, no account
    /// is active, or the platform is unreachable; <see cref="PlatformApi"/> degrades to signed-out.
    /// </summary>
    public event Action? PlatformSessionChanged;

    public async Task SignInToPlatformAsync(CancellationToken cancel = default)
    {
        if (!Platform.IsConfigured)
        {
            // Still a real state transition (e.g. an account just logged in/out) — the top bar's
            // account name and other account-driven bindings need to hear about it even when there's
            // no platform to actually sign in to.
            PlatformSessionChanged?.Invoke();
            return;
        }

        var active = Accounts.Active;
        if (active is null)
        {
            Platform.SignOut();
            PlatformSessionChanged?.Invoke();
            return;
        }

        var game = await Accounts.ToGameAccountAsync(active, cancel);
        if (game is null)
            Platform.SignOut();
        else
            await Platform.SignInAsync(game.UserId, game.Username, game.Token, cancel);

        PlatformSessionChanged?.Invoke();
    }

    public AppServices()
    {
        Http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        Http.DefaultRequestHeaders.Add("User-Agent", "Frontier15Launcher/0.2");

        Settings = new SettingsDatabase(LauncherPaths.SettingsDbPath);
        Settings.Initialize();

        ContentDb = new ContentDatabase(LauncherPaths.ContentDbPath);
        ContentDb.Initialize();

        Auth = new AuthApi(Http, Settings.GetConfig("AuthUrl"));
        Accounts = new AccountManager(Settings, Auth);
        Accounts.Load();

        var signing = new EngineSignature(LauncherPaths.SigningKeyPath);
        var bundledEngines = Path.Combine(AppContext.BaseDirectory, "bundled-engines");
        Engines = new EngineManager(Http, LauncherPaths.EnginesDir, LauncherPaths.ModulesDir, signing,
            bundledEngines, Settings.GetConfig("EngineBuildsUrl"));

        if (int.TryParse(Settings.GetConfig("DownloadLimitKbps"), out var kbps))
            DarkHaven.Launcher.Content.DownloadThrottle.SetKbps(kbps);

        Hub = new HubApi(Http, LauncherPaths.HubCachePath);
        ServerList = new ServerListManager(Hub);
        Regions = new DhRegions(Http, LauncherPaths.RegionsJsonPath, remoteUrl: Settings.GetConfig("RegionsUrl"));
        RegionWatch = new RegionWatcher(Http, Settings, () => Regions.Regions);
        RegionWatch.EnsureRunning();

        var platformUrl = Settings.GetConfig("PlatformApiUrl");
        Platform = new PlatformApi(Http, platformUrl);
        // The platform's own /api/news is a drop-in for the bundled news.json (same shape) —
        // default to it once a platform is configured, unless someone already set NewsUrl by hand.
        var newsUrl = Settings.GetConfig("NewsUrl")
                      ?? (string.IsNullOrWhiteSpace(platformUrl) ? null : $"{platformUrl.TrimEnd('/')}/api/news");
        News = new DhNews(Http, LauncherPaths.NewsJsonPath, LauncherPaths.NewsCachePath, newsUrl);

        ServerInfo = new ServerInfoApi(Http);
        Content = new ContentUpdater(ContentDb, new ManifestDownloader(Http), Engines);
        Game = new GameLauncher(LocateLoader(), LauncherPaths.SigningKeyPath, Engines, LauncherPaths.ContentDbPath);
        Launch = new LaunchCoordinator(ServerInfo, Content, Accounts, Engines, Game);
        Updater = new LauncherUpdater(Settings.GetConfig("UpdateFeedUrl"), Settings.GetConfig("UpdateChannel"));
        Discord = new DiscordPresence(Settings.GetConfig("DiscordAppId"));
        SlotWatch = new SlotWatcher(Http);

        StartPresenceHeartbeat();
        _ = SignInToPlatformAsync();
    }

    // --- Presence heartbeat: tells friends "running, and playing here" (see dh-platform's PresenceController) ---

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The platform JWT lives an hour; re-sign-in comfortably before that, or a launcher left open
    /// would silently drop off friends' lists and stop receiving notifications.
    /// </summary>
    private static readonly TimeSpan PlatformSessionRenewAfter = TimeSpan.FromMinutes(45);

    private DispatcherTimer? _heartbeat;

    /// <summary>
    /// A DispatcherTimer rather than a thread-pool one on purpose: re-signing in raises
    /// PlatformSessionChanged, whose handlers touch bound view-model properties.
    /// </summary>
    private void StartPresenceHeartbeat()
    {
        PlatformSessionChanged += () => _ = SendPresenceAsync();
        GameSessionChanged += () => _ = SendPresenceAsync();

        _heartbeat = new DispatcherTimer { Interval = HeartbeatInterval };
        _heartbeat.Tick += async (_, _) =>
        {
            if (Platform.IsSignedIn && Platform.SignedInAt is { } at && DateTimeOffset.UtcNow - at > PlatformSessionRenewAfter)
                await SignInToPlatformAsync(); // raises PlatformSessionChanged, which sends presence
            else
                await SendPresenceAsync();
        };
        _heartbeat.Start();
    }

    private Task SendPresenceAsync() =>
        Platform.IsSignedIn
            ? Platform.UpdatePresenceAsync(CurrentGameAddress, CurrentGameName)
            : Task.CompletedTask;

    public void Dispose()
    {
        _heartbeat?.Stop();
        if (Platform.IsSignedIn)
        {
            // Drop off friends' lists now rather than after the platform's 5-minute window. Off the
            // UI thread, so the shutdown wait can't deadlock on the dispatcher; capped so a dead
            // platform can't hold up closing the launcher.
            try { Task.Run(() => Platform.ClearPresenceAsync()).Wait(TimeSpan.FromSeconds(2)); }
            catch { /* best effort */ }
        }

        RegionWatch.Dispose();
        SlotWatch.Dispose();
        Discord.Dispose();
        Http.Dispose();
    }

    private static string LocateLoader()
    {
        var here = AppContext.BaseDirectory;
        // Installed layout: loader sits under ./loader/. Dev layout: sibling build output.
        var installed = Path.Combine(here, "loader", "DarkHaven.Loader.exe");
        if (File.Exists(installed))
            return installed;

        foreach (var cfg in new[] { "Debug", "Release" })
        {
            var dev = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..",
                "DarkHaven.Loader", "bin", cfg, "net10.0", "DarkHaven.Loader.exe"));
            if (File.Exists(dev))
                return dev;
        }
        return installed; // will error clearly on use
    }
}
