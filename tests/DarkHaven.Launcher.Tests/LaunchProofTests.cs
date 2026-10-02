using System.Security.Cryptography;
using System.Text;
using DarkHaven.Launcher.Security;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public sealed class LaunchProofTests
{
    internal static readonly byte[] VectorChallenge = Enumerable.Range(0, LaunchProof.ChallengeLength).Select(i => (byte)(i * 3 + 1)).ToArray();

    [Fact]
    public void ProofHasTheAgreedShapeAndVerifies()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var user = Guid.NewGuid();

        var token = LaunchProof.Create(key, user, VectorChallenge, "1.2.3");
        var dot = token.IndexOf('.');
        var payload = FromBase64Url(token[..dot]);
        var signature = FromBase64Url(token[(dot + 1)..]);

        Assert.Equal($"dh-launch/2\n{user:D}\n{LaunchProof.ToBase64Url(VectorChallenge)}\n1.2.3", Encoding.UTF8.GetString(payload));
        Assert.Equal(64, signature.Length);
        Assert.True(key.VerifyData(payload, signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void ChallengeMustBeWhole()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<ArgumentException>(() => LaunchProof.Create(key, Guid.NewGuid(), VectorChallenge.AsSpan(0, 32), "1.2.3"));
    }

    [Fact]
    public void V1ProofHasTheOldShape()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var user = Guid.NewGuid();
        var token = LaunchProof.CreateV1(key, user, DateTimeOffset.FromUnixTimeSeconds(1_790_000_000), "1.2.3");
        var payload = FromBase64Url(token[..token.IndexOf('.')]);
        Assert.Equal($"dh-launch/1\n{user:D}\n1790000000\n1.2.3", Encoding.UTF8.GetString(payload));
    }

    [Fact]
    public void SealedScalarRoundTrips()
    {
        var scalar = ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportParameters(true).D!;
        var sealedScalar = LaunchSigningKey.SealScalar(scalar);

        Assert.NotEqual(Convert.ToBase64String(scalar), sealedScalar);
        Assert.Equal(scalar, LaunchSigningKey.UnsealScalar(sealedScalar));
    }

    /// <summary>
    /// Prints a proof for the game server's known-vector test (LaunchProofTest.KnownVectorFromTheLauncher).
    /// </summary>
    [Fact]
    public void KnownVector()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = LaunchProof.Create(key, Guid.Parse("11111111-2222-3333-4444-555555555555"), VectorChallenge, "9.9.9");

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
