using System.Runtime.InteropServices;

namespace DarkHaven.Launcher.Engine;

/// <summary>
/// Picks the best-matching .NET RID from a set of RIDs a build offers, for the current machine.
/// Handles the emulation fallbacks SS14 relies on (Apple Silicon → x64 via Rosetta, Windows ARM → x64).
/// </summary>
public static class RidUtility
{
    public static string CurrentOs =>
        OperatingSystem.IsWindows() ? "win"
        : OperatingSystem.IsMacOS() ? "osx"
        : OperatingSystem.IsFreeBSD() ? "freebsd"
        : "linux";

    public static string CurrentArch => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
    };

    /// <summary>Ordered most- to least-preferred RIDs for this machine.</summary>
    public static IEnumerable<string> Candidates()
    {
        var os = CurrentOs;
        var arch = CurrentArch;

        yield return $"{os}-{arch}";

        // Emulation fallbacks.
        if (arch == "arm64")
            yield return $"{os}-x64";
        if (os == "win" && arch == "x64")
            yield break;
    }

    public static string? FindBest(IEnumerable<string> available)
    {
        var set = available as ICollection<string> ?? available.ToList();
        foreach (var candidate in Candidates())
        {
            if (set.Contains(candidate))
                return candidate;
        }
        return null;
    }
}
