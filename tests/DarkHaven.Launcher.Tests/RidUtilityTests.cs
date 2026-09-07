using DarkHaven.Launcher.Engine;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public class RidUtilityTests
{
    [Fact]
    public void Prefers_an_exact_match_for_this_machine()
    {
        var exact = $"{RidUtility.CurrentOs}-{RidUtility.CurrentArch}";
        var pick = RidUtility.FindBest(["totally-fake", exact, "linux-mips"]);
        Assert.Equal(exact, pick);
    }

    [Fact]
    public void Returns_null_when_nothing_fits()
    {
        Assert.Null(RidUtility.FindBest(["solaris-sparc", "haiku-x64"]));
    }

    [Fact]
    public void Candidates_lead_with_the_native_rid()
    {
        var first = RidUtility.Candidates().First();
        Assert.Equal($"{RidUtility.CurrentOs}-{RidUtility.CurrentArch}", first);
    }
}
