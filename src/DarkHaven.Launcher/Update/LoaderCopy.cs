using System.Security.Cryptography;
using DarkHaven.Launcher.Security;
using Serilog;

namespace DarkHaven.Launcher.Update;

/// <summary>
/// Inside an AppImage the launcher's files live on a FUSE mount that goes away when the launcher exits,
/// while the game it started keeps running and keeps reading its loader's files. So there the game is
/// started from a copy of the loader under the data directory, one per launcher version.
/// <para>
/// A release guard starts the loader only from a directory holding exactly the files it pins (docs/GUARD.md), and
/// the copy outlives any reinstall of the AppImage. So before each launch the copy is compared with the AppImage's
/// loader and made again if anything drifted, and a copy the guard refuses all the same is made again once.
/// </para>
/// </summary>
public static class LoaderCopy
{
    private const string CompleteMarker = ".complete";

    /// <summary>Where the copies go: one directory per launcher version under <see cref="Root"/>.</summary>
    internal sealed record Copies(string Root, string Version);

    private static readonly object Gate = new();

    /// <summary>
    /// Starts the game with <paramref name="start"/>, given the loader to start: <paramref name="loaderPath"/> itself,
    /// or its copy when running from an AppImage.
    /// </summary>
    public static T Start<T>(string loaderPath, Func<string, T> start) =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 }
            ? Start(loaderPath, new Copies(Path.Combine(LauncherPaths.DataDir, "loader"), LauncherInfo.Version), start)
            : start(loaderPath);

    internal static T Start<T>(string loaderPath, Copies copies, Func<string, T> start)
    {
        var loader = Prepare(loaderPath, copies);
        if (loader == loaderPath)
            return start(loader);

        try
        {
            return start(loader);
        }
        catch (GuardException e) when (e.Status == GuardStatus.Refused)
        {
            // It matched the AppImage's loader a moment ago, and the guard still turned it down. A fresh copy is all
            // there is left to try here: it is what reinstalling the launcher would come down to.
            Log.Warning("The guard refused the loader copy ({Error}); copying it again", e.Message);
            Discard(loader);
            return start(Prepare(loaderPath, copies));
        }
    }

    /// <summary>The loader to start: the copy of <paramref name="loaderPath"/>'s directory, made or made again as needed.</summary>
    internal static string Prepare(string loaderPath, Copies copies)
    {
        lock (Gate)
        {
            try
            {
                var source = Path.GetDirectoryName(loaderPath)!;
                var dest = Path.Combine(copies.Root, copies.Version);

                if (!File.Exists(Path.Combine(dest, CompleteMarker)))
                {
                    Copy(source, dest);
                    Log.Information("Loader copied out of the AppImage to {Dir}", dest);
                }
                else if (Drift(source, dest) is { } drift)
                {
                    Log.Warning("The loader copy in {Dir} no longer matches the launcher's ({Drift}); copying it again", dest, drift);
                    Copy(source, dest);
                }

                // Earlier versions' copies. A game still running from one keeps its open files.
                foreach (var dir in Directory.GetDirectories(copies.Root))
                {
                    if (dir == dest)
                        continue;
                    try { Directory.Delete(dir, recursive: true); }
                    catch (Exception e) { Log.Debug(e, "Could not remove old loader copy {Dir}", dir); }
                }

                return Path.Combine(dest, Path.GetFileName(loaderPath));
            }
            catch (Exception e)
            {
                Log.Warning(e, "Could not copy the loader out of the AppImage; the game will stop if the launcher is closed");
                return loaderPath;
            }
        }
    }

    /// <summary>
    /// Why <paramref name="copy"/> is not a copy of <paramref name="source"/>, or null if it is: the same files by
    /// relative path, each a regular file of the same size and SHA-256, no links, and nothing else but the marker.
    /// </summary>
    internal static string? Drift(string source, string copy)
    {
        var expected = Files(source, out _);
        var actual = Files(copy, out var link);
        if (link is not null)
            return $"{link} is a link";
        actual.Remove(CompleteMarker);

        // Names and sizes first: cheap, and what most damage shows up as.
        foreach (var (path, file) in expected)
        {
            if (!actual.TryGetValue(path, out var other))
                return $"{path} is missing";
            if (other.Length != file.Length)
                return $"{path} has changed";
        }
        if (actual.Keys.FirstOrDefault(path => !expected.ContainsKey(path)) is { } extra)
            return $"{extra} is not the loader's";

        foreach (var (path, file) in expected)
        {
            if (!Sha256(file).AsSpan().SequenceEqual(Sha256(actual[path])))
                return $"{path} has changed";
        }
        return null;
    }

    /// <summary>The regular files under <paramref name="dir"/> by '/'-separated relative path; the first link found, if any.</summary>
    private static Dictionary<string, FileInfo> Files(string dir, out string? link)
    {
        link = null;
        var files = new Dictionary<string, FileInfo>(StringComparer.Ordinal);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 };
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
        {
            var path = Path.GetRelativePath(dir, entry.FullName).Replace('\\', '/');
            if (entry.LinkTarget is not null)
                link ??= path;
            else if (entry is FileInfo file)
                files[path] = file;
        }
        return files;
    }

    private static byte[] Sha256(FileInfo file)
    {
        using var stream = file.OpenRead();
        return SHA256.HashData(stream);
    }

    private static void Copy(string source, string dest)
    {
        var tmp = $"{dest}.tmp-{Environment.ProcessId}";
        if (Directory.Exists(tmp))
            Directory.Delete(tmp, recursive: true);

        CopyDirectory(source, tmp);
        File.WriteAllText(Path.Combine(tmp, CompleteMarker), "");

        if (Directory.Exists(dest))
            Directory.Delete(dest, recursive: true);
        Directory.Move(tmp, dest);
    }

    private static void Discard(string loader)
    {
        lock (Gate)
        {
            var dir = Path.GetDirectoryName(loader)!;
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception e) { Log.Warning(e, "Could not remove the refused loader copy {Dir}", dir); }
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
        {
            // File.Copy keeps the Unix mode, so the loader stays executable.
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }
}
