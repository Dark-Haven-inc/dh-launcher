using System.Security.Cryptography;
using System.Text;

namespace DarkHaven.Launcher.Data;

/// <summary>
/// Encrypts the stored auth token. It is a bearer credential good for ~30 days, and settings.db is a
/// plain SQLite file any process running as the same user could otherwise just read.
/// <list type="bullet">
/// <item>Windows: DPAPI, tied to the Windows user account.</item>
/// <item>Elsewhere: AES-GCM with a random key kept in its own owner-only file next to the database, so
/// copying settings.db alone is not enough to steal a session.</item>
/// </list>
/// </summary>
internal sealed class TokenProtector(string keyPath)
{
    private const string AesPrefix = "aes1:";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private byte[]? _key;

    public string Protect(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        if (OperatingSystem.IsWindows())
            return Convert.ToBase64String(ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser));

        var blob = new byte[NonceSize + data.Length + TagSize];
        var nonce = blob.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(GetKey(create: true)!, TagSize);
        aes.Encrypt(nonce, data, blob.AsSpan(NonceSize, data.Length), blob.AsSpan(NonceSize + data.Length));
        return AesPrefix + Convert.ToBase64String(blob);
    }

    /// <summary>
    /// Null when the value can't be decrypted: an old plaintext row, another user's or machine's blob,
    /// a lost key — treated as absent, not fatal.
    /// </summary>
    public string? Unprotect(string stored)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return stored.StartsWith(AesPrefix, StringComparison.Ordinal)
                    ? null
                    : Encoding.UTF8.GetString(
                        ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
            }

            if (!stored.StartsWith(AesPrefix, StringComparison.Ordinal) || GetKey(create: false) is not { } key)
                return null;

            var blob = Convert.FromBase64String(stored[AesPrefix.Length..]);
            if (blob.Length < NonceSize + TagSize)
                return null;

            var length = blob.Length - NonceSize - TagSize;
            var plain = new byte[length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize, length), blob.AsSpan(NonceSize + length), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception)
        {
            return null;
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
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
            using var file = new FileStream(keyPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
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
