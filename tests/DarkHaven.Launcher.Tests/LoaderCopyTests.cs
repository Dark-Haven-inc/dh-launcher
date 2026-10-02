using DarkHaven.Launcher.Security;
using DarkHaven.Launcher.Update;
using Xunit;

namespace DarkHaven.Launcher.Tests;

/// <summary>
/// The AppImage's copy of the loader: made once per version, and made again whenever it no longer matches the
/// AppImage's loader or the guard refuses it, since a release guard starts only a loader directory it pins.
/// </summary>
public sealed class LoaderCopyTests : IDisposable
{
    private const string LoaderName = "DarkHaven.Loader";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dh-loader-copy-");
    private readonly string _source;
    private readonly LoaderCopy.Copies _copies;

    public LoaderCopyTests()
    {
        // The AppImage's loader/.
        _source = Path.Combine(_root.FullName, "appimage", "usr", "bin", "loader");
        Directory.CreateDirectory(Path.Combine(_source, "runtimes"));
        File.WriteAllText(Path.Combine(_source, LoaderName), "loader");
        File.WriteAllText(Path.Combine(_source, "DarkHaven.ContentDb.dll"), "content db");
        File.WriteAllText(Path.Combine(_source, "runtimes", "libnative.so"), "native");
        _copies = new LoaderCopy.Copies(Path.Combine(_root.FullName, "data", "loader"), "1.2.3");
    }

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch { /* temp */ }
    }

    private string Loader => Path.Combine(_source, LoaderName);
    private string Copy => Path.Combine(_copies.Root, _copies.Version);
    private string CopiedLoader => Path.Combine(Copy, LoaderName);

    /// <summary>
    /// Marks the copy as it is now with an empty directory: the guard pins files only, so the check ignores it, and it
    /// is gone only once the copy has been made again.
    /// </summary>
    private string Mark() => Directory.CreateDirectory(Path.Combine(Copy, "marked")).FullName;

    [Fact]
    public void ACopyIsMadeOnceAndReusedWhileItMatches()
    {
        Assert.Equal(CopiedLoader, LoaderCopy.Prepare(Loader, _copies));
        Assert.Equal("native", File.ReadAllText(Path.Combine(Copy, "runtimes", "libnative.so")));
        Assert.True(File.Exists(Path.Combine(Copy, ".complete")));
        Assert.Null(LoaderCopy.Drift(_source, Copy));

        var mark = Mark();
        Assert.Equal(CopiedLoader, LoaderCopy.Prepare(Loader, _copies));
        Assert.True(Directory.Exists(mark));
    }

    [Theory]
    [InlineData("changed", "DarkHaven.ContentDb.dll has changed")]
    [InlineData("resized", "runtimes/libnative.so has changed")]
    [InlineData("missing", "DarkHaven.ContentDb.dll is missing")]
    [InlineData("extra", "cheat.dll is not the loader's")]
    public void ACopyThatDriftedIsMadeAgain(string damage, string drift)
    {
        LoaderCopy.Prepare(Loader, _copies);
        var mark = Mark();
        switch (damage)
        {
            case "changed": File.WriteAllText(Path.Combine(Copy, "DarkHaven.ContentDb.dll"), "content dB"); break;
            case "resized": File.AppendAllText(Path.Combine(Copy, "runtimes", "libnative.so"), "+"); break;
            case "missing": File.Delete(Path.Combine(Copy, "DarkHaven.ContentDb.dll")); break;
            case "extra": File.WriteAllText(Path.Combine(Copy, "cheat.dll"), ""); break;
        }
        Assert.Equal(drift, LoaderCopy.Drift(_source, Copy));

        Assert.Equal(CopiedLoader, LoaderCopy.Prepare(Loader, _copies));
        Assert.Null(LoaderCopy.Drift(_source, Copy));
        Assert.False(Directory.Exists(mark));
        Assert.Equal("content db", File.ReadAllText(Path.Combine(Copy, "DarkHaven.ContentDb.dll")));
    }

    [Fact]
    public void ALinkInTheCopyIsDrift()
    {
        if (OperatingSystem.IsWindows())
            return; // symbolic links need a privilege there

        LoaderCopy.Prepare(Loader, _copies);
        var target = Path.Combine(_root.FullName, "elsewhere.dll");
        File.WriteAllText(target, "content db");
        File.Delete(Path.Combine(Copy, "DarkHaven.ContentDb.dll"));
        File.CreateSymbolicLink(Path.Combine(Copy, "DarkHaven.ContentDb.dll"), target);

        Assert.Equal("DarkHaven.ContentDb.dll is a link", LoaderCopy.Drift(_source, Copy));
        LoaderCopy.Prepare(Loader, _copies);
        Assert.Null(LoaderCopy.Drift(_source, Copy));
    }

    [Fact]
    public void ARefusedCopyIsMadeAgainAndStartedOnceMore()
    {
        var started = new List<string>();
        string? mark = null;

        var game = LoaderCopy.Start(Loader, _copies, loader =>
        {
            started.Add(loader);
            if (started.Count == 1)
            {
                // Matches the AppImage's loader, and refused all the same (a guard that knows better).
                mark = Mark();
                throw new GuardException(GuardStatus.Refused, "loader file DarkHaven.Loader has been modified");
            }
            Assert.False(Directory.Exists(mark));
            return "game";
        });

        Assert.Equal("game", game);
        Assert.Equal(new[] { CopiedLoader, CopiedLoader }, started);
        Assert.Null(LoaderCopy.Drift(_source, Copy));
    }

    [Fact]
    public void ARefusalIsRetriedOnceAndOtherFailuresNotAtAll()
    {
        var refused = 0;
        var e = Assert.Throws<GuardException>(() => LoaderCopy.Start<string>(Loader, _copies, _ =>
        {
            refused++;
            throw new GuardException(GuardStatus.Refused, "not a bundled engine");
        }));
        Assert.Equal(GuardStatus.Refused, e.Status);
        Assert.Equal(2, refused);

        var failed = 0;
        Assert.Throws<GuardException>(() => LoaderCopy.Start<string>(Loader, _copies, _ =>
        {
            failed++;
            throw new GuardException(GuardStatus.Os, "spawn failed");
        }));
        Assert.Equal(1, failed);
    }

    [Fact]
    public void WithoutACopyTheLoaderStartsInPlaceAndIsNotRetried()
    {
        // The data directory cannot take the copy: the loader runs from the AppImage itself.
        File.WriteAllText(Path.Combine(_root.FullName, "not-a-directory"), "");
        var copies = new LoaderCopy.Copies(Path.Combine(_root.FullName, "not-a-directory"), "1.2.3");

        var started = new List<string>();
        Assert.Throws<GuardException>(() => LoaderCopy.Start<string>(Loader, copies, loader =>
        {
            started.Add(loader);
            throw new GuardException(GuardStatus.Refused, "refused");
        }));
        Assert.Equal(new[] { Loader }, started);
    }

    [Fact]
    public void EarlierVersionsCopiesAreRemoved()
    {
        LoaderCopy.Prepare(Loader, _copies with { Version = "1.2.2" });
        LoaderCopy.Prepare(Loader, _copies);
        Assert.Equal(new[] { Copy }, Directory.GetDirectories(_copies.Root));
    }
}
