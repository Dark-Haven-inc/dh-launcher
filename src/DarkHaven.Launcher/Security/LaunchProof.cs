using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DarkHaven.Launcher.Security;

/// <summary>
/// The token the launcher's native guard (<c>dh_guard</c>, native/dh-guard; see <see cref="Guard"/>) signs so a
/// Frontier 15 server can tell its players came through the genuine launcher and not the official one, a Marsey build
/// or a hand-rolled client. The game server's <c>LaunchProof</c> (dh-sector-frontier,
/// Content.Server/_DH/AntiCheat/Launcher) verifies it; both must agree:
/// <code>
/// token     = base64url(payload) "." base64url(signature)
/// payload   = "dh-launch/2\n" userId(guid, "D") "\n" base64url(challenge) "\n" launcherVersion      (UTF-8)
/// signature = ECDSA P-256 over SHA-256 of the payload bytes, IEEE P1363 (r || s, 64 bytes)
/// </code>
/// The challenge is the server's nonce for one login followed by that session's auth hash; the game asks the guard's
/// broker for the proof mid-handshake, so each proof is good for exactly one connection.
/// </summary>
/// <remarks>
/// Version 1 (<c>"dh-launch/1\n" userId "\n" unixSeconds "\n" launcherVersion</c>) is signed once at start and put
/// in <see cref="EnvVar"/> for servers that predate version 2; it goes once they all verify version 2.
/// Nothing in C# signs with the release key any more: it is built into the guard only (docs/GUARD.md). What is left
/// here is the format itself, signed with a whole key, as the reference the tests hold the guard's proofs to.
/// </remarks>
public static class LaunchProof
{
    /// <summary>Environment variable the engine reads a version 1 proof from (<c>NetManager.LaunchProofEnvVar</c>).</summary>
    public const string EnvVar = "DH_LAUNCH_PROOF";

    public const string MagicV1 = "dh-launch/1";
    public const string Magic = "dh-launch/2";

    /// <summary>Bytes in a challenge: the server's 32-byte nonce and the 32-byte auth hash.</summary>
    public const int ChallengeLength = 64;

    /// <summary>Signs a proof for a login with a whole key: the reference format. Releases sign in dh_guard.</summary>
    public static string Create(ECDsa key, Guid userId, ReadOnlySpan<byte> challenge, string launcherVersion)
    {
        var payload = Payload(userId, challenge, launcherVersion);
        var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{ToBase64Url(payload)}.{ToBase64Url(signature)}";
    }

    /// <summary>Signs a version 1 proof with a whole key: the reference format.</summary>
    public static string CreateV1(ECDsa key, Guid userId, DateTimeOffset issued, string launcherVersion)
    {
        var payload = PayloadV1(userId, issued, launcherVersion);
        var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{ToBase64Url(payload)}.{ToBase64Url(signature)}";
    }

    private static byte[] Payload(Guid userId, ReadOnlySpan<byte> challenge, string launcherVersion)
    {
        if (challenge.Length != ChallengeLength)
            throw new ArgumentException($"A challenge is {ChallengeLength} bytes", nameof(challenge));

        return Encoding.UTF8.GetBytes($"{Magic}\n{userId:D}\n{ToBase64Url(challenge.ToArray())}\n{launcherVersion}");
    }

    private static byte[] PayloadV1(Guid userId, DateTimeOffset issued, string launcherVersion) =>
        Encoding.UTF8.GetBytes(
            $"{MagicV1}\n{userId:D}\n{issued.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}\n{launcherVersion}");

    internal static string ToBase64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryFromBase64Url(string text, out byte[] data)
    {
        data = [];
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        try
        {
            data = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// How the release signing key is sealed for the <c>DH_LAUNCH_SIGNING_KEY</c> secret: the 32-byte P-256 private scalar
/// XOR <c>SHA256("dh-launch-mask/1")</c>, base64. <c>dhlauncher launch-key</c> prints it this way; the guard's build
/// script (native/dh-guard/build.rs) unseals it and splits it into shares. The launcher itself never holds the key.
/// </summary>
public static class LaunchSigningKey
{
    private static readonly byte[] Mask = SHA256.HashData(Encoding.ASCII.GetBytes("dh-launch-mask/1"));

    /// <summary>Masks a 32-byte private scalar for the CI secret, as <c>dhlauncher launch-key</c> prints it.</summary>
    public static string SealScalar(byte[] scalar) => Convert.ToBase64String(ApplyMask(scalar));

    public static byte[] UnsealScalar(string sealedScalar) => ApplyMask(Convert.FromBase64String(sealedScalar));

    private static byte[] ApplyMask(byte[] data)
    {
        var result = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
            result[i] = (byte)(data[i] ^ Mask[i % Mask.Length]);
        return result;
    }
}
