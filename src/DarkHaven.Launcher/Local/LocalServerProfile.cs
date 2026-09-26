using System.Text.Json;
using DarkHaven.Launcher.Api;

namespace DarkHaven.Launcher.Local;

public enum LocalServerMode
{
    /// <summary>A normal round: lobby, jobs, the usual map.</summary>
    Play,

    /// <summary>The game's own developer preset (Build/development): no lobby, no events, NFDev map,
    /// no grid splitting — for building maps and trying things out.</summary>
    Develop,
}

/// <summary>One local server the player has set up. Its folder keeps the saved maps, the database and logs.</summary>
public sealed class LocalServerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Мой сервер";
    public LocalServerMode Mode { get; set; } = LocalServerMode.Develop;

    /// <summary>The CDN build to run; null = the newest one available.</summary>
    public string? Build { get; set; }

    public int Port { get; set; } = LocalServerStore.FirstPort;
    public int MaxPlayers { get; set; } = 8;

    /// <summary>Map id to load instead of the mode's own (e.g. a map the player is building).</summary>
    public string? Map { get; set; }

    /// <summary>Anything else, as cvar = value; passed to the server with <c>--cvar</c>.</summary>
    public Dictionary<string, string> Cvars { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastStartedAt { get; set; }
}

/// <summary>
/// Local server profiles, one folder each under <c>local/servers/&lt;id&gt;</c>: <c>server.json</c> (the
/// profile), <c>server_config.toml</c> (written fresh on every start), <c>data/</c> (the server's own
/// data dir — saved maps, database) and <c>logs/</c>.
/// </summary>
public sealed class LocalServerStore(string root)
{
    /// <summary>Ports start above the usual 1212, so a dev server someone runs by hand isn't in the way.</summary>
    public const int FirstPort = 1250;

    private const string ProfileFile = "server.json";

    public string DirFor(string id) => Path.Combine(root, LocalBuildStore.Safe(id));
    public string DataDirFor(string id) => Path.Combine(DirFor(id), "data");
    public string ConfigPathFor(string id) => Path.Combine(DirFor(id), "server_config.toml");
    public string LogsDirFor(string id) => Path.Combine(DirFor(id), "logs");

    public IReadOnlyList<LocalServerProfile> List()
    {
        if (!Directory.Exists(root))
            return [];
        var list = new List<LocalServerProfile>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            var file = Path.Combine(dir, ProfileFile);
            if (!File.Exists(file))
                continue;
            try
            {
                if (JsonSerializer.Deserialize<LocalServerProfile>(File.ReadAllText(file), LauncherJson.Options) is { } p)
                {
                    p.Id = Path.GetFileName(dir); // the folder is the identity, whatever the file says
                    list.Add(p);
                }
            }
            catch (JsonException)
            {
                // A hand-broken profile shouldn't hide the others.
            }
        }
        return list.OrderBy(p => p.CreatedAt).ToList();
    }

    public void Save(LocalServerProfile p)
    {
        Directory.CreateDirectory(DirFor(p.Id));
        Directory.CreateDirectory(DataDirFor(p.Id));
        var tmp = Path.Combine(DirFor(p.Id), ProfileFile + ".tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(p, LauncherJson.Options));
        File.Move(tmp, Path.Combine(DirFor(p.Id), ProfileFile), overwrite: true);
    }

    /// <summary>Removes the server with everything in its folder — saved maps included.</summary>
    public void Delete(string id)
    {
        var dir = DirFor(id);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    /// <summary>A fresh profile on the first port no other local server uses.</summary>
    public LocalServerProfile New(string name, LocalServerMode mode)
    {
        var taken = List().Select(p => p.Port).ToHashSet();
        var port = FirstPort;
        while (taken.Contains(port))
            port++;
        return new LocalServerProfile { Name = name, Mode = mode, Port = port };
    }
}
