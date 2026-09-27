using System.Runtime.InteropServices;
using System.Text.Json;
using Serilog;

namespace DarkHaven.Launcher.Local;

/// <summary>A server build on the DH CDN that can run on this PC.</summary>
public sealed record LocalBuild(string Version, DateTimeOffset Time, string Url, string Sha256, long? Size)
{
    /// <summary>First 8 characters of the commit the build was made from — enough to tell builds apart.</summary>
    public string ShortVersion => Version.Length > 8 ? Version[..8] : Version;
}

/// <summary>What the CDN has: builds that run here, and how many don't (no server for this platform).</summary>
public sealed record LocalBuildList(IReadOnlyList<LocalBuild> Builds, int WithoutServer, DateTimeOffset? StaleSince);

/// <summary>
/// The server builds ЛОКАЛКА may run, from a manifest in Robust.Cdn's format, filtered to those with a
/// server for this platform. They are NOT the CDN's builds: those carry the server-side anti-cheat,
/// which players must not get their hands on. Local builds are made without it (dh-sector-frontier's
/// DhLocalServer build) and published by <c>scripts/publish-local-server.ps1</c> to the
/// <c>local-servers</c> pre-release of the public releases repo — a pre-release, so the launcher's own
/// updater never looks at it. The last manifest that loaded is kept on disk for offline use.
/// </summary>
public sealed class LocalBuildCatalog(HttpClient http, string manifestUrl, string cachePath)
{
    public const string DefaultManifestUrl =
        "https://github.com/Dark-Haven-inc/frontier15-launcher/releases/download/local-servers/manifest.json";

    /// <summary>
    /// The server platform for this PC, as Robust.Cdn names it: <c>win-x64</c> or <c>linux-x64</c> (the launcher
    /// ships for both). Anything else finds no builds, and ЛОКАЛКА says there's none for this system yet.
    /// </summary>
    public static string Rid { get; } = RidFor(
        OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux",
        RuntimeInformation.ProcessArchitecture);

    internal static string RidFor(string os, Architecture arch) => $"{os}-{arch.ToString().ToLowerInvariant()}";

    public async Task<LocalBuildList> GetAsync(CancellationToken cancel = default)
    {
        try
        {
            var json = await http.GetStringAsync(manifestUrl, cancel);
            var list = Parse(json, Rid);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await File.WriteAllTextAsync(cachePath, json, cancel);
            }
            catch (Exception e)
            {
                Log.Debug(e, "Couldn't cache the CDN manifest");
            }
            return list;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            Log.Warning(e, "CDN manifest unavailable, using the cached copy");
            if (!File.Exists(cachePath))
                return new LocalBuildList([], 0, DateTimeOffset.UtcNow);
            var cached = Parse(await File.ReadAllTextAsync(cachePath, cancel), Rid);
            return cached with { StaleSince = File.GetLastWriteTimeUtc(cachePath) };
        }
    }

    /// <summary>Robust.Cdn's <c>/fork/{id}/manifest</c>: <c>{"builds":{"&lt;version&gt;":{"time",…,"server":{"&lt;rid&gt;":{"url","sha256","size"}}}}}</c>.</summary>
    public static LocalBuildList Parse(string json, string rid)
    {
        using var doc = JsonDocument.Parse(json);
        var builds = new List<LocalBuild>();
        var without = 0;
        if (!doc.RootElement.TryGetProperty("builds", out var all) || all.ValueKind != JsonValueKind.Object)
            return new LocalBuildList(builds, 0, null);

        foreach (var entry in all.EnumerateObject())
        {
            var b = entry.Value;
            if (!b.TryGetProperty("server", out var servers)
                || servers.ValueKind != JsonValueKind.Object
                || !servers.TryGetProperty(rid, out var server)
                || Str(server, "url") is not { Length: > 0 } url
                || Str(server, "sha256") is not { Length: > 0 } sha)
            {
                without++;
                continue;
            }

            var time = b.TryGetProperty("time", out var t) && t.TryGetDateTimeOffset(out var parsed) ? parsed : DateTimeOffset.MinValue;
            long? size = server.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : null;
            builds.Add(new LocalBuild(entry.Name, time, url, sha, size));
        }

        builds.Sort((a, b) => b.Time.CompareTo(a.Time));
        return new LocalBuildList(builds, without, null);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
