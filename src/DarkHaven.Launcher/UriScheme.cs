using Microsoft.Win32;
using Serilog;

namespace DarkHaven.Launcher;

/// <summary>
/// Registers the launcher as the OS handler for <c>ss14://</c> and <c>ss14s://</c> links so a click
/// on a server link (on a website, in Discord, in the game lobby) opens Dark Haven Launcher.
/// Per-user (<c>HKCU</c>), no admin rights, idempotent.
/// </summary>
public static class UriScheme
{
    public static readonly string[] Schemes = ["ss14", "ss14s"];

    public static void EnsureRegistered(string exePath)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
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

            Log.Debug("Registered ss14:// URI scheme -> {Exe}", exePath);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not register the ss14:// URI scheme");
        }
    }
}
