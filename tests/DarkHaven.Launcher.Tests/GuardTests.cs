using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DarkHaven.Launcher.Security;
using Xunit;

namespace DarkHaven.Launcher.Tests;

/// <summary>A test for the development guard (no key) this project is normally built with.</summary>
public sealed class DevGuardFactAttribute : FactAttribute
{
    public DevGuardFactAttribute()
    {
        if (GuardTests.IsKeyed())
            Skip = "a release (keyed) dh_guard: it signs and starts only the pinned loader";
    }
}

/// <summary>
/// A development guard starting a stand-in loader (a shell script on Linux, tests/DarkHaven.Launcher.FakeLoader on
/// Windows) in the loader's place.
/// </summary>
public sealed class DevGuardLaunchFactAttribute : FactAttribute
{
    public DevGuardLaunchFactAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
            Skip = "no stand-in loader for this system";
        else if (GuardTests.IsKeyed())
            Skip = "a release (keyed) dh_guard: it starts only the pinned loader";
    }
}

public sealed class GuardTests
{
    /// <summary>Whether the guard next to the tests carries a key; false if it cannot be loaded (tests then fail).</summary>
    internal static bool IsKeyed()
    {
        try
        {
            return Guard.Info().HasKey;
        }
        catch (GuardException)
        {
            return false;
        }
    }

    [Fact]
    public void InfoHasTheNativeLayout()
    {
        Assert.Equal(180, Unsafe.SizeOf<Guard.NativeInfo>());
        Assert.Equal(Guard.AbiVersion, Guard.Info().AbiVersion);
    }

    [DevGuardFact]
    public void ADevelopmentGuardHasNoKey()
    {
        var info = Guard.Info();
        Assert.False(info.HasKey);
        Assert.Equal(0, info.ShareCount);
        Assert.Equal(0, info.PinnedFiles);
        Assert.Empty(info.PublicKey);
        Assert.NotEmpty(info.LauncherVersion);
    }

    /// <summary>
    /// The proof native/dh-guard's split signer made for a fixed login (tests/fixture.rs writes it): the C# reference
    /// format and .NET's ECDSA must agree with it, as the game server does.
    /// </summary>
    [Fact]
    public void TheRustSignersProofVerifies()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "rust-launch-proof.json");
        using var fixture = JsonDocument.Parse(File.ReadAllText(path));
        string Field(string name) => fixture.RootElement.GetProperty(name).GetString()!;

        var user = Guid.Parse(Field("userId"));
        var version = Field("launcherVersion");
        Assert.Equal(LaunchProof.ToBase64Url(LaunchProofTests.VectorChallenge), Field("challenge"));

        var token = Field("token");
        var dot = token.IndexOf('.');
        Assert.True(LaunchProof.TryFromBase64Url(token[..dot], out var payload));
        Assert.True(LaunchProof.TryFromBase64Url(token[(dot + 1)..], out var signature));
        Assert.Equal(Field("payload"), Encoding.UTF8.GetString(payload));

        // Byte for byte what the C# reference signs for the same login.
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var reference = LaunchProof.Create(other, user, LaunchProofTests.VectorChallenge, version);
        Assert.Equal(reference[..reference.IndexOf('.')], token[..dot]);

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(Field("publicKey")), out _);
        Assert.Equal(64, signature.Length);
        Assert.True(key.VerifyData(payload, signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        signature[^1] ^= 1;
        Assert.False(key.VerifyData(payload, signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }
}
