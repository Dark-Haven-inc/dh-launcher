using Avalonia.Media.Imaging;
using DarkHaven.Launcher.Api;

namespace DarkHaven.App;

/// <summary>
/// Avatars and banners from the platform. Media URLs never change content (a new upload gets a new
/// id), so the bytes are kept for the session; every caller gets its own <see cref="Bitmap"/> to own
/// and dispose — a shared one could be disposed by one screen while another still shows it.
/// </summary>
public sealed class RemoteImages(PlatformApi platform)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<byte[]?>> _bytes = new();

    public async Task<Bitmap?> GetAsync(string? path)
    {
        if (path is null)
            return null;

        var bytes = await _bytes.GetOrAdd(path, platform.GetMediaAsync(path));
        if (bytes is null)
        {
            _bytes.TryRemove(path, out _); // try again next time rather than remember a failure
            return null;
        }

        try { return new Bitmap(new MemoryStream(bytes)); }
        catch { return null; }
    }
}
