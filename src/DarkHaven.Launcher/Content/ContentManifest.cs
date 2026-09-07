namespace DarkHaven.Launcher.Content;

/// <summary>One line of a <c>Robust Content Manifest 1</c>: a blob hash and the path it maps to.</summary>
public readonly record struct ContentManifestEntry(byte[] Hash, string Path);

/// <summary>A parsed + hash-verified content manifest.</summary>
public sealed class ContentManifest
{
    public const string Header = "Robust Content Manifest 1";

    public required byte[] ManifestHash { get; init; }
    public required IReadOnlyList<ContentManifestEntry> Entries { get; init; }

    /// <summary>
    /// Parses the manifest text and verifies its BLAKE2b hash against <paramref name="expectedHashHex"/>.
    /// </summary>
    public static ContentManifest ParseAndVerify(byte[] rawManifest, string expectedHashHex)
    {
        var actualHash = Blake2.Hash(rawManifest);
        if (!Convert.ToHexString(actualHash).Equals(expectedHashHex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Manifest hash mismatch: expected {expectedHashHex}, got {Convert.ToHexString(actualHash)}");

        using var reader = new StreamReader(new MemoryStream(rawManifest, writable: false));

        var headerLine = reader.ReadLine();
        if (headerLine != Header)
            throw new InvalidDataException($"Unexpected manifest header: '{headerLine}'");

        var entries = new List<ContentManifestEntry>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
                continue;

            var sep = line.IndexOf(' ');
            if (sep <= 0)
                throw new InvalidDataException($"Malformed manifest line: '{line}'");

            var hash = Convert.FromHexString(line.AsSpan(0, sep));
            var path = line[(sep + 1)..];
            entries.Add(new ContentManifestEntry(hash, path));
        }

        return new ContentManifest { ManifestHash = actualHash, Entries = entries };
    }
}
