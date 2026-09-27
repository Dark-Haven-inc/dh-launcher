using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Serilog;

namespace DarkHaven.Launcher.Local;

/// <summary>
/// The SS14 server ships without its own .NET — it needs Microsoft.NETCore.App on the machine. Use
/// one that is installed; if there is none, download Microsoft's runtime (a zip on Windows, a tar.gz
/// on Linux, checked against the SHA-512 in Microsoft's release metadata) into the launcher's own
/// folder and point the server at it with <c>DOTNET_ROOT</c>. Nothing is installed system-wide.
/// </summary>
/// <param name="trySystem">False: always the launcher's own runtime, even when the system has one (tests).</param>
public sealed class DotnetRuntime(HttpClient http, string privateRoot, bool trySystem = true)
{
    private const string Framework = "Microsoft.NETCore.App";

    /// <summary>The host library every runtime root has under <c>host/fxr/&lt;version&gt;</c>, named per system.</summary>
    internal static string HostFxr { get; } =
        OperatingSystem.IsWindows() ? "hostfxr.dll" : OperatingSystem.IsMacOS() ? "libhostfxr.dylib" : "libhostfxr.so";

    /// <summary>
    /// A <c>DOTNET_ROOT</c> that has the runtime, or null when Windows' system-wide install already has it
    /// (the server's own launcher finds that one by itself).
    /// </summary>
    public async Task<string?> EnsureAsync(int major, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        if (trySystem)
        {
            if (OperatingSystem.IsWindows())
            {
                if (HasRuntime(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"), major))
                    return null;
            }
            // Where Linux keeps .NET depends on the distro and how it was installed: name the one found.
            else if (UnixSystemRoots().FirstOrDefault(r => HasRuntime(r, major)) is { } system)
            {
                return system;
            }
        }
        if (HasRuntime(privateRoot, major))
            return privateRoot;

        var rid = LocalBuildCatalog.Rid;
        var metadataUrl = $"https://builds.dotnet.microsoft.com/dotnet/release-metadata/{major}.0/releases.json";
        var metadata = await http.GetStringAsync(metadataUrl, cancel);
        var (url, sha512) = PickRuntimeArchive(metadata, rid)
                            ?? throw new InvalidOperationException($"Microsoft не публикует .NET {major} для этой системы ({rid}) — сервер не запустить.");

        // Launcher 0.3.9 on Linux unpacked the Windows runtime here: nothing in it can run, so it goes.
        if (Directory.Exists(privateRoot) && !HasHost(privateRoot))
            Directory.Delete(privateRoot, recursive: true);
        Directory.CreateDirectory(privateRoot);

        var tarGz = url.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);
        var archive = Path.Combine(privateRoot, tarGz ? ".download-runtime.tar.gz" : ".download-runtime.zip");
        try
        {
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
                await using var input = await response.Content.ReadAsStreamAsync(cancel);
                await using var output = File.Create(archive);
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

            if (tarGz)
            {
                // The tar keeps the Unix permissions (the dotnet executable has to stay executable).
                await using var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gz, privateRoot, overwriteFiles: true, cancel);
            }
            else
            {
                await Task.Run(() => ZipFile.ExtractToDirectory(archive, privateRoot, overwriteFiles: true), cancel);
            }
        }
        finally
        {
            try { File.Delete(archive); } catch { /* best effort */ }
        }

        if (!HasRuntime(privateRoot, major))
            throw new InvalidOperationException($"После распаковки .NET {major} так и не нашёлся — сервер не запустить.");
        Log.Information("Downloaded .NET {Major} runtime ({Rid}) for local servers into {Root}", major, rid, privateRoot);
        return privateRoot;
    }

    /// <summary>A runtime of this major version under <paramref name="root"/>, plus a host for this system that loads it.</summary>
    internal static bool HasRuntime(string? root, int major)
    {
        if (string.IsNullOrEmpty(root) || !HasHost(root))
            return false;
        var shared = Path.Combine(root, "shared", Framework);
        return Directory.Exists(shared)
               && Directory.GetDirectories(shared).Any(d =>
                   Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) && v.Major == major);
    }

    private static bool HasHost(string root)
    {
        var fxr = Path.Combine(root, "host", "fxr");
        return Directory.Exists(fxr) && Directory.GetDirectories(fxr).Any(d => File.Exists(Path.Combine(d, HostFxr)));
    }

    /// <summary>Where Linux installs of .NET live, in the order the .NET host itself looks, then the usual places.</summary>
    private static IEnumerable<string> UnixSystemRoots()
    {
        var arch = RuntimeInformation.ProcessArchitecture.ToString();
        foreach (var name in new[] { $"DOTNET_ROOT_{arch.ToUpperInvariant()}", "DOTNET_ROOT" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } fromEnv)
                yield return fromEnv;

        foreach (var file in new[] { $"/etc/dotnet/install_location_{arch.ToLowerInvariant()}", "/etc/dotnet/install_location" })
        {
            string? line = null;
            try { line = File.Exists(file) ? File.ReadLines(file).FirstOrDefault()?.Trim() : null; }
            catch (IOException) { /* unreadable: skip */ }
            catch (UnauthorizedAccessException) { /* same */ }
            if (line is { Length: > 0 })
                yield return line;
        }

        yield return "/usr/share/dotnet";   // Microsoft's packages, Arch, the default
        yield return "/usr/lib/dotnet";     // Ubuntu's and Debian's own packages
        yield return "/usr/lib64/dotnet";   // Fedora, openSUSE
        yield return "/usr/local/share/dotnet";
        yield return "/opt/dotnet";
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet"); // dotnet-install.sh
    }

    /// <summary>
    /// The newest release's runtime archive for <paramref name="rid"/> from Microsoft's releases.json: the zip
    /// for Windows, the tar.gz everywhere else. By its exact name — the apphost pack comes first in the list
    /// with the same extension, and has no runtime in it.
    /// </summary>
    internal static (string Url, string Sha512)? PickRuntimeArchive(string releasesJson, string rid)
    {
        var wanted = $"dotnet-runtime-{rid}" + (rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz");
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
                if (fileRid == rid && string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)
                    && f.TryGetProperty("url", out var u) && f.TryGetProperty("hash", out var h))
                    return (u.GetString()!, h.GetString()!);
            }
            return null; // releases are newest first; only the newest one matters
        }
        return null;
    }
}
