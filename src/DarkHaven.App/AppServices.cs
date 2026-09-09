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
    public ServerInfoApi ServerInfo { get; }
    public ContentUpdater Content { get; }
    public GameLauncher Game { get; }
    public LaunchCoordinator Launch { get; }
    public LauncherUpdater Updater { get; }
    public DiscordPresence Discord { get; }

    /// <summary>The <c>ss14://</c> address of the server the player is currently in, or null.</summary>
    public string? CurrentGameAddress { get; private set; }
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
        GameSessionChanged?.Invoke();
    }

    public AppServices()
    {
        Http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        Http.DefaultRequestHeaders.Add("User-Agent", "DarkHavenLauncher/0.1");

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

        ServerInfo = new ServerInfoApi(Http);
        Content = new ContentUpdater(ContentDb, new ManifestDownloader(Http), Engines);
        Game = new GameLauncher(LocateLoader(), LauncherPaths.SigningKeyPath, Engines, LauncherPaths.ContentDbPath);
        Launch = new LaunchCoordinator(ServerInfo, Content, Accounts, Engines, Game);
        Updater = new LauncherUpdater(Settings.GetConfig("UpdateFeedUrl"), Settings.GetConfig("UpdateChannel"));
        Discord = new DiscordPresence(Settings.GetConfig("DiscordAppId"));
    }

    public void Dispose()
    {
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
