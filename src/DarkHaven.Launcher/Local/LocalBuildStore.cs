using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DarkHaven.Launcher.Local;

/// <summary>
/// Server builds downloaded for ЛОКАЛКА, one folder per version under <c>local/builds</c>. A build
/// counts as installed only once its zip matched the CDN's SHA-256 and unpacked completely — a
/// download cut off halfway never looks like a usable server.
/// </summary>
public sealed partial class LocalBuildStore(HttpClient http, string root)
{
    private const string CompleteMarker = ".complete";
    private const string ServerExe = "Robust.Server.exe";

    public string PathFor(string version) => Path.Combine(root, Safe(version));

    public bool IsInstalled(string version) => File.Exists(Path.Combine(PathFor(version), CompleteMarker));

    public IReadOnlyList<string> Installed() =>
        Directory.Exists(root)
            ? Directory.GetDirectories(root)
                .Where(d => File.Exists(Path.Combine(d, CompleteMarker)))
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToList()
            : [];

    /// <summary>Where the server's executable is inside an installed build, or null if it isn't there.</summary>
    public string? ServerExecutable(string version)
    {
        var dir = PathFor(version);
        var direct = Path.Combine(dir, ServerExe);
        if (File.Exists(direct))
            return direct;
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, ServerExe, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 })
                .FirstOrDefault()
            : null;
    }

    /// <summary>
    /// The .NET major version the server needs, from <c>Robust.Server.runtimeconfig.json</c>
    /// (the server ships without its own runtime).
    /// </summary>
    public int? RequiredDotnetMajor(string version)
    {
        var exe = ServerExecutable(version);
        if (exe is null)
            return null;
        var config = Path.ChangeExtension(exe, null) + ".runtimeconfig.json";
        return File.Exists(config) ? ReadDotnetMajor(File.ReadAllText(config)) : null;
    }

    internal static int? ReadDotnetMajor(string runtimeConfigJson)
    {
        using var doc = JsonDocument.Parse(runtimeConfigJson);
        if (!doc.RootElement.TryGetProperty("runtimeOptions", out var options))
            return null;

        IEnumerable<JsonElement> frameworks =
            options.TryGetProperty("frameworks", out var many) && many.ValueKind == JsonValueKind.Array ? many.EnumerateArray()
            : options.TryGetProperty("framework", out var one) ? [one]
            : [];

        foreach (var f in frameworks)
        {
            if (f.TryGetProperty("name", out var name) && name.GetString() == "Microsoft.NETCore.App"
                && f.TryGetProperty("version", out var ver) && Version.TryParse(ver.GetString(), out var v))
                return v.Major;
        }
        return null;
    }

    /// <summary>Downloads the build, checks it against the CDN's SHA-256 and unpacks it.</summary>
    public async Task InstallAsync(LocalBuild build, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        if (IsInstalled(build.Version))
            return;

        Directory.CreateDirectory(root);
        var safe = Safe(build.Version);
        var zip = Path.Combine(root, $".download-{safe}.zip");
        var unpack = Path.Combine(root, $".unpack-{safe}");
        try
        {
            await DownloadVerifiedAsync(build, zip, progress, cancel);

            if (Directory.Exists(unpack))
                Directory.Delete(unpack, recursive: true);
            // ExtractToDirectory refuses entries that would land outside the folder ("zip slip").
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, unpack), cancel);
            await File.WriteAllTextAsync(Path.Combine(unpack, CompleteMarker), build.Sha256, cancel);

            var target = PathFor(build.Version);
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            Directory.Move(unpack, target);
        }
        finally
        {
            TryDelete(zip);
            if (Directory.Exists(unpack))
                try { Directory.Delete(unpack, recursive: true); } catch { /* next install cleans it */ }
        }
    }

    public void Delete(string version)
    {
        var dir = PathFor(version);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    private async Task DownloadVerifiedAsync(LocalBuild build, string path, IProgress<double>? progress, CancellationToken cancel)
    {
        using var response = await http.GetAsync(build.Url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? build.Size;

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var input = await response.Content.ReadAsStreamAsync(cancel))
        await using (var output = File.Create(path))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancel)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                if (total > 0)
                    progress?.Report((double)done / total.Value);
            }
        }

        var actual = Convert.ToHexString(sha.GetHashAndReset());
        if (!actual.Equals(build.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Сборка {build.ShortVersion} скачалась повреждённой (контрольная сумма не совпала). Попробуйте ещё раз.");
    }

    /// <summary>CDN versions are commit hashes; anything else is squeezed into a safe folder name.</summary>
    internal static string Safe(string version)
    {
        var s = UnsafeChars().Replace(version, "_");
        return s is "" or "." or ".." ? "_" : s;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeChars();
}
