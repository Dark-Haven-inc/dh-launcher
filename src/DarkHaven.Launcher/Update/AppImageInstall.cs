using Serilog;

namespace DarkHaven.Launcher.Update;

/// <summary>
/// Linux has no installer: players download the .AppImage and run it, usually from their downloads
/// folder. On that first run the launcher puts itself somewhere permanent (<see cref="InstallPath"/>)
/// and restarts from there, so the menu entry, ss14:// links and self-updates don't point at a file the
/// player will move or delete. An AppImage somewhere the player can't write (a distro package under
/// /opt or /usr) is left where it is: the package manager owns it.
/// </summary>
public static class AppImageInstall
{
    public const string FileName = "Frontier15Launcher.AppImage";

    /// <summary>Set to 1 to keep running the AppImage from wherever it is.</summary>
    public const string PortableEnvVar = "F15_PORTABLE";

    /// <summary><c>$XDG_DATA_HOME/Frontier15Launcher/Frontier15Launcher.AppImage</c>.</summary>
    public static string InstallPath => Path.Combine(DataHome(), "Frontier15Launcher", FileName);

    /// <summary>The AppImage this process runs from, or null when not in one.</summary>
    public static string? Current =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } p ? p : null;

    /// <summary>
    /// Whether the running launcher can replace its own files — false for an AppImage a package
    /// manager put in a system directory, where a self-update would only fail on permissions.
    /// </summary>
    public static bool CanUpdateItself => Current is not { } appImage || IsUserWritable(appImage);

    /// <summary>
    /// Copies the running AppImage to <see cref="InstallPath"/> if it isn't there yet. Returns the
    /// path to restart from, or null to carry on in this process.
    /// </summary>
    public static string? InstallIfNeeded()
    {
        if (!OperatingSystem.IsLinux() || Current is not { } current
            || Environment.GetEnvironmentVariable(PortableEnvVar) == "1")
            return null;
        return InstallIfNeeded(current, InstallPath);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    internal static string? InstallIfNeeded(string current, string target)
    {
        if (SamePath(current, target) || !IsUserWritable(current))
            return null;

        try
        {
            if (File.Exists(target))
            {
                // Already installed (and kept current by self-update): run that one instead.
                Log.Information("Launcher already installed at {Target}; starting it instead of {Current}", target, current);
                return target;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var tmp = target + ".part";
            File.Copy(current, tmp, overwrite: true);
            File.SetUnixFileMode(tmp,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            // A rename, so a copy that dies halfway never looks installed.
            File.Move(tmp, target, overwrite: true);
            Log.Information("Launcher installed to {Target} (from {Current})", target, current);
            return target;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not install the launcher to {Target}; running from {Current}", target, current);
            return null;
        }
    }

    /// <summary>
    /// Starts the installed copy with the same arguments. Through <c>sh</c>, to close every descriptor
    /// past stdio first: the AppImage runtime hands the app its keep-alive pipe and mount directory
    /// without close-on-exec, and a child that inherits them keeps the old AppImage mounted (and its
    /// runtime alive) for as long as the child runs. The old AppImage's variables go too; the new one
    /// sets its own.
    /// </summary>
    public static void StartInstalled(string path, IEnumerable<string> args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("for f in /proc/$$/fd/*; do n=${f##*/}; case $n in 0|1|2) ;; *) eval \"exec $n>&-\" 2>/dev/null ;; esac; done; exec \"$0\" \"$@\"");
        psi.ArgumentList.Add(path);
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        if (Environment.GetEnvironmentVariable("APPDIR") is { Length: > 0 } appDir && psi.Environment["PATH"] is { } pathVar)
            psi.Environment["PATH"] = string.Join(':', pathVar.Split(':').Where(p => !p.StartsWith(appDir, StringComparison.Ordinal)));
        foreach (var name in new[] { "APPDIR", "APPIMAGE", "ARGV0", "OWD" })
            psi.Environment.Remove(name);

        System.Diagnostics.Process.Start(psi);
    }

    /// <summary>True when the file's directory takes new files from this user (not a system directory).</summary>
    internal static bool IsUserWritable(string file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (string.IsNullOrEmpty(dir))
            return false;
        try
        {
            var probe = Path.Combine(dir, $".f15-write-test-{Environment.ProcessId}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal);

    private static string DataHome() =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
}
