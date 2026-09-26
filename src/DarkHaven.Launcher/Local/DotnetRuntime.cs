using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Serilog;

namespace DarkHaven.Launcher.Local;

/// <summary>
/// The SS14 server ships without its own .NET — it needs Microsoft.NETCore.App on the machine. Use
/// one that is installed; if there is none, download Microsoft's runtime zip (checked against the
/// SHA-512 in Microsoft's release metadata) into the launcher's own folder and point the server at it
/// with <c>DOTNET_ROOT</c>. Nothing is installed system-wide.
/// </summary>
public sealed class DotnetRuntime(HttpClient http, string privateRoot)
{
    private const string Framework = "Microsoft.NETCore.App";

    /// <summary>
    /// A <c>DOTNET_ROOT</c> that has the runtime, or null when the system-wide install already has it
    /// (the server's own launcher finds that one by itself).
    /// </summary>
    public async Task<string?> EnsureAsync(int major, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        if (HasRuntime(SystemRoot(), major))
            return null;
        if (HasRuntime(privateRoot, major))
            return privateRoot;

        var metadataUrl = $"https://builds.dotnet.microsoft.com/dotnet/release-metadata/{major}.0/releases.json";
        var metadata = await http.GetStringAsync(metadataUrl, cancel);
        var (url, sha512) = PickRuntimeZip(metadata, LocalBuildCatalog.Rid)
                            ?? throw new InvalidOperationException($"Microsoft не публикует .NET {major} для Windows x64 — сервер не запустить.");

        Directory.CreateDirectory(privateRoot);
        var zip = Path.Combine(privateRoot, ".download-runtime.zip");
        try
        {
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
                await using var input = await response.Content.ReadAsStreamAsync(cancel);
                await using var output = File.Create(zip);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancel)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                    done += read;
                    if (total > 0)
                        progress?.Report((double)done / total.Value);
                }
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha512, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(".NET скачался повреждённым (контрольная сумма не совпала). Попробуйте ещё раз.");
            }

            await Task.Run(() => ZipFile.ExtractToDirectory(zip, privateRoot, overwriteFiles: true), cancel);
        }
        finally
        {
            try { File.Delete(zip); } catch { /* best effort */ }
        }

        if (!HasRuntime(privateRoot, major))
            throw new InvalidOperationException($"После распаковки .NET {major} так и не нашёлся — сервер не запустить.");
        Log.Information("Downloaded .NET {Major} runtime for local servers into {Root}", major, privateRoot);
        return privateRoot;
    }

    /// <summary>A runtime of this major version under <paramref name="root"/>, plus the host that loads it.</summary>
    internal static bool HasRuntime(string? root, int major)
    {
        if (string.IsNullOrEmpty(root))
            return false;
        var shared = Path.Combine(root, "shared", Framework);
        var fxr = Path.Combine(root, "host", "fxr");
        return Directory.Exists(shared) && Directory.Exists(fxr)
               && Directory.GetDirectories(shared).Any(d =>
                   Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) && v.Major == major);
    }

    private static string? SystemRoot() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet")
            : null;

    /// <summary>The newest release's runtime zip for <paramref name="rid"/> from Microsoft's releases.json.</summary>
    internal static (string Url, string Sha512)? PickRuntimeZip(string releasesJson, string rid)
    {
        using var doc = JsonDocument.Parse(releasesJson);
        if (!doc.RootElement.TryGetProperty("releases", out var releases))
            return null;

        foreach (var release in releases.EnumerateArray())
        {
            if (!release.TryGetProperty("runtime", out var runtime) || !runtime.TryGetProperty("files", out var files))
                continue;
            foreach (var f in files.EnumerateArray())
            {
                var name = f.TryGetProperty("name", out var n) ? n.GetString() : null;
                var fileRid = f.TryGetProperty("rid", out var r) ? r.GetString() : null;
                if (fileRid == rid && name is not null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    && f.TryGetProperty("url", out var u) && f.TryGetProperty("hash", out var h))
                    return (u.GetString()!, h.GetString()!);
            }
            return null; // releases are newest first; only the newest one matters
        }
        return null;
    }
}
