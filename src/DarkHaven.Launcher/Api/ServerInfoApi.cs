using System.Net.Http.Json;
using DarkHaven.Launcher.Models;
using Serilog;

namespace DarkHaven.Launcher.Api;

/// <summary>
/// Fetches and normalises a server's <c>/info</c>. Handles ACZ / self-hosted URL derivation the
/// same way the reference launcher's <c>Connector.GetServerInfoAsync</c> does.
/// </summary>
public sealed class ServerInfoApi(HttpClient http)
{
    public async Task<ResolvedServerInfo> GetAsync(string address, CancellationToken cancel = default)
    {
        if (!Ss14Address.TryParse(address, out var uri))
            throw new FormatException($"Invalid SS14 address: {address}");

        return await GetAsync(uri, cancel);
    }

    public async Task<ResolvedServerInfo> GetAsync(Uri serverUri, CancellationToken cancel = default)
    {
        var infoAddr = Ss14Address.InfoAddress(serverUri);
        Log.Debug("Fetching server info: {InfoAddr}", infoAddr);

        var info = await http.GetFromJsonAsync<ServerInfo>(infoAddr, LauncherJson.Options, cancel)
                   ?? throw new InvalidDataException("Server /info returned null");

        if (info.Build is { } build && (build.Acz || string.IsNullOrEmpty(build.DownloadUrl)))
        {
            // Server self-hosts content; it may not know its own address, so derive the URLs.
            build.DownloadUrl = Ss14Address.SelfhostedClientZip(serverUri).ToString();
            if (build.Acz)
            {
                build.ManifestUrl = Ss14Address.SelfhostedManifest(serverUri).ToString();
                build.ManifestDownloadUrl = Ss14Address.SelfhostedManifestDownload(serverUri).ToString();
            }
        }

        var connectAddress = string.IsNullOrEmpty(info.ConnectAddress)
            ? Ss14Address.DeriveConnectAddress(serverUri)
            : new Uri(info.ConnectAddress);

        return new ResolvedServerInfo(serverUri, infoAddr, connectAddress, info);
    }
}

/// <summary>A <see cref="ServerInfo"/> with the derived connect address and the URI it came from.</summary>
public sealed record ResolvedServerInfo(Uri ServerUri, Uri InfoAddress, Uri ConnectAddress, ServerInfo Info);
