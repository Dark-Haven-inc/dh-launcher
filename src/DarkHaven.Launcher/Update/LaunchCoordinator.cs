using System.Diagnostics;
using DarkHaven.Launcher.Accounts;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Content;
using DarkHaven.Launcher.Models;
using Serilog;

namespace DarkHaven.Launcher.Update;

public enum LaunchPhase { Resolving, Updating, Authenticating, StartingClient, ClientRunning, Done, Failed }

public sealed record LaunchProgress(LaunchPhase Phase, string Message, double? Fraction = null);

/// <summary>
/// End-to-end connect: resolve <c>/info</c> → bring content + engine up to date → attach the active
/// account → launch the client. The one place the CLI and the GUI share for "connect to a server".
/// </summary>
public sealed class LaunchCoordinator(
    ServerInfoApi serverInfo,
    ContentUpdater content,
    AccountManager accounts,
    GameLauncher game)
{
    public async Task<Process> ConnectAsync(
        string address,
        bool allowGuest,
        bool compatMode,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken cancel = default)
    {
        void Report(LaunchPhase p, string m, double? f = null) => progress?.Report(new LaunchProgress(p, m, f));

        Report(LaunchPhase.Resolving, "Contacting server…");
        var resolved = await serverInfo.GetAsync(address, cancel);
        var build = resolved.Info.Build
                    ?? throw new InvalidOperationException("Server did not provide build information");

        Report(LaunchPhase.Updating, $"Updating {build.ForkId ?? "content"}…");
        var lastFrac = -1.0;
        void DownloadProgress(long done, long total, string unit)
        {
            if (total <= 0) return;
            var frac = (double)done / total;
            if (Math.Abs(frac - lastFrac) < 0.01) return;
            lastFrac = frac;
            progress?.Report(new LaunchProgress(LaunchPhase.Updating, $"Downloading {unit}: {done:N0}/{total:N0}", frac));
        }

        var launch = await content.UpdateAsync(build, DownloadProgress, cancel);

        Report(LaunchPhase.Authenticating, "Preparing account…");
        GameAccount? account = null;
        var active = accounts.Active ?? accounts.Accounts.FirstOrDefault();
        if (active is not null)
            account = await accounts.ToGameAccountAsync(active, cancel);

        if (account is null && resolved.Info.Auth.Mode == AuthMode.Required)
            throw new InvalidOperationException("This server requires a Space Station 14 account. Log in first.");
        if (account is null && !allowGuest && resolved.Info.Auth.Mode != AuthMode.Disabled)
            Log.Warning("Connecting to {Server} as a guest", address);

        Report(LaunchPhase.StartingClient, "Starting client…");
        var proc = game.Start(resolved, launch, account, compatMode);

        Report(LaunchPhase.ClientRunning, "Client running");
        return proc;
    }
}
