using System.Security.Cryptography;
using System.Text;

namespace DarkHaven.Launcher.Data;

/// <summary>
/// Encrypts the stored auth token. It is a bearer credential good for ~30 days, and settings.db is a
/// plain SQLite file any process running as the same user could otherwise just read.
/// <list type="bullet">
/// <item>Windows: DPAPI, tied to the Windows user account.</item>
/// <item>Elsewhere: AES-GCM with a key made from a random one kept in its own owner-only file next to the
/// database, this machine's id and the user's name. A copy of the launcher's folder - settings.db and the
/// key file together - is no good on another machine or to another account.</item>
/// </list>
/// Neither stops a program already running as this user on this machine: nothing on the player's side can.
/// </summary>
/// <param name="useDpapi">Null: DPAPI on Windows. Tests set it to try the other scheme anywhere.</param>
/// <param name="machineId">This machine's id; null reads /etc/machine-id. Tests stand in another machine.</param>
internal sealed class TokenProtector(string keyPath, bool? useDpapi = null, Func<string?>? machineId = null)
{
    /// <summary>The key file alone (up to launcher 0.3.9). Still read, and written again bound.</summary>
    private const string LegacyPrefix = "aes1:";

    /// <summary>The key file, bound to this machine and this user.</summary>
    private const string BoundPrefix = "aes2:";

    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly bool _dpapi = useDpapi ?? OperatingSystem.IsWindows();
    private readonly Func<string?> _machineId = machineId ?? ReadMachineId;
    private byte[]? _key;

    public string Protect(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        if (_dpapi)
            return Convert.ToBase64String(ProtectOs(data, protect: true));

        var blob = new byte[NonceSize + data.Length + TagSize];
        var nonce = blob.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(Bind(GetKey(create: true)!), TagSize);
        aes.Encrypt(nonce, data, blob.AsSpan(NonceSize, data.Length), blob.AsSpan(NonceSize + data.Length));
        return BoundPrefix + Convert.ToBase64String(blob);
    }

    /// <summary>
    /// Null when the value can't be decrypted: an old plaintext row, another user's or machine's blob,
    /// a lost key — treated as absent, not fatal.
    /// </summary>
    public string? Unprotect(string stored)
    {
        try
        {
            if (_dpapi)
            {
                return stored.StartsWith(LegacyPrefix, StringComparison.Ordinal) || stored.StartsWith(BoundPrefix, StringComparison.Ordinal)
                    ? null
                    : Encoding.UTF8.GetString(ProtectOs(Convert.FromBase64String(stored), protect: false));
            }

            var bound = stored.StartsWith(BoundPrefix, StringComparison.Ordinal);
            if (!bound && !stored.StartsWith(LegacyPrefix, StringComparison.Ordinal) || GetKey(create: false) is not { } fileKey)
                return null;

            var blob = Convert.FromBase64String(stored[(bound ? BoundPrefix : LegacyPrefix).Length..]);
            if (blob.Length < NonceSize + TagSize)
                return null;

            var length = blob.Length - NonceSize - TagSize;
            var plain = new byte[length];
            using var aes = new AesGcm(bound ? Bind(fileKey) : fileKey, TagSize);
            aes.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize, length), blob.AsSpan(NonceSize + length), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Stored the old way, not bound to the machine: worth writing again.</summary>
    public bool IsLegacy(string stored) => !_dpapi && stored.StartsWith(LegacyPrefix, StringComparison.Ordinal);

    /// <summary>The key that actually encrypts: the file's key, this machine's id and this user's name, together.</summary>
    private byte[] Bind(byte[] fileKey) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, fileKey, KeySize,
            salt: Encoding.UTF8.GetBytes(_machineId() ?? string.Empty),
            info: Encoding.UTF8.GetBytes($"Frontier15Launcher token v2|{Environment.UserName}"));

    /// <summary>systemd's /etc/machine-id (D-Bus's copy on older systems): stable per install, different per machine.</summary>
    private static string? ReadMachineId()
    {
        foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            try
            {
                if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } id)
                    return id;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // try the next one
            }
        }
        return null;
    }

    private static byte[] ProtectOs(byte[] data, bool protect)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI is Windows-only");
        return protect
            ? ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser)
            : ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
    }

    private byte[]? GetKey(bool create)
    {
        if (_key is not null)
            return _key;

        if (File.Exists(keyPath))
        {
            var existing = File.ReadAllBytes(keyPath);
            if (existing.Length == KeySize)
                return _key = existing;

            // Damaged: whatever it encrypted is lost either way.
            if (!create)
                return null;
            File.Delete(keyPath);
        }
        else if (!create)
        {
            return null;
        }

        var key = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            // Owner-only from the moment it exists: no window where another user could read it.
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var file = new FileStream(keyPath, options);
            file.Write(key);
        }
        catch (IOException) when (File.Exists(keyPath))
        {
            // Another launcher process made it first; use theirs.
            key = File.ReadAllBytes(keyPath);
        }

        return _key = key;
    }
}
