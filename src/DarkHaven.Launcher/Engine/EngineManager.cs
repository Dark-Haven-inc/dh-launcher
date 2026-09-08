using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Content;
using Serilog;

namespace DarkHaven.Launcher.Engine;

/// <summary>
/// Provides RobustToolbox engine builds and modules. Normally downloads + Ed25519-verifies from the
/// public <c>robust-builds</c> CDN. A version listed in the launcher's <c>bundled-engines/manifest.json</c>
/// is taken from the shipped zip instead and verified by SHA-256 — this is how a private forked engine
/// (Dark Haven's) reaches players, since it is on no public CDN.
/// An engine is "installed" when <c>engines/&lt;version&gt;.zip</c> exists next to a
/// <c>&lt;version&gt;.zip.sig</c> sidecar (an Ed25519 hex signature, or <c>sha256:&lt;hex&gt;</c> for a bundled one).
/// </summary>
public sealed class EngineManager(
    HttpClient http, string enginesDir, string modulesDir, EngineSignature signature, string? bundledEnginesDir = null)
{
    public const string BuildsManifestUrl = "https://robust-builds.cdn.spacestation14.com/manifest.json";
    public const string ModulesManifestUrl = "https://robust-builds.cdn.spacestation14.com/modules.json";
    private static readonly TimeSpan ManifestCacheTime = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _manifestLock = new(1, 1);
    private Dictionary<string, RobustBuildEntry>? _buildManifest;
    private DateTime _buildManifestFetched;
    private Dictionary<string, BundledEngine>? _bundled;

    public string EnginePath(string version) => Path.Combine(enginesDir, $"{version}.zip");
    private string EngineSigPath(string version) => Path.Combine(enginesDir, $"{version}.zip.sig");

    public string EngineSignatureHex(string version)
        => File.ReadAllText(EngineSigPath(version)).Trim();

    public bool IsEngineInstalled(string version)
        => File.Exists(EnginePath(version)) && File.Exists(EngineSigPath(version));

    /// <summary>
    /// Ensures <paramref name="requestedVersion"/> (after following manifest redirects) is on disk.
    /// Returns the resolved version string.
    /// </summary>
    public async Task<string> EnsureEngineAsync(
        string requestedVersion, DownloadProgress? progress = null, CancellationToken cancel = default)
    {
        // A bundled engine wins over the CDN — it exists precisely because the CDN has no matching build.
        if (LoadBundled().TryGetValue(requestedVersion, out var bundled))
        {
            InstallBundled(requestedVersion, bundled);
            return requestedVersion;
        }

        var (version, platform) = await ResolveAsync(requestedVersion, cancel);

        if (IsEngineInstalled(version))
        {
            Log.Debug("Engine {Version} already installed", version);
            return version;
        }

        Directory.CreateDirectory(enginesDir);
        var target = EnginePath(version);
        var tmp = target + ".part";

        Log.Information("Downloading engine {Version} from {Url}", version, platform.Url);
        await DownloadFileAsync(platform.Url, tmp, platform.Sha256, progress, cancel);

        if (!signature.VerifyFile(tmp, platform.Sig))
        {
            File.Delete(tmp);
            throw new InvalidDataException($"Engine {version} failed signature verification");
        }

        File.Move(tmp, target, overwrite: true);
        await File.WriteAllTextAsync(EngineSigPath(version), platform.Sig, cancel);
        Log.Information("Engine {Version} installed", version);
        return version;
    }

    private void InstallBundled(string version, BundledEngine bundled)
    {
        var src = Path.Combine(bundledEnginesDir!, bundled.File);
        if (!File.Exists(src))
            throw new FileNotFoundException($"Bundled engine {version} missing: {src}");

        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(src)));
        if (!actual.Equals(bundled.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Bundled engine {version} SHA-256 mismatch (manifest {bundled.Sha256}, file {actual})");

        Directory.CreateDirectory(enginesDir);
        if (!IsEngineInstalled(version) || EngineSignatureHex(version) != $"sha256:{bundled.Sha256}")
        {
            File.Copy(src, EnginePath(version), overwrite: true);
            File.WriteAllText(EngineSigPath(version), $"sha256:{bundled.Sha256}");
            Log.Information("Installed bundled engine {Version} ({Note})", version, bundled.Note);
        }
    }

    private Dictionary<string, BundledEngine> LoadBundled()
    {
        if (_bundled is not null)
            return _bundled;

        var manifestPath = bundledEnginesDir is null ? null : Path.Combine(bundledEnginesDir, "manifest.json");
        if (manifestPath is null || !File.Exists(manifestPath))
            return _bundled = new();

        try
        {
            _bundled = JsonSerializer.Deserialize<Dictionary<string, BundledEngine>>(
                File.ReadAllText(manifestPath), LauncherJson.Options) ?? new();
            if (_bundled.Count > 0)
                Log.Debug("Bundled engines: {Versions}", string.Join(", ", _bundled.Keys));
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to read bundled-engines manifest");
            _bundled = new();
        }
        return _bundled;
    }

    private sealed record BundledEngine(string File, string Sha256, string? Note = null);

    /// <summary>Ensures an engine module (e.g. <c>Robust.Client.WebView</c>) is extracted to disk.</summary>
    public async Task EnsureModuleAsync(
        string moduleName, string engineVersion, DownloadProgress? progress = null, CancellationToken cancel = default)
    {
        var manifest = await http.GetFromJsonAsync<RobustModuleManifest>(ModulesManifestUrl, LauncherJson.Options, cancel)
                       ?? throw new InvalidDataException("modules.json returned null");

        if (!manifest.Modules.TryGetValue(moduleName, out var module))
            throw new InvalidDataException($"Module '{moduleName}' not in manifest");

        var engineVer = Version.Parse(engineVersion);
        var pick = module.Versions
            .Select(kv => (Ver: Version.Parse(kv.Key), Key: kv.Key, Value: kv.Value))
            .Where(x => engineVer >= x.Ver)
            .OrderByDescending(x => x.Ver)
            .FirstOrDefault();

        if (pick.Key is null)
            throw new InvalidDataException($"No '{moduleName}' version for engine {engineVersion}");

        var destDir = Path.Combine(modulesDir, moduleName, pick.Key);
        if (Directory.Exists(destDir) && Directory.EnumerateFileSystemEntries(destDir).Any())
        {
            Log.Debug("Module {Module} {Version} already installed", moduleName, pick.Key);
            return;
        }

        var rid = RidUtility.FindBest(pick.Value.Platforms.Keys)
                  ?? throw new PlatformNotSupportedException($"No '{moduleName}' build for this platform");
        var platform = pick.Value.Platforms[rid];

        var tmp = Path.Combine(modulesDir, $"{moduleName}-{pick.Key}.part");
        Directory.CreateDirectory(modulesDir);
        await DownloadFileAsync(platform.Url, tmp, platform.Sha256, progress, cancel);

        if (!signature.VerifyFile(tmp, platform.Sig))
        {
            File.Delete(tmp);
            throw new InvalidDataException($"Module {moduleName} {pick.Key} failed signature verification");
        }

        Directory.CreateDirectory(destDir);
        ZipFile.ExtractToDirectory(tmp, destDir, overwriteFiles: true);
        File.Delete(tmp);
        Log.Information("Module {Module} {Version} installed", moduleName, pick.Key);
    }

    private async Task<(string Version, RobustPlatformBuild Platform)> ResolveAsync(string version, CancellationToken cancel)
    {
        var manifest = await GetBuildManifestAsync(cancel);

        var seen = new HashSet<string>();
        while (true)
        {
            if (!manifest.TryGetValue(version, out var entry))
                throw new InvalidDataException($"Engine version '{version}' not found in build manifest");

            if (entry.Redirect is { } redirect)
            {
                if (!seen.Add(version))
                    throw new InvalidDataException($"Engine manifest redirect loop at '{version}'");
                version = redirect;
                continue;
            }

            if (entry.Insecure)
                throw new InvalidDataException($"Engine version '{version}' is marked insecure");

            var rid = RidUtility.FindBest(entry.Platforms.Keys)
                      ?? throw new PlatformNotSupportedException($"No engine {version} build for this platform");
            return (version, entry.Platforms[rid]);
        }
    }

    private async Task<Dictionary<string, RobustBuildEntry>> GetBuildManifestAsync(CancellationToken cancel)
    {
        await _manifestLock.WaitAsync(cancel);
        try
        {
            if (_buildManifest != null && DateTime.UtcNow - _buildManifestFetched < ManifestCacheTime)
                return _buildManifest;

            Log.Debug("Fetching engine build manifest");
            _buildManifest = await http.GetFromJsonAsync<Dictionary<string, RobustBuildEntry>>(
                                 BuildsManifestUrl, LauncherJson.Options, cancel)
                             ?? throw new InvalidDataException("Engine build manifest returned null");
            _buildManifestFetched = DateTime.UtcNow;
            return _buildManifest;
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    private async Task DownloadFileAsync(
        string url, string destPath, string? expectedSha256, DownloadProgress? progress, CancellationToken cancel)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? -1;
        await using var netStream = await resp.Content.ReadAsStreamAsync(cancel);
        await using var fileStream = File.Create(destPath);
        using var sha = SHA256.Create();

        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await netStream.ReadAsync(buffer, cancel)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancel);
            sha.TransformBlock(buffer, 0, read, null, 0);
            done += read;
            progress?.Invoke(done, total, "bytes");
        }
        sha.TransformFinalBlock([], 0, 0);

        if (!string.IsNullOrEmpty(expectedSha256))
        {
            var actual = Convert.ToHexString(sha.Hash!);
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                await fileStream.DisposeAsync();
                File.Delete(destPath);
                throw new InvalidDataException($"SHA256 mismatch for {url}: expected {expectedSha256}, got {actual}");
            }
        }
    }
}
