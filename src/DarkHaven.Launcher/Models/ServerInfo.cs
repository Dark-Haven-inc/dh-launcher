using System.Text.Json.Serialization;

namespace DarkHaven.Launcher.Models;

/// <summary>
/// The <c>GET /info</c> response from a RobustToolbox server.
/// Mirrors <c>StatusHost.Handlers.cs::HandleInfo</c> in the engine.
/// </summary>
public sealed record ServerInfo
{
    [JsonPropertyName("connect_address")]
    public string? ConnectAddress { get; init; }

    [JsonPropertyName("auth")]
    public required ServerAuthInfo Auth { get; init; }

    [JsonPropertyName("build")]
    public ServerBuildInfo? Build { get; init; }

    [JsonPropertyName("desc")]
    public string? Desc { get; init; }

    [JsonPropertyName("privacy_policy")]
    public ServerPrivacyPolicy? PrivacyPolicy { get; init; }

    [JsonPropertyName("links")]
    public ServerLink[]? Links { get; init; }
}

public sealed record ServerAuthInfo
{
    [JsonPropertyName("mode")]
    public required AuthMode Mode { get; init; }

    /// <summary>Base64 Ed25519 public key the client passes back as <c>ROBUST_AUTH_PUBKEY</c>.</summary>
    [JsonPropertyName("public_key")]
    public string? PublicKey { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<AuthMode>))]
public enum AuthMode
{
    Disabled = 0,
    Optional = 1,
    Required = 2,
}

/// <summary>
/// <c>build</c> block of <c>/info</c>. <see cref="Acz"/> true (or an empty <see cref="DownloadUrl"/>)
/// means the server self-hosts content and the URLs must be derived from its API base.
/// </summary>
public sealed record ServerBuildInfo
{
    [JsonPropertyName("engine_version")]
    public required string EngineVersion { get; init; }

    [JsonPropertyName("fork_id")]
    public string? ForkId { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("download_url")]
    public string? DownloadUrl { get; set; }

    [JsonPropertyName("hash")]
    public string? Hash { get; init; }

    [JsonPropertyName("acz")]
    public bool Acz { get; init; }

    [JsonPropertyName("manifest_url")]
    public string? ManifestUrl { get; set; }

    [JsonPropertyName("manifest_download_url")]
    public string? ManifestDownloadUrl { get; set; }

    [JsonPropertyName("manifest_hash")]
    public string? ManifestHash { get; init; }

    /// <summary>True when the server ships a delta-manifest (all modern servers).</summary>
    [JsonIgnore]
    public bool HasManifest => !string.IsNullOrEmpty(ManifestHash);
}

public sealed record ServerPrivacyPolicy
{
    [JsonPropertyName("identifier")]
    public required string Identifier { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("link")]
    public required string Link { get; init; }
}

public sealed record ServerLink
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("icon")]
    public string? Icon { get; init; }
}
