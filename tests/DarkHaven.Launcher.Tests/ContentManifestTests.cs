using System.Text;
using DarkHaven.Launcher.Content;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public class ContentManifestTests
{
    private static byte[] Manifest(string body) => Encoding.UTF8.GetBytes(body);

    [Fact]
    public void Parses_a_well_formed_manifest()
    {
        var body = "Robust Content Manifest 1\n"
                   + "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF Assemblies/Content.Client.dll\n"
                   + "FFEEDDCCBBAA99887766554433221100FFEEDDCCBBAA998877665544332211 Prototypes/entities.yml\n";
        var raw = Manifest(body);
        var hash = Blake2.HashHex(raw);

        var manifest = ContentManifest.ParseAndVerify(raw, hash);

        Assert.Equal(2, manifest.Entries.Count);
        Assert.Equal("Assemblies/Content.Client.dll", manifest.Entries[0].Path);
        Assert.Equal(32, manifest.Entries[0].Hash.Length);
    }

    [Fact]
    public void Rejects_a_hash_mismatch()
    {
        var raw = Manifest("Robust Content Manifest 1\n");
        Assert.Throws<InvalidDataException>(() => ContentManifest.ParseAndVerify(raw, new string('0', 64)));
    }

    [Fact]
    public void Rejects_a_bad_header()
    {
        var raw = Manifest("Not A Manifest\n");
        Assert.Throws<InvalidDataException>(() => ContentManifest.ParseAndVerify(raw, Blake2.HashHex(raw)));
    }

    [Fact]
    public void Rejects_a_malformed_line()
    {
        var raw = Manifest("Robust Content Manifest 1\nnospacehere\n");
        Assert.Throws<InvalidDataException>(() => ContentManifest.ParseAndVerify(raw, Blake2.HashHex(raw)));
    }

    [Fact]
    public void Paths_with_spaces_survive()
    {
        var body = "Robust Content Manifest 1\n"
                   + "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF Textures/my folder/a b.png\n";
        var raw = Manifest(body);
        var manifest = ContentManifest.ParseAndVerify(raw, Blake2.HashHex(raw));
        Assert.Equal("Textures/my folder/a b.png", manifest.Entries[0].Path);
    }
}
