using System.Reflection;

namespace DarkHaven.Launcher;

/// <summary>Single source of truth for the launcher's own version, read from the entry assembly.</summary>
public static class LauncherInfo
{
    /// <summary>Marketing version without any git-hash suffix, e.g. <c>"0.1.0"</c>.</summary>
    public static string Version { get; } = Resolve();

    /// <summary>
    /// The file that starts this launcher again (ss14:// links, the engine's redial). Inside an AppImage
    /// that is the .AppImage itself: the running executable sits on a mount that goes away when it exits.
    /// </summary>
    public static string? ExecutablePath =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage ? appImage : Environment.ProcessPath;

    private static string Resolve()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }

        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
