using DarkHaven.Launcher.Update;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public class AppImageInstallTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dh-appimage-");

    private string Downloaded(string content = "appimage v1")
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root.FullName, "Downloads"));
        var path = Path.Combine(dir.FullName, AppImageInstall.FileName);
        File.WriteAllText(path, content);
        return path;
    }

    private string Target => Path.Combine(_root.FullName, "share", "Frontier15Launcher", AppImageInstall.FileName);

    [Fact]
    public void First_run_from_downloads_copies_itself_into_place()
    {
        if (OperatingSystem.IsWindows())
            return;

        var from = Downloaded();
        Assert.Equal(Target, AppImageInstall.InstallIfNeeded(from, Target));
        Assert.Equal("appimage v1", File.ReadAllText(Target));
        Assert.True(File.GetUnixFileMode(Target).HasFlag(UnixFileMode.UserExecute));
        Assert.False(File.Exists(Target + ".part"));
    }

    [Fact]
    public void An_existing_install_is_started_instead_and_left_alone()
    {
        if (OperatingSystem.IsWindows())
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        File.WriteAllText(Target, "installed, maybe self-updated since");
        var from = Downloaded("an older download");

        Assert.Equal(Target, AppImageInstall.InstallIfNeeded(from, Target));
        Assert.Equal("installed, maybe self-updated since", File.ReadAllText(Target));
    }

    [Fact]
    public void Running_from_the_install_or_a_system_directory_changes_nothing()
    {
        if (OperatingSystem.IsWindows())
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        File.WriteAllText(Target, "installed");
        Assert.Null(AppImageInstall.InstallIfNeeded(Target, Target));

        // A package manager's copy (read-only directory, like /opt): not copied, not self-updated.
        var system = Directory.CreateDirectory(Path.Combine(_root.FullName, "opt"));
        var packaged = Path.Combine(system.FullName, AppImageInstall.FileName);
        File.WriteAllText(packaged, "from pacman");
        File.SetUnixFileMode(system.FullName, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            if (AppImageInstall.IsUserWritable(packaged))
                return; // running as root: every directory is writable, nothing to check
            Assert.Null(AppImageInstall.InstallIfNeeded(packaged, Path.Combine(_root.FullName, "other", AppImageInstall.FileName)));
        }
        finally
        {
            File.SetUnixFileMode(system.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void Dispose() => _root.Delete(recursive: true);
}
