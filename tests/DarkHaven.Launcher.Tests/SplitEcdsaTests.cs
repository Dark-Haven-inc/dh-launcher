using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using DarkHaven.Launcher.Security;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public class SplitEcdsaTests
{
    private static BigInteger D(ECParameters p) => new(p.D!, isUnsigned: true, isBigEndian: true);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void Signature_from_shares_verifies_with_the_real_public_key(int shareCount)
    {
        using var real = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var shares = SplitEcdsa.Split(D(real.ExportParameters(true)), shareCount);

        for (var i = 0; i < 20; i++)
        {
            var message = Encoding.UTF8.GetBytes($"dh-launch/2 payload #{i}");
            var sig = SplitEcdsa.SignData(shares, message);

            Assert.True(real.VerifyData(message, sig, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
    }

    [Fact]
    public void A_wrong_key_does_not_verify()
    {
        using var real = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var shares = SplitEcdsa.Split(D(real.ExportParameters(true)), 3);

        var sig = SplitEcdsa.SignData(shares, "x"u8.ToArray());
        Assert.False(other.VerifyData("x"u8.ToArray(), sig, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void Derived_public_key_matches_dotnet()
    {
        using var real = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = SplitEcdsa.PublicKeyFromScalar(D(real.ExportParameters(true)));

        using var derived = ECDsa.Create();
        derived.ImportSubjectPublicKeyInfo(spki, out _);

        // Both public keys accept the same signature.
        var sig = real.SignData("m"u8.ToArray(), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Assert.True(derived.VerifyData("m"u8.ToArray(), sig, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void Signing_is_randomised_but_each_signature_is_valid()
    {
        using var real = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var shares = SplitEcdsa.Split(D(real.ExportParameters(true)), 2);

        var a = SplitEcdsa.SignData(shares, "same"u8.ToArray());
        var b = SplitEcdsa.SignData(shares, "same"u8.ToArray());
        Assert.NotEqual(Convert.ToHexString(a), Convert.ToHexString(b)); // fresh k each time
    }
}
