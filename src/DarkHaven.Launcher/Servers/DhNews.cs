using System.Text.Json;
using System.Text.Json.Serialization;
using DarkHaven.Launcher.Api;
using Serilog;

namespace DarkHaven.Launcher.Servers;

/// <summary>One entry of <c>news.json</c>.</summary>
public sealed record DhNewsItem
{
    /// <summary>Stable id, used to dedupe and to remember what the player has already seen.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    /// <summary>ISO date (<c>yyyy-MM-dd</c>). Drives ordering and the "N дней назад" label.</summary>
    [JsonPropertyName("date")]
    public string Date { get; init; } = "";

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("body")]
    public string Body { get; init; } = "";

    /// <summary>Short category chip, e.g. "Лаунчер" / "Сеть" / "Событие". Optional.</summary>
    [JsonPropertyName("tag")]
    public string? Tag { get; init; }

    /// <summary>Optional "подробнее" link opened in the browser.</summary>
    [JsonPropertyName("link")]
    public string? Link { get; init; }

    /// <summary>Kept at the top regardless of date.</summary>
    [JsonPropertyName("pinned")]
    public bool Pinned { get; init; }

    [JsonIgnore]
    public DateOnly? ParsedDate =>
        DateOnly.TryParse(Date, out var d) ? d : null;
}

/// <summary>
/// The НОВОСТИ feed. Ships a bundled <c>news.json</c> and, if a <c>NewsUrl</c> is configured,
/// refreshes from there with a write-through cache — same shape as <see cref="HubApi"/> and
/// <see cref="DhRegions"/>. No backend: the remote is just a static JSON file.
/// </summary>
public sealed class DhNews(HttpClient http, string bundledJsonPath, string? cachePath = null, string? remoteUrl = null)
{
    public IReadOnlyList<DhNewsItem> Items { get; private set; } = [];

    /// <summary>True when the last <see cref="LoadAsync"/> served the on-disk cache after a failed fetch.</summary>
    public bool ServedFromCache { get; private set; }

    public async Task LoadAsync(CancellationToken cancel = default)
    {
        ServedFromCache = false;
        var items = ReadFile(bundledJsonPath);

        // A newer cached copy from a previous run beats the bundled one.
        if (cachePath is not null && File.Exists(cachePath))
        {
            var cached = ReadFile(cachePath);
            if (cached.Count > 0)
                items = cached;
        }

        if (!string.IsNullOrWhiteSpace(remoteUrl))
        {
            try
            {
                var json = await http.GetStringAsync(remoteUrl, cancel);
                var remote = Parse(json);
                if (remote.Count > 0)
                {
                    items = remote;
                    if (cachePath is not null)
                    {
                        try { await File.WriteAllTextAsync(cachePath, json, cancel); }
                        catch (Exception e) { Log.Debug(e, "Could not cache news to {Path}", cachePath); }
                    }
                }
            }
            catch (Exception e)
            {
                ServedFromCache = cachePath is not null && File.Exists(cachePath);
                Log.Warning(e, "Could not refresh news from {Url}, using {Fallback}",
                    remoteUrl, ServedFromCache ? "cache" : "bundled feed");
            }
        }

        Items = Order(items);
    }

    private static IReadOnlyList<DhNewsItem> Order(IEnumerable<DhNewsItem> items) =>
        items
            .GroupBy(i => string.IsNullOrEmpty(i.Id) ? Guid.NewGuid().ToString() : i.Id)
            .Select(g => g.First())
            .OrderByDescending(i => i.Pinned)
            .ThenByDescending(i => i.ParsedDate ?? DateOnly.MinValue)
            .ToList();

    private static IReadOnlyList<DhNewsItem> ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : [];
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not read news feed {Path}", path);
            return [];
        }
    }

    private static IReadOnlyList<DhNewsItem> Parse(string json) =>
        JsonSerializer.Deserialize<DhNewsItem[]>(json, LauncherJson.Options) ?? [];
}
