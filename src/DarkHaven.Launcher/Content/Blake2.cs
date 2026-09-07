using Blake2Fast;

namespace DarkHaven.Launcher.Content;

/// <summary>BLAKE2b-256, unkeyed — the hash SS14 uses for content manifests and blobs.</summary>
public static class Blake2
{
    public const int DigestBytes = 32;

    public static byte[] Hash(ReadOnlySpan<byte> data) => Blake2b.ComputeHash(DigestBytes, data);

    public static string HashHex(ReadOnlySpan<byte> data) => Convert.ToHexString(Hash(data));

    /// <summary>Hashes <paramref name="stream"/> from its current position to the end.</summary>
    public static byte[] HashStream(Stream stream)
    {
        var hasher = Blake2b.CreateIncrementalHasher(DigestBytes);
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer)) > 0)
            hasher.Update(buffer.AsSpan(0, read));
        return hasher.Finish();
    }
}
