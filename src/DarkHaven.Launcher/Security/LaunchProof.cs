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
/// The signing key is built into release builds from a CI secret (see <see cref="LaunchSigningKey"/>), kept as split
/// shares and never held whole (see <see cref="SplitEcdsa"/>). A proof is bound to the account, so a leaked one is
/// useless to anyone else. Someone determined can still recover the key from a build; rotating it with releases and
/// trusting only recent keys is what limits that.
/// </remarks>
public static class LaunchProof
{
    /// <summary>Environment variable the engine reads the proof from (<c>NetManager.LaunchProofEnvVar</c>).</summary>
    public const string EnvVar = "DH_LAUNCH_PROOF";

    public const string Magic = "dh-launch/1";

    /// <summary>Signs a proof with a whole key. Used by tests and the CLI; production uses the split shares.</summary>
    public static string Create(ECDsa key, Guid userId, DateTimeOffset issued, string launcherVersion)
    {
        var payload = Payload(userId, issued, launcherVersion);
        var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{ToBase64Url(payload)}.{ToBase64Url(signature)}";
    }

    /// <summary>
    /// A proof for this account from this build's split signing key, or null if the build has none (dev builds).
    /// </summary>
    public static string? TryCreate(Guid userId)
    {
        if (LaunchSigningKey.Shares is not { Count: > 0 } shares)
            return null;

        var payload = Payload(userId, DateTimeOffset.UtcNow, LauncherInfo.Version);
        var signature = SplitEcdsa.SignData(shares, payload);
        return $"{ToBase64Url(payload)}.{ToBase64Url(signature)}";
    }

    private static byte[] Payload(Guid userId, DateTimeOffset issued, string launcherVersion) =>
        Encoding.UTF8.GetBytes(
            $"{Magic}\n{userId:D}\n{issued.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}\n{launcherVersion}");

    private static string ToBase64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// The release signing key, as additive shares the source generator lays out at build time from the <c>DhLaunchKey</c>
/// MSBuild property (CI passes the <c>DH_LAUNCH_SIGNING_KEY</c> secret). <see cref="Shares"/> is null in builds without
/// it. The key is never stored or reconstructed whole; signing sums the shares (<see cref="SplitEcdsa"/>).
/// </summary>
public static partial class LaunchSigningKey
{
    private static readonly byte[] Mask = SHA256.HashData(Encoding.ASCII.GetBytes("dh-launch-mask/1"));

    private static readonly Lazy<IReadOnlyList<byte[]>?> LazyShares = new(LoadShares);

    /// <summary>The key's shares (each 32 bytes, summing to the private scalar mod n), or null in a keyless build.</summary>
    public static IReadOnlyList<byte[]>? Shares => LazyShares.Value;

    // Filled in by DarkHaven.Launcher.KeyGen at compile time; returns null when no key was supplied.
    private static partial IReadOnlyList<byte[]>? LoadShares();

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
