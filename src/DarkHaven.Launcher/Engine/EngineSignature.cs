using NSec.Cryptography;

namespace DarkHaven.Launcher.Engine;

/// <summary>Verifies RobustToolbox build / module signatures against the SS14 Ed25519 signing key.</summary>
public sealed class EngineSignature
{
    private readonly PublicKey _key;

    public EngineSignature(string signingKeyPath)
    {
        _key = PublicKey.Import(
            SignatureAlgorithm.Ed25519,
            File.ReadAllBytes(signingKeyPath),
            KeyBlobFormat.PkixPublicKeyText);
    }

    public bool Verify(ReadOnlySpan<byte> data, string signatureHex)
        => SignatureAlgorithm.Ed25519.Verify(_key, data, Convert.FromHexString(signatureHex));

    public bool VerifyFile(string path, string signatureHex)
        => Verify(File.ReadAllBytes(path), signatureHex);
}
