using System.Text.Json.Serialization;

namespace DarkHaven.Launcher.Engine;

/// <summary>
/// <c>robust-builds.cdn.spacestation14.com/manifest.json</c> — a map of engine version → build info.
/// </summary>
public sealed record RobustBuildEntry
{
    [JsonPropertyName("insecure")]
    public bool Insecure { get; init; }

    /// <summary>If set, this version is an alias — resolve to this one instead.</summary>
    [JsonPropertyName("redirect")]
    public string? Redirect { get; init; }

    [JsonPropertyName("platforms")]
    public Dictionary<string, RobustPlatformBuild> Platforms { get; init; } = new();
}

public sealed record RobustPlatformBuild
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    /// <summary>Ed25519 signature (hex) of the zip, verified against the SS14 signing key.</summary>
    [JsonPropertyName("sig")]
    public required string Sig { get; init; }
}

/// <summary>
/// <c>modules.json</c> — engine modules (e.g. <c>Robust.Client.WebView</c>/CEF) keyed by name then version.
/// </summary>
public sealed record RobustModuleManifest
{
    [JsonPropertyName("modules")]
    public Dictionary<string, RobustModule> Modules { get; init; } = new();
}

public sealed record RobustModule
{
    [JsonPropertyName("versions")]
    public Dictionary<string, RobustModuleVersion> Versions { get; init; } = new();
}

public sealed record RobustModuleVersion
{
    [JsonPropertyName("insecure")]
    public bool Insecure { get; init; }

    [JsonPropertyName("platforms")]
    public Dictionary<string, RobustPlatformBuild> Platforms { get; init; } = new();
}
