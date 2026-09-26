using Serilog;

namespace DarkHaven.Launcher.Update;

/// <summary>
/// Inside an AppImage the launcher's files live on a FUSE mount that goes away when the launcher exits,
/// while the game it started keeps running and keeps reading its loader's files. So there the game is
/// started from a copy of the loader under the data directory, one per launcher version.
/// </summary>
public static class LoaderCopy
{
    private const string CompleteMarker = ".complete";

    /// <summary>The loader to start: <paramref name="loaderPath"/> itself, or its copy when running from an AppImage.</summary>
    public static string Prepare(string loaderPath)
    {
        if (Environment.GetEnvironmentVariable("APPIMAGE") is not { Length: > 0 })
            return loaderPath;

        try
        {
            var root = Path.Combine(LauncherPaths.DataDir, "loader");
            var dest = Path.Combine(root, LauncherInfo.Version);

            if (!File.Exists(Path.Combine(dest, CompleteMarker)))
            {
                var tmp = $"{dest}.tmp-{Environment.ProcessId}";
                if (Directory.Exists(tmp))
                    Directory.Delete(tmp, recursive: true);

                CopyDirectory(Path.GetDirectoryName(loaderPath)!, tmp);
                File.WriteAllText(Path.Combine(tmp, CompleteMarker), "");

                if (Directory.Exists(dest))
                    Directory.Delete(dest, recursive: true);
                Directory.Move(tmp, dest);
                Log.Information("Loader copied out of the AppImage to {Dir}", dest);
            }

            // Earlier versions' copies. A game still running from one keeps its open files.
            foreach (var dir in Directory.GetDirectories(root))
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
