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
/// The server builds the DH CDN (Robust.Cdn) publishes for our fork, filtered to those with a server
/// for this platform. It's the same manifest the live server is deployed from, so the newest build
/// here is what the live server runs. The last manifest that loaded is kept on disk for offline use.
/// </summary>
public sealed class LocalBuildCatalog(HttpClient http, string manifestUrl, string cachePath)
{
    public const string DefaultManifestUrl = "https://cdn.dark-haven.xyz/fork/Dark-Haven/manifest";

    /// <summary>The only server platform ЛОКАЛКА runs today.</summary>
    public const string Rid = "win-x64";

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
