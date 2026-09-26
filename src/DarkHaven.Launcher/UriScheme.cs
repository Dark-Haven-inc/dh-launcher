using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using Serilog;

namespace DarkHaven.Launcher;

/// <summary>
/// Registers the launcher as the OS handler for <c>ss14://</c> and <c>ss14s://</c> links so a click
/// on a server link (on a website, in Discord, in the game lobby) opens Dark Haven Launcher.
/// Per-user, no admin rights, idempotent: <c>HKCU</c> on Windows; on Linux a desktop entry in
/// <c>~/.local/share/applications</c> made the default handler in <c>~/.config/mimeapps.list</c>
/// (which also puts the launcher in the application menu).
/// </summary>
public static class UriScheme
{
    public static readonly string[] Schemes = ["ss14", "ss14s"];

    private const string DesktopFileName = "frontier15-launcher.desktop";

    /// <param name="exePath">What the link starts.</param>
    /// <param name="iconPng">Icon for the Linux desktop entry, copied next to the launcher's data.</param>
    public static void EnsureRegistered(string exePath, string? iconPng = null)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                RegisterWindows(exePath);
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
                RegisterXdg(exePath, iconPng);
            else
                return;

            Log.Debug("Registered ss14:// URI scheme -> {Exe}", exePath);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not register the ss14:// URI scheme");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RegisterWindows(string exePath)
    {
        foreach (var scheme in Schemes)
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{scheme}");
            key.SetValue("", "URL:Space Station 14 Protocol");
            key.SetValue("URL Protocol", "");

            using (var icon = key.CreateSubKey("DefaultIcon"))
                icon.SetValue("", $"\"{exePath}\",0");

            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{exePath}\" \"%1\"");
        }
    }

    private static void RegisterXdg(string exePath, string? iconPng)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d
            ? d
            : Path.Combine(home, ".local", "share");
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c
            ? c
            : Path.Combine(home, ".config");

        string? icon = null;
        if (iconPng is not null && File.Exists(iconPng))
        {
            // The launcher's own copy may sit on an AppImage mount that is gone once it exits.
            icon = Path.Combine(LauncherPaths.DataDir, "launcher-icon.png");
            Directory.CreateDirectory(LauncherPaths.DataDir);
            File.Copy(iconPng, icon, overwrite: true);
        }

        var applications = Path.Combine(dataHome, "applications");
        Directory.CreateDirectory(applications);
        var entry = DesktopEntry(exePath, icon);
        var entryPath = Path.Combine(applications, DesktopFileName);
        if (!File.Exists(entryPath) || File.ReadAllText(entryPath) != entry)
        {
            File.WriteAllText(entryPath, entry);
            // Refreshes the "open with" cache; the default handler below works without it.
            TryRun("update-desktop-database", applications);
        }

        Directory.CreateDirectory(configHome);
        var mimeApps = Path.Combine(configHome, "mimeapps.list");
        var existing = File.Exists(mimeApps) ? File.ReadAllText(mimeApps) : "";
        var updated = SetDefaultHandlers(existing, Schemes.Select(s => $"x-scheme-handler/{s}"), DesktopFileName);
        if (updated != existing)
            File.WriteAllText(mimeApps, updated);
    }

    internal static string DesktopEntry(string exePath, string? icon)
    {
        var sb = new StringBuilder();
        sb.Append("[Desktop Entry]\n");
        sb.Append("Type=Application\n");
        sb.Append("Name=Frontier 15 Launcher\n");
        sb.Append("Comment=Space Station 14 launcher\n");
        sb.Append($"Exec={QuoteExecArg(exePath)} %u\n");
        if (icon is not null)
            sb.Append($"Icon={icon}\n");
        sb.Append("Terminal=false\n");
        sb.Append("Categories=Game;\n");
        sb.Append($"MimeType={string.Join("", Schemes.Select(s => $"x-scheme-handler/{s};"))}\n");
        return sb.ToString();
    }

    /// <summary>
    /// Quotes an Exec argument per the Desktop Entry spec: inside double quotes, <c>" ` $ \</c> are
    /// backslash-escaped, and the string value itself then has its backslashes escaped once more.
    /// </summary>
    internal static string QuoteExecArg(string arg)
    {
        var quoted = new StringBuilder("\"");
        foreach (var ch in arg)
        {
            if (ch is '"' or '`' or '$' or '\\')
                quoted.Append('\\');
            quoted.Append(ch);
        }
        quoted.Append('"');
        return quoted.ToString().Replace("\\", "\\\\").Replace("%", "%%");
    }

    /// <summary>Makes <paramref name="desktopFile"/> the default for each MIME type in <c>[Default Applications]</c>, keeping the rest of the file.</summary>
    internal static string SetDefaultHandlers(string mimeApps, IEnumerable<string> mimeTypes, string desktopFile)
    {
        const string section = "[Default Applications]";
        var lines = mimeApps.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1] == "")
            lines.RemoveAt(lines.Count - 1);

        var start = lines.FindIndex(l => l.Trim() == section);
        if (start < 0)
        {
            if (lines.Count > 0)
                lines.Add("");
            lines.Add(section);
            start = lines.Count - 1;
        }

        foreach (var mime in mimeTypes)
        {
            var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
            if (end < 0)
                end = lines.Count;

            var line = $"{mime}={desktopFile};";
            var at = lines.FindIndex(start + 1, end - start - 1, l => l.Split('=', 2)[0].Trim() == mime);
            if (at >= 0)
            {
                lines[at] = line;
            }
            else
            {
                // After the section's last entry, not after the blank line that separates it from the next.
                var insert = end;
                while (insert > start + 1 && lines[insert - 1].Trim() == "")
                    insert--;
                lines.Insert(insert, line);
            }
        }

        return string.Join("\n", lines) + "\n";
    }

    private static void TryRun(string file, string arg)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, [arg])
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process?.WaitForExit(5000);
        }
        catch (Exception e)
        {
            Log.Debug(e, "{File} not run", file);
        }
    }
}
