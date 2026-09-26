using DarkHaven.Launcher.Update;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public class LauncherUpdaterTests
{
    /// <summary>
    /// The source repo (dh-launcher) is going private, and the updater reads its feed anonymously — a
    /// launcher that looked there would silently stop getting updates. Releases live in the public,
    /// releases-only repo.
    /// </summary>
    [Fact]
    public void Updates_come_from_the_public_releases_only_repo()
    {
        Assert.Equal("https://github.com/Dark-Haven-inc/frontier15-launcher", LauncherUpdater.DefaultFeedUrl);
        Assert.DoesNotContain("dh-launcher", LauncherUpdater.DefaultFeedUrl);
    }
}
