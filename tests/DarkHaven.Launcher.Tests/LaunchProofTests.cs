using System.Security.Cryptography;
using System.Text;
using DarkHaven.Launcher.Security;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public sealed class LaunchProofTests
{
    [Fact]
    public void ProofHasTheAgreedShapeAndVerifies()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var user = Guid.NewGuid();
        var issued = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);

        var token = LaunchProof.Create(key, user, issued, "1.2.3");
        var dot = token.IndexOf('.');
        var payload = FromBase64Url(token[..dot]);
        var signature = FromBase64Url(token[(dot + 1)..]);

        Assert.Equal($"dh-launch/1\n{user:D}\n1790000000\n1.2.3", Encoding.UTF8.GetString(payload));
        Assert.Equal(64, signature.Length);
        Assert.True(key.VerifyData(payload, signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void SealedKeyRoundTrips()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = key.ExportPkcs8PrivateKey();
        var sealedKey = LaunchSigningKey.Seal(pkcs8);

        Assert.NotEqual(Convert.ToBase64String(pkcs8), sealedKey);
        Assert.Equal(pkcs8, LaunchSigningKey.Unseal(sealedKey));
    }

    [Fact]
    public void DevelopmentBuildsHaveNoKey()
    {
        // Tests are built without -p:DhLaunchKey, like every non-release build.
        Assert.Null(LaunchSigningKey.TryLoad());
        Assert.Null(LaunchProof.TryCreate(Guid.NewGuid()));
    }

    /// <summary>
    /// Prints a proof for the game server's known-vector test (LaunchProofTest.KnownVectorFromTheLauncher).
    /// </summary>
    [Fact]
    public void KnownVector()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = LaunchProof.Create(key, Guid.Parse("11111111-2222-3333-4444-555555555555"),
            DateTimeOffset.FromUnixTimeSeconds(1_790_000_000), "9.9.9");

        Console.WriteLine($"VECTOR_PUBLIC_KEY={Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}");
        Console.WriteLine($"VECTOR_TOKEN={token}");
        Assert.Contains('.', token);
    }

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }
}
