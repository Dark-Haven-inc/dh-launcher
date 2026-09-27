using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DarkHaven.Launcher.Local;

/// <summary>
/// Server builds downloaded for ЛОКАЛКА, one folder per version under <c>local/builds</c>. A build
/// counts as installed only once its zip matched the CDN's SHA-256 and unpacked completely — a
/// download cut off halfway never looks like a usable server.
/// </summary>
/// <remarks>
/// Folders are named by the first <see cref="FolderLength"/> characters of the version (a commit
/// hash), not all 40: the server's resources nest deep (120 characters and growing), and Windows
/// without long paths enabled can't open anything past 259. The full version is kept in the
/// marker file, so two builds sharing a prefix are never mistaken for each other.
/// </remarks>
public sealed partial class LocalBuildStore(HttpClient http, string root, string? rid = null)
{
    private const string CompleteMarker = ".complete";
    internal const int FolderLength = 12;

    /// <summary>The server's executable in a build for this platform.</summary>
    public string ServerExe { get; } = ServerExeFor(rid ?? LocalBuildCatalog.Rid);

    /// <summary><c>Robust.Server.exe</c> on Windows; elsewhere the apphost has no extension.</summary>
    internal static string ServerExeFor(string rid) =>
        rid.StartsWith("win-", StringComparison.Ordinal) ? "Robust.Server.exe" : "Robust.Server";

    /// <summary>Longest full path Windows opens when long paths aren't enabled (MAX_PATH minus the terminator).</summary>
    internal const int MaxPath = 259;

    public string PathFor(string version)
    {
        var safe = Safe(version);
        return Path.Combine(root, safe.Length > FolderLength ? safe[..FolderLength] : safe);
    }

    /// <summary>
    /// Downloaded completely, and a server for this platform: launcher 0.3.9 on Linux fetched the Windows build,
    /// which counts as not installed here and gets replaced.
    /// </summary>
    public bool IsInstalled(string version) => MarkerVersion(PathFor(version)) == version && ServerExecutable(version) is not null;

    /// <summary>The versions on disk, as the CDN names them.</summary>
    public IReadOnlyList<string> Installed() =>
        Directory.Exists(root)
            ? Directory.GetDirectories(root)
                .Select(MarkerVersion)
                .OfType<string>()
                .ToList()
            : [];

    private static string? MarkerVersion(string dir)
    {
        var marker = Path.Combine(dir, CompleteMarker);
        try
        {
            return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

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
        var target = PathFor(build.Version);
        var folder = Path.GetFileName(target);
        var zip = Path.Combine(root, $".download-{folder}.zip");
        var unpack = Path.Combine(root, $".u-{folder}");
        try
        {
            await DownloadVerifiedAsync(build, zip, progress, cancel);
            EnsurePathsFit(zip, target, LongPathsEnabled());

            if (Directory.Exists(unpack))
                Directory.Delete(unpack, recursive: true);
            // ExtractToDirectory refuses entries that would land outside the folder ("zip slip").
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, unpack), cancel);
            if (!OperatingSystem.IsWindows()
                && Directory.EnumerateFiles(unpack, ServerExe, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 })
                    .FirstOrDefault() is { } exe)
                MakeExecutable(exe);
            await File.WriteAllTextAsync(Path.Combine(unpack, CompleteMarker), build.Version, cancel);

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

    /// <summary>
    /// The server reads its resources with ordinary Windows paths: one that doesn't fit makes it die at
    /// start with "Path does not exist in the VFS". Say so plainly instead, before unpacking anything.
    /// </summary>
    internal static void EnsurePathsFit(string zipPath, string target, bool longPathsEnabled)
    {
        if (longPathsEnabled)
            return;
        using var archive = ZipFile.OpenRead(zipPath);
        var longest = archive.Entries.Select(e => e.FullName).MaxBy(n => n.Length);
        if (longest is not null && Path.GetFullPath(target).Length + 1 + longest.Length > MaxPath)
            throw new InvalidOperationException(
                "Папка лаунчера лежит слишком глубоко: пути к файлам сервера выходят за 260 символов, а Windows такие не открывает. " +
                "Включите в Windows длинные пути (параметр LongPathsEnabled) или перенесите папку данных лаунчера ближе к корню диска.");
    }

    private static bool LongPathsEnabled()
    {
        if (!OperatingSystem.IsWindows())
            return true;
        try
        {
            return Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 0) is 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Server zips are packed on Windows, which stores no Unix permissions: unpacked on Linux, Robust.Server
    /// isn't executable and the process won't start.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    internal static void MakeExecutable(string path)
    {
        const UnixFileMode exec = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        var mode = File.GetUnixFileMode(path);
        if ((mode & exec) != exec)
            File.SetUnixFileMode(path, mode | exec);
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
