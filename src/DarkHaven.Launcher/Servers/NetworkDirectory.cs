using System.Text.Json;
using System.Text.Json.Serialization;
using DarkHaven.Launcher.Api;
using Serilog;

namespace DarkHaven.Launcher.Servers;

/// <summary>One entry of <c>dh-networks.json</c> — hand-curated extra info for a РУхаб network group
/// that <see cref="NetworkGrouping"/> can't derive from the hub itself (no server's own status carries
/// a longer description or its team's Discord invite).</summary>
public sealed record NetworkInfo
{
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("discordUrl")]
    public string? DiscordUrl { get; init; }
}

/// <summary>
/// Looks up <see cref="NetworkInfo"/> by a network's <see cref="ServerNetworkGroup.Label"/> —
/// case-insensitive, since the label's exact casing follows however that network's own servers spell
/// their name. Ships as an empty file by default; filling in real descriptions/Discord links for other
/// teams' networks is data entry for whoever maintains them, not something to guess at here.
/// </summary>
public sealed class NetworkDirectory(string bundledJsonPath)
{
    private readonly Dictionary<string, NetworkInfo> _byLabel = Load(bundledJsonPath);

    public bool TryGet(string label, out NetworkInfo info) => _byLabel.TryGetValue(label, out info!);

    private static Dictionary<string, NetworkInfo> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var parsed = JsonSerializer.Deserialize<Dictionary<string, NetworkInfo>>(json, LauncherJson.Options);
                if (parsed is not null)
                    return new Dictionary<string, NetworkInfo>(parsed, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to read bundled dh-networks.json");
        }
        return new Dictionary<string, NetworkInfo>(StringComparer.OrdinalIgnoreCase);
    }
}
