using System.Security.Cryptography;
using DarkHaven.Launcher.Security;
using DarkHaven.Launcher.Update;
using Xunit;

namespace DarkHaven.Launcher.Tests;

/// <summary>
/// A test of a release (keyed) guard against the loader it pins. It runs only when the tests were built with
/// <c>DH_LAUNCH_SIGNING_KEY</c> and <c>DH_GUARD_LOADER_PINS</c>, and <c>DH_GUARD_TEST_LOADER</c> names the pinned
/// loader's executable (the published <c>loader/DarkHaven.Loader[.exe]</c> the pins were made from).
/// </summary>
public sealed class KeyedGuardFactAttribute : FactAttribute
{
    public KeyedGuardFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(KeyedGuardTests.LoaderVar) is not { Length: > 0 } loader || !File.Exists(loader))
            Skip = $"needs a keyed dh_guard and {KeyedGuardTests.LoaderVar} naming the loader it pins (docs/GUARD.md)";
        else if (!GuardTests.IsKeyed())
            Skip = "the tests were built with a development dh_guard (no DH_LAUNCH_SIGNING_KEY)";
    }
}

/// <summary>A release guard starts the genuine loader with a genuine engine, and nothing else.</summary>
public sealed class KeyedGuardTests : IDisposable
{
    internal const string LoaderVar = "DH_GUARD_TEST_LOADER";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dh-keyed-{Guid.NewGuid():N}");
    private readonly string _engine;

    public KeyedGuardTests()
    {
        Directory.CreateDirectory(_dir);
        _engine = Path.Combine(_dir, "engine.zip");
        File.WriteAllBytes(_engine, RandomNumberGenerator.GetBytes(4096));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private static string PinnedLoader => Environment.GetEnvironmentVariable(LoaderVar)!;

    private GuardLaunchRequest Request(string loader, string signature) => new()
    {
        LoaderPath = loader,
        EnginePath = _engine,
        EngineSignature = signature,
        EnginePublicKeyPath = LauncherPaths.SigningKeyPath,
        ContentDbPath = Path.Combine(_dir, "content.db"),
        ContentVersion = 1,
        Modules = [],
        ConnectAddress = "udp://127.0.0.1:1212/",
        Ss14Address = "ss14://127.0.0.1:1212/",
        Account = new GuardAccount("Player", "token", Guid.NewGuid(), null),
    };

    private static GuardException Refused(GuardLaunchRequest request)
    {
        var e = Assert.Throws<GuardException>(() => GameProcess.Start(request));
        Assert.Equal(GuardStatus.Refused, e.Status);
        return e;
    }

    [KeyedGuardFact]
    public void TheBuildCarriesAKeyAndPins()
    {
        var info = Guard.Info();
        Assert.True(info.HasKey);
        Assert.Equal(0, info.ShareCount); // not exposed: a hint for a key scanner (docs/GUARD.md)
        Assert.True(info.PinnedFiles > 0);
        Assert.Equal(91, info.PublicKey.Length);
    }

    [KeyedGuardFact]
    public void AnEngineThatIsNeitherBundledNorSignedIsRefused()
    {
        // The pinned loader passes; the engine is what stops it.
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(_engine)));
        Assert.Contains("not a bundled engine", Refused(Request(PinnedLoader, $"sha256:{hash}")).Message);
        Assert.Contains("signature does not hold", Refused(Request(PinnedLoader, new string('0', 128))).Message);
    }

    [KeyedGuardFact]
    public void ATamperedLoaderIsRefused()
    {
        var copy = CopyLoader();
        var victim = Directory.EnumerateFiles(copy, "*.dll", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First();
        File.AppendAllText(victim, "patched");

        var e = Refused(Request(Path.Combine(copy, Path.GetFileName(PinnedLoader)), new string('0', 128)));
        Assert.Contains("has been modified", e.Message);
    }

    [KeyedGuardFact]
    public void AnExtraFileNextToTheLoaderIsRefused()
    {
        var copy = CopyLoader();
        File.WriteAllText(Path.Combine(copy, "cheat.dll"), "");

        var e = Refused(Request(Path.Combine(copy, Path.GetFileName(PinnedLoader)), new string('0', 128)));
        Assert.Contains("unexpected file", e.Message);
    }

    [KeyedGuardFact]
    public void OnlyTheLoaderItselfIsStarted()
    {
        var e = Refused(Request(Path.Combine(Path.GetDirectoryName(PinnedLoader)!, "createdump"), new string('0', 128)));
        Assert.Contains("the loader must be", e.Message);
    }

    /// <summary>A byte-for-byte copy of the pinned loader directory (as the AppImage copy is, plus its marker).</summary>
    private string CopyLoader()
    {
        var source = Path.GetDirectoryName(PinnedLoader)!;
        var dest = Path.Combine(_dir, "loader");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        File.WriteAllText(Path.Combine(dest, ".complete"), "");

        // The copy itself is still the genuine loader: refused for the engine, not the loader.
        var e = Refused(Request(Path.Combine(dest, Path.GetFileName(PinnedLoader)), new string('0', 128)));
        Assert.Contains("signature does not hold", e.Message);
        return dest;
    }
}
