using System.Text.Json;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Content;
using DarkHaven.Launcher.Engine;
using DarkHaven.Launcher.Models;
using DarkHaven.Launcher.Security;
using Serilog;
using Serilog.Events;

namespace DarkHaven.Launcher.Update;

/// <summary>Auth material to hand the client, or null for a guest connection.</summary>
public sealed record GameAccount(string Username, string Token, Guid UserId);

/// <summary>
/// Starts the game client, mirroring the reference launcher's <c>Connector.ConnectLaunchClient</c>. The loader's
/// command line and environment are built by the guard (<see cref="Guard"/>, docs/GUARD.md) from the facts gathered
/// here; in release builds it also checks the loader and engine, and runs the broker that vouches for the game.
/// </summary>
public sealed class GameLauncher(string loaderPath, string signingKeyPath, EngineManager engines, string contentDbPath)
{
    public GameProcess Start(
        ResolvedServerInfo server,
        LaunchManifest launch,
        GameAccount? account,
        bool compatMode = false,
        bool redirectOutput = false,
        IEnumerable<string>? extraCvars = null)
    {
        var request = CreateRequest(server, launch, account, compatMode, redirectOutput, extraCvars);

        // In an AppImage the game starts from a copy of the loader (LoaderCopy), checked and remade as needed.
        var game = LoaderCopy.Start(request.LoaderPath, loader =>
        {
            var started = request with { LoaderPath = loader };
            if (Log.IsEnabled(LogEventLevel.Debug))
            {
                var shown = started with { Account = started.Account is { } a ? a with { Token = "(hidden)" } : null };
                Log.Debug("Launching: {Request}", JsonSerializer.Serialize(shown, GuardJsonContext.Default.GuardLaunchRequest));
            }
            return GameProcess.Start(started);
        });
        if (game.BrokerState == GuardBrokerState.Failed)
            Log.Error("Could not start the launch broker; Frontier 15 servers will not let this game in");
        return game;
    }

    internal GuardLaunchRequest CreateRequest(
        ResolvedServerInfo server,
        LaunchManifest launch,
        GameAccount? account,
        bool compatMode,
        bool redirectOutput,
        IEnumerable<string>? extraCvars)
    {
        var build = server.Info.Build;
        return new GuardLaunchRequest
        {
            LoaderPath = Path.GetFullPath(loaderPath),
            EnginePath = engines.EnginePath(launch.EngineVersion),
            EngineSignature = engines.EngineSignatureHex(launch.EngineVersion),
            EnginePublicKeyPath = signingKeyPath,
            ContentDbPath = contentDbPath,
            ContentVersion = launch.VersionId,
            Modules = launch.Modules.Select(m => new GuardModule(m.Name, m.Version)).ToList(),
            LauncherPath = LauncherInfo.ExecutablePath,
            Username = account?.Username,
            CompatMode = compatMode,
            ConnectAddress = server.ConnectAddress.ToString(),
            Ss14Address = server.ServerUri.ToString(),
            Build = build is null
                ? null
                : new GuardBuild
                {
                    EngineVersion = build.EngineVersion,
                    Version = build.Version,
                    ForkId = build.ForkId,
                    Hash = build.Hash,
                    ManifestHash = build.ManifestHash,
                    ManifestUrl = build.ManifestUrl,
                    ManifestDownloadUrl = build.ManifestDownloadUrl,
                    DownloadUrl = build.DownloadUrl,
                },
            ExtraCvars = extraCvars?.ToList() ?? [],
            // Auth (and with it the broker and the proofs) only for a server that uses it.
            Account = account is not null && server.Info.Auth.Mode != AuthMode.Disabled
                ? new GuardAccount(account.Username, account.Token, account.UserId, server.Info.Auth.PublicKey)
                : null,
            RedirectOutput = redirectOutput,
        };
    }
}
