using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using Robust.LoaderApi;

namespace DarkHaven.Loader;

/// <summary>
/// <see cref="IFileApi"/> backed by a zip archive — used for the engine build and for content-bundle
/// overlays. A <paramref name="prefix"/> lets the engine see files at a path inside the archive
/// (the macOS engine build nests everything under an <c>.app</c> bundle).
/// </summary>
internal sealed class ZipFileApi(ZipArchive archive, string prefix = "") : IFileApi, IDisposable
{
    public bool TryOpen(string path, [NotNullWhen(true)] out Stream? stream)
    {
        var entry = archive.GetEntry(prefix.Length == 0 ? path : prefix + path);
        if (entry == null)
        {
            stream = null;
            return false;
        }

        var ms = new MemoryStream((int)entry.Length);
        lock (archive)
        {
            using var zipStream = entry.Open();
            zipStream.CopyTo(ms);
        }

        ms.Position = 0;
        stream = ms;
        return true;
    }

    public IEnumerable<string> AllFiles => prefix.Length == 0
        ? archive.Entries.Where(e => e.Name != "").Select(e => e.FullName)
        : archive.Entries.Where(e => e.Name != "" && e.FullName.StartsWith(prefix, StringComparison.Ordinal))
            .Select(e => e.FullName[prefix.Length..]);

    public void Dispose() => archive.Dispose();
}
