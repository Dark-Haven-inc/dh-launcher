using SkiaSharp;

namespace DarkHaven.App;

/// <summary>
/// Gets a picked picture ready to upload. The platform takes up to 4 MB, and a phone photo or a big
/// PNG is easily over that — rather than refuse it, send a version already close to what the
/// platform will keep. The platform still checks, crops and re-encodes everything itself.
/// </summary>
public static class UploadImage
{
    /// <summary>Twice the platform's own slot size, so its crop stays sharp.</summary>
    private static readonly Dictionary<string, (int Width, int Height)> Limits = new()
    {
        ["avatar"] = (512, 512),
        ["banner"] = (1920, 640),
    };

    private const int SendAsIsBelowBytes = 1024 * 1024;
    private const long MaxSourcePixels = 50_000_000;

    /// <returns>The bytes to upload, or null and why not.</returns>
    public static (byte[]? Bytes, string? Error) Prepare(string path, string slot)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch { return (null, "Не получилось прочитать выбранный файл."); }

        using var codec = SKCodec.Create(new SKMemoryStream(data));
        if (codec is null)
            return (null, "Это не картинка — подойдут PNG, JPG, WebP или GIF.");

        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if ((long)width * height > MaxSourcePixels)
            return (null, "Картинка слишком большая — больше 50 мегапикселей.");

        var (maxW, maxH) = Limits[slot];
        if (data.Length <= SendAsIsBelowBytes && width <= maxW && height <= maxH)
            return (data, null);

        // Cover the slot's box (the platform crops the rest), never upscale.
        var scale = Math.Min(1.0, Math.Max((double)maxW / width, (double)maxH / height));
        var target = new SKImageInfo(
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)),
            SKColorType.Rgba8888, SKAlphaType.Premul);

        using var source = SKBitmap.Decode(codec);
        if (source is null)
            return (null, "Не получилось прочитать картинку.");
        using var resized = source.Resize(target, SKFilterQuality.High);
        if (resized is null)
            return (null, "Не получилось уменьшить картинку.");

        using var image = SKImage.FromBitmap(resized);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, 90); // keeps transparency, unlike JPEG
        return encoded is null ? (null, "Не получилось уменьшить картинку.") : (encoded.ToArray(), null);
    }
}
