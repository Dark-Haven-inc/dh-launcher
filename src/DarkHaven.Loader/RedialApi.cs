using System.Diagnostics;
using System.Text;
using Robust.LoaderApi;

namespace DarkHaven.Loader;

/// <summary>
/// Lets the running engine ask the launcher to reconnect / connect elsewhere (region gates).
/// Re-invokes the launcher exe with a <c>--commands</c> batch; env vars that would leak state
/// into the next client are stripped. Port of the reference <c>RedialApi</c>.
/// </summary>
internal sealed class RedialApi(string launcherPath) : IRedialApi
{
    private static readonly string[] EnvVarsToClear =
    [
        "ROBUST_AUTH_TOKEN", "ROBUST_AUTH_USERID", "ROBUST_AUTH_PUBKEY", "ROBUST_AUTH_SERVER", "DH_LAUNCH_PROOF",
        "SS14_LOADER_CONTENT_DB", "SS14_LOADER_CONTENT_VERSION", "SS14_LOADER_OVERLAY_ZIP",
        "SS14_DISABLE_SIGNING", "SS14_LAUNCHER_PATH", "SS14_LOG_CLIENT",
        "DOTNET_MULTILEVEL_LOOKUP", "DOTNET_TieredPGO", "DOTNET_TC_QuickJitForLoops",
        "DOTNET_ReadyToRun", "DOTNET_gcServer",
    ];

    public void Redial(Uri uri, string text = "")
    {
        var reasonCommand = "R" + Convert.ToHexString(Encoding.UTF8.GetBytes(text));
        var connectCommand = "C" + Convert.ToHexString(Encoding.UTF8.GetBytes(uri.ToString()));

        var startInfo = new ProcessStartInfo
        {
            FileName = launcherPath,
            UseShellExecute = false,
            ArgumentList = { "--commands", ":RedialWait", reasonCommand, connectCommand },
        };

        foreach (var envVar in EnvVarsToClear)
            startInfo.EnvironmentVariables.Remove(envVar);

        Process.Start(startInfo);
    }
}
