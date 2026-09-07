using System.Diagnostics.CodeAnalysis;

namespace DarkHaven.Launcher.Models;

/// <summary>
/// Helpers for <c>ss14://</c> / <c>ss14s://</c> URIs and the HTTP endpoints derived from them.
/// See https://github.com/space-wizards/RobustToolbox/wiki/ss14:---and-ss14s:---URI-handling
/// </summary>
public static class Ss14Address
{
    public const string SchemeInsecure = "ss14";
    public const string SchemeSecure = "ss14s";

    /// <summary>Default UDP/HTTP port for a bare <c>ss14://</c> address.</summary>
    public const int DefaultPort = 1212;

    public static Uri Parse(string address)
    {
        if (!TryParse(address, out var uri))
            throw new FormatException($"Not a valid SS14 URI: {address}");
        return uri;
    }

    public static bool TryParse(string address, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(address))
            return false;

        // Bare "host[:port][/path]" defaults to ss14://.
        if (!address.Contains("://"))
            address = SchemeInsecure + "://" + address;

        if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed))
            return false;

        if (parsed.Scheme != SchemeInsecure && parsed.Scheme != SchemeSecure)
            return false;

        if (string.IsNullOrWhiteSpace(parsed.Host))
            return false;

        uri = parsed;
        return true;
    }

    /// <summary>The <c>http(s)://host[:port]/[path]/</c> base the server's HTTP API lives under.</summary>
    public static Uri ApiBase(Uri serverAddress)
    {
        var scheme = serverAddress.Scheme switch
        {
            SchemeInsecure => Uri.UriSchemeHttp,
            SchemeSecure => Uri.UriSchemeHttps,
            _ => throw new ArgumentException($"Wrong URI scheme: {serverAddress.Scheme}"),
        };

        var builder = new UriBuilder(serverAddress) { Scheme = scheme };

        // ss14:// with no explicit port -> 1212. ss14s:// with no port -> 443 (HTTPS default, leave alone).
        if (serverAddress.IsDefaultPort && serverAddress.Scheme == SchemeInsecure)
            builder.Port = DefaultPort;

        if (!builder.Path.EndsWith('/'))
            builder.Path += "/";

        // Drop the userinfo/query/fragment that UriBuilder may carry over.
        builder.Query = "";
        builder.Fragment = "";
        return builder.Uri;
    }

    public static Uri InfoAddress(Uri serverAddress) => new(ApiBase(serverAddress), "info");
    public static Uri StatusAddress(Uri serverAddress) => new(ApiBase(serverAddress), "status");
    public static Uri SelfhostedClientZip(Uri serverAddress) => new(ApiBase(serverAddress), "client.zip");
    public static Uri SelfhostedManifest(Uri serverAddress) => new(ApiBase(serverAddress), "manifest.txt");
    public static Uri SelfhostedManifestDownload(Uri serverAddress) => new(ApiBase(serverAddress), "download");

    /// <summary>Derives the <c>udp://host:port</c> the client connects to when <c>connect_address</c> is empty.</summary>
    public static Uri DeriveConnectAddress(Uri serverAddress)
    {
        var api = ApiBase(serverAddress);
        return new UriBuilder { Scheme = "udp", Host = api.Host, Port = api.Port }.Uri;
    }
}
