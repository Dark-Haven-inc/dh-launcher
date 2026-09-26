using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DarkHaven.Launcher.Security;

/// <summary>
/// The token this launcher signs when it starts the game, so a Frontier 15 server can tell its players came through
/// the genuine launcher and not the official one, a Marsey build or a hand-rolled client. The game server's
/// <c>LaunchProof</c> (dh-sector-frontier, Content.Server/_DH/AntiCheat/Launcher) verifies it; both must agree:
/// <code>
/// token     = base64url(payload) "." base64url(signature)
/// payload   = "dh-launch/1\n" userId(guid, "D") "\n" unixSeconds "\n" launcherVersion      (UTF-8)
/// signature = ECDSA P-256 over SHA-256 of the payload bytes, IEEE P1363 (r || s, 64 bytes)
/// </code>
/// </summary>
/// <remarks>
/// The signing key is built into release builds from a CI secret (see <see cref="LaunchSigningKey"/>). A proof is
/// bound to the account, so a leaked one is useless to anyone else. The key itself ships inside every copy of the
/// launcher and can be dug out by someone determined; that is why it is rotated with releases and servers only
/// trust the keys of recent ones.
/// </remarks>
public static class LaunchProof
{
    /// <summary>Environment variable the engine reads the proof from (<c>NetManager.LaunchProofEnvVar</c>).</summary>
    public const string EnvVar = "DH_LAUNCH_PROOF";

    public const string Magic = "dh-launch/1";

    public static string Create(ECDsa key, Guid userId, DateTimeOffset issued, string launcherVersion)
    {
        var payload = Encoding.UTF8.GetBytes(
            $"{Magic}\n{userId:D}\n{issued.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}\n{launcherVersion}");
        var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{ToBase64Url(payload)}.{ToBase64Url(signature)}";
    }

    /// <summary>
    /// A proof for this account from this build's signing key, or null if the build has none (development builds).
    /// </summary>
    public static string? TryCreate(Guid userId)
    {
        using var key = LaunchSigningKey.TryLoad();
        return key == null ? null : Create(key, userId, DateTimeOffset.UtcNow, LauncherInfo.Version);
    }

    private static string ToBase64Url(byte[] data)
    {
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

/// <summary>
/// The release signing key. <see cref="Sealed"/> is generated at build time from the <c>DhLaunchKey</c> MSBuild
/// property (CI passes the <c>DH_LAUNCH_SIGNING_KEY</c> secret) and is empty in builds without it. It is stored lightly
/// masked, as <c>dhlauncher launch-key</c> prints it - enough to keep it out of a casual strings dump, no more.
/// </summary>
public static partial class LaunchSigningKey
{
    private static readonly byte[] Mask = SHA256.HashData(Encoding.ASCII.GetBytes("dh-launch-mask/1"));

    public static ECDsa? TryLoad()
    {
        if (string.IsNullOrEmpty(Sealed))
            return null;

        try
        {
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(Unseal(Sealed), out _);
            return key;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>What goes into the CI secret for a PKCS#8 private key.</summary>
    public static string Seal(byte[] pkcs8) => Convert.ToBase64String(ApplyMask(pkcs8));

    public static byte[] Unseal(string sealedKey) => ApplyMask(Convert.FromBase64String(sealedKey));

    private static byte[] ApplyMask(byte[] data)
    {
        var result = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
            result[i] = (byte) (data[i] ^ Mask[i % Mask.Length]);

        return result;
    }
}
