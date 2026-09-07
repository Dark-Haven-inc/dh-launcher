using System.Text.Json.Serialization;

namespace DarkHaven.Launcher.Models;

/// <summary>
/// The <c>GET /status</c> response. Most fields are content-supplied and optional;
/// only <see cref="Name"/> and <see cref="Players"/> are guaranteed by the engine.
/// </summary>
public sealed record ServerStatus
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("players")]
    public int Players { get; init; }

    [JsonPropertyName("soft_max_players")]
    public int SoftMaxPlayers { get; init; }

    [JsonPropertyName("map")]
    public string? Map { get; init; }

    [JsonPropertyName("round_id")]
    public int? RoundId { get; init; }

    [JsonPropertyName("preset")]
    public string? Preset { get; init; }

    [JsonPropertyName("round_start_time")]
    public string? RoundStartTime { get; init; }

    [JsonPropertyName("run_level")]
    public RunLevel? RunLevel { get; init; }

    [JsonPropertyName("panic_bunker")]
    public bool PanicBunker { get; init; }

    [JsonPropertyName("tags")]
    public string[]? Tags { get; init; }
}

public enum RunLevel
{
    PreRoundLobby = 0,
    InRound = 1,
    PostRound = 2,
}

/// <summary>One entry of <c>GET {hub}/api/servers</c>.</summary>
public sealed record HubServerEntry
{
    [JsonPropertyName("address")]
    public required string Address { get; init; }

    [JsonPropertyName("statusData")]
    public ServerStatus? StatusData { get; init; }

    [JsonPropertyName("inferredTags")]
    public string[]? InferredTags { get; init; }
}
