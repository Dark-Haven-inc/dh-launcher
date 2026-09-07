namespace DarkHaven.ContentDb;

/// <summary>
/// Compression scheme for a blob stored in the <c>Content.Data</c> column of the content DB.
/// Values are wire-compatible with the reference launcher's schema.
/// </summary>
public enum ContentCompressionScheme
{
    None = 0,
    Deflate = 1,
    ZStd = 2,
}
