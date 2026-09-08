using System.Diagnostics;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Content;
using DarkHaven.Launcher.Engine;
using DarkHaven.Launcher.Models;
using Serilog;

namespace DarkHaven.Launcher.Update;

/// <summary>Auth material to hand the client, or null for a guest connection.</summary>
public sealed record GameAccount(string Username, string Token, Guid UserId);

/// <summary>
/// Builds the loader command line + environment and starts the game client, mirroring the reference
/// launcher's <c>Connector.ConnectLaunchClient</c>.
/// </summary>
public sealed class GameLauncher(string loaderPath, string signingKeyPath, EngineManager engines, string contentDbPath)
{
    public Process Start(
        ResolvedServerInfo server,
        LaunchManifest launch,
        GameAccount? account,
        bool compatMode = false,
        bool redirectOutput = false,
        IEnumerable<string>? extraCvars = null)
    {
        var enginePath = engines.EnginePath(launch.EngineVersion);
        var engineSig = engines.EngineSignatureHex(launch.EngineVersion);

        var psi = new ProcessStartInfo
        {
            FileName = loaderPath,
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
        };

        psi.ArgumentList.Add(enginePath);
        psi.ArgumentList.Add(engineSig);
        psi.ArgumentList.Add(signingKeyPath);

        void Arg(string a) => psi.ArgumentList.Add(a);
        void Cvar(string kv) { Arg("--cvar"); Arg(kv); }

        Arg("--username");
        Arg(account?.Username ?? "JoeGenero");
        Cvar($"display.compat={compatMode.ToString().ToLowerInvariant()}");
        Cvar("launch.launcher=true");

        foreach (var kv in extraCvars ?? [])
            Cvar(kv);

        Arg("--launcher");
        Arg("--connect-address");
        Arg(server.ConnectAddress.ToString());
        Arg("--ss14-address");
        Arg(server.ServerUri.ToString());

        var build = server.Info.Build;
        if (build is not null)
        {
            BuildCvar("engine_version", build.EngineVersion);
            BuildCvar("version", build.Version);
            BuildCvar("fork_id", build.ForkId);
            BuildCvar("hash", build.Hash);
            BuildCvar("manifest_hash", build.ManifestHash);
            BuildCvar("manifest_url", build.ManifestUrl);
            BuildCvar("manifest_download_url", build.ManifestDownloadUrl);
            BuildCvar("download_url", build.DownloadUrl);
        }

        void BuildCvar(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                Cvar($"build.{name}={value}");
        }

        var env = psi.EnvironmentVariables;
        env["SS14_LOADER_CONTENT_DB"] = contentDbPath;
        env["SS14_LOADER_CONTENT_VERSION"] = launch.VersionId.ToString();
        env["SS14_LAUNCHER_PATH"] = Environment.ProcessPath ?? "";
        env["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        env["DOTNET_TieredPGO"] = "1";
        env["DOTNET_ReadyToRun"] = "0";

        if (account is not null && server.Info.Auth.Mode != AuthMode.Disabled)
        {
            env["ROBUST_AUTH_TOKEN"] = account.Token;
            env["ROBUST_AUTH_USERID"] = account.UserId.ToString();
            env["ROBUST_AUTH_PUBKEY"] = server.Info.Auth.PublicKey ?? "";
            env["ROBUST_AUTH_SERVER"] = "https://auth.spacestation14.com/";
        }

        foreach (var (name, version) in launch.Modules)
        {
            var modulePath = Path.Combine(Path.GetDirectoryName(contentDbPath)!, "modules", name, version);
            env[$"ROBUST_MODULE_{name.ToUpperInvariant().Replace('.', '_')}"] = modulePath;
        }

        Log.Debug("Launching: {File} {Args}", psi.FileName, string.Join(' ', psi.ArgumentList));
        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start loader process");
    }
}
