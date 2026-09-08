using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Headers;
using DarkHaven.ContentDb;
using DarkHaven.Launcher.Models;
using Microsoft.Data.Sqlite;
using Serilog;
using ZstdSharp;

namespace DarkHaven.Launcher.Content;

/// <summary>Progress callback: (done, total, unit).</summary>
public delegate void DownloadProgress(long done, long total, string unit);

/// <summary>
/// Implements the RobustToolbox delta-manifest download protocol (v1): fetch + verify the manifest,
/// diff it against the local content store, then stream the missing blobs over the binary
/// <c>X-Robust-Download-Protocol</c> endpoint into the content DB.
/// </summary>
public sealed class ManifestDownloader(HttpClient http)
{
    private const int ProtocolVersion = 1;
    private const int RecompressSavingsThreshold = 10;

    [Flags]
    private enum StreamFlags
    {
        None = 0,
        PreCompressed = 1 << 0,
    }

    /// <summary>
    /// Downloads any blobs missing for <paramref name="build"/> into <paramref name="versionId"/> and
    /// writes that version's <c>ContentManifest</c> rows. Returns the verified manifest hash.
    /// Appends every newly-inserted Content row id to <paramref name="insertedContentIds"/> so an
    /// interrupted download can be salvaged by the caller.
    /// </summary>
    public async Task<byte[]> DownloadAsync(
        SqliteConnection con,
        long versionId,
        ServerBuildInfo build,
        List<long> insertedContentIds,
        DownloadProgress? progress,
        CancellationToken cancel)
    {
        if (string.IsNullOrEmpty(build.ManifestUrl)
            || string.IsNullOrEmpty(build.ManifestDownloadUrl)
            || string.IsNullOrEmpty(build.ManifestHash))
            throw new InvalidOperationException("Server build info has no delta manifest URLs");

        var manifest = await FetchManifestAsync(build.ManifestUrl!, build.ManifestHash!, cancel);
        Log.Debug("Manifest has {Count} entries", manifest.Entries.Count);

        var toDownload = CalculateMissing(con, manifest);
        Log.Debug("Need to download {Count} blobs", toDownload.Count);

        if (toDownload.Count > 0)
            await DownloadMissingAsync(con, build.ManifestDownloadUrl!, manifest, toDownload, insertedContentIds, progress, cancel);

        FillManifestRows(con, versionId, manifest);
        return manifest.ManifestHash;
    }

    private async Task<ContentManifest> FetchManifestAsync(string url, string expectedHash, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("zstd"));

        using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        resp.EnsureSuccessStatusCode();

        await using var netStream = await resp.Content.ReadAsStreamAsync(cancel);
        await using var body = WrapZstdIfNeeded(netStream, resp.Content.Headers.ContentEncoding);

        using var ms = new MemoryStream();
        await body.CopyToAsync(ms, cancel);
        return ContentManifest.ParseAndVerify(ms.ToArray(), expectedHash);
    }

    private static List<int> CalculateMissing(SqliteConnection con, ContentManifest manifest)
    {
        using var find = con.CreateCommand();
        find.CommandText = "SELECT 1 FROM Content WHERE Hash = $hash LIMIT 1";
        var hashParam = find.Parameters.Add("$hash", SqliteType.Blob);

        var missing = new List<int>();
        var queued = new HashSet<string>();

        for (var i = 0; i < manifest.Entries.Count; i++)
        {
            var entry = manifest.Entries[i];
            var key = Convert.ToHexString(entry.Hash);
            if (!queued.Add(key))
                continue;

            hashParam.Value = entry.Hash;
            if (find.ExecuteScalar() is null)
                missing.Add(i);
            else
                queued.Remove(key); // present already; let a later identical entry skip cheaply too
        }

        return missing;
    }

    private async Task DownloadMissingAsync(
        SqliteConnection con,
        string downloadUrl,
        ContentManifest manifest,
        List<int> toDownload,
        List<long> insertedContentIds,
        DownloadProgress? progress,
        CancellationToken cancel)
    {
        await CheckProtocolAsync(downloadUrl, cancel);

        var requestBody = new byte[toDownload.Count * 4];
        for (var i = 0; i < toDownload.Count; i++)
            BinaryPrimitives.WriteInt32LittleEndian(requestBody.AsSpan(i * 4, 4), toDownload[i]);

        using var request = new HttpRequestMessage(HttpMethod.Post, downloadUrl);
        request.Headers.Add("X-Robust-Download-Protocol", ProtocolVersion.ToString(CultureInfo.InvariantCulture));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("zstd"));
        request.Content = new ByteArrayContent(requestBody);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        resp.EnsureSuccessStatusCode();

        await using var netStream = await resp.Content.ReadAsStreamAsync(cancel);
        await using var stream = WrapZstdIfNeeded(netStream, resp.Content.Headers.ContentEncoding);

        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancel);
        var flags = (StreamFlags)BinaryPrimitives.ReadInt32LittleEndian(header);
        var preCompressed = (flags & StreamFlags.PreCompressed) != 0;
        Log.Debug("Download stream flags: {Flags}", flags);

        using var decompressor = preCompressed ? new Decompressor() : null;
        using var compressor = preCompressed ? null : new Compressor(10);

        using var insert = con.CreateCommand();
        insert.CommandText = """
            INSERT INTO Content (Hash, Size, Compression, Data) VALUES ($hash, $size, $c, $data)
            ON CONFLICT(Hash) DO UPDATE SET Hash = Hash
            RETURNING Id
            """;
        var pHash = insert.Parameters.Add("$hash", SqliteType.Blob);
        var pSize = insert.Parameters.Add("$size", SqliteType.Integer);
        var pComp = insert.Parameters.Add("$c", SqliteType.Integer);
        var pData = insert.Parameters.Add("$data", SqliteType.Blob);

        var fileHeader = new byte[preCompressed ? 8 : 4];
        long doneBytes = 0;

        for (var i = 0; i < toDownload.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var entry = manifest.Entries[toDownload[i]];

            await stream.ReadExactlyAsync(fileHeader, cancel);
            var uncompressedLen = BinaryPrimitives.ReadInt32LittleEndian(fileHeader.AsSpan(0, 4));

            var data = new byte[uncompressedLen];
            var storeCompression = ContentCompressionScheme.None;
            byte[] storeData;

            if (preCompressed)
            {
                var compressedLen = BinaryPrimitives.ReadInt32LittleEndian(fileHeader.AsSpan(4, 4));
                if (compressedLen > 0)
                {
                    var compressed = new byte[compressedLen];
                    await stream.ReadExactlyAsync(compressed, cancel);
                    var produced = decompressor!.Unwrap(compressed);
                    if (produced.Length != uncompressedLen)
                        throw new InvalidDataException($"Blob {i}: decompressed to {produced.Length}, expected {uncompressedLen}");
                    produced.CopyTo(data);
                    storeCompression = ContentCompressionScheme.ZStd;
                    storeData = compressed;
                }
                else
                {
                    await stream.ReadExactlyAsync(data, cancel);
                    storeData = data;
                }
            }
            else
            {
                await stream.ReadExactlyAsync(data, cancel);
                var recompressed = compressor!.Wrap((ReadOnlySpan<byte>)data).ToArray();
                if (recompressed.Length + RecompressSavingsThreshold < uncompressedLen)
                {
                    storeCompression = ContentCompressionScheme.ZStd;
                    storeData = recompressed;
                }
                else
                {
                    storeData = data;
                }
            }

            var actualHash = Blake2.Hash(data);
            if (!actualHash.AsSpan().SequenceEqual(entry.Hash))
                throw new InvalidDataException(
                    $"Blob hash mismatch for '{entry.Path}': expected {Convert.ToHexString(entry.Hash)}, got {Convert.ToHexString(actualHash)}");

            pHash.Value = entry.Hash;
            pSize.Value = uncompressedLen;
            pComp.Value = (int)storeCompression;
            pData.Value = storeData;
            var contentId = Convert.ToInt64(insert.ExecuteScalar());
            insertedContentIds.Add(contentId);

            doneBytes += uncompressedLen;
            progress?.Invoke(doneBytes, 0, "bytes");
            progress?.Invoke(i + 1, toDownload.Count, "files");
        }

        progress?.Invoke(toDownload.Count, toDownload.Count, "files");
        Log.Debug("Downloaded {Count} blobs, {Bytes} uncompressed bytes", toDownload.Count, doneBytes);
    }

    private async Task CheckProtocolAsync(string url, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, url);
        using var resp = await http.SendAsync(request, cancel);
        resp.EnsureSuccessStatusCode();

        if (!resp.Headers.TryGetValues("X-Robust-Download-Min-Protocol", out var minH)
            || !resp.Headers.TryGetValues("X-Robust-Download-Max-Protocol", out var maxH)
            || !int.TryParse(minH.First(), out var min)
            || !int.TryParse(maxH.First(), out var max))
        {
            throw new InvalidDataException("Missing / invalid X-Robust-Download-*-Protocol headers on OPTIONS");
        }

        if (min > ProtocolVersion || max < ProtocolVersion)
            throw new InvalidDataException($"Server download protocol [{min}, {max}] does not include v{ProtocolVersion}");
    }

    private static void FillManifestRows(SqliteConnection con, long versionId, ContentManifest manifest)
    {
        using var findContent = con.CreateCommand();
        findContent.CommandText = "SELECT Id FROM Content WHERE Hash = $hash";
        var pHash = findContent.Parameters.Add("$hash", SqliteType.Blob);

        using var insert = con.CreateCommand();
        insert.CommandText = "INSERT INTO ContentManifest (VersionId, Path, ContentId) VALUES ($v, $p, $c)";
        insert.Parameters.AddWithValue("$v", versionId);
        var pPath = insert.Parameters.Add("$p", SqliteType.Text);
        var pContent = insert.Parameters.Add("$c", SqliteType.Integer);

        foreach (var entry in manifest.Entries)
        {
            pHash.Value = entry.Hash;
            var contentId = findContent.ExecuteScalar()
                            ?? throw new InvalidOperationException($"Missing content blob for '{entry.Path}' after download");
            pPath.Value = entry.Path;
            pContent.Value = Convert.ToInt64(contentId);
            insert.ExecuteNonQuery();
        }
    }

    private static Stream WrapZstdIfNeeded(Stream inner, IEnumerable<string> contentEncoding)
        => contentEncoding.Contains("zstd") ? new DecompressionStream(inner) : inner;
}
