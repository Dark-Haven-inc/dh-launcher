using DarkHaven.Launcher.Security;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public sealed class GameEnvironmentTests
{
    [Theory]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("dotnet_startup_hooks")]
    [InlineData("DOTNET_ADDITIONAL_DEPS")]
    [InlineData("DOTNET_SHARED_STORE")]
    [InlineData("SS14_LOADER_OVERLAY_ZIP")]
    [InlineData("SS14_DISABLE_SIGNING")]
    [InlineData("ROBUST_DISABLE_SANDBOX")]
    [InlineData("ROBUST_MODULE_ROBUST_CLIENT_WEBVIEW")]
    [InlineData("CORECLR_ENABLE_PROFILING")]
    [InlineData("CORECLR_PROFILER_PATH_64")]
    [InlineData("CORECLR_NOTIFICATION_PROFILERS")]
    [InlineData("COR_PROFILER")]
    [InlineData("DOTNET_EnableDiagnostics")]
    [InlineData("DOTNET_EnableDiagnostics_IPC")]
    [InlineData("COMPlus_EnableDiagnostics")]
    [InlineData("DOTNET_DiagnosticPorts")]
    [InlineData("COMPlus_DefaultDiagnosticPortSuspend")]
    [InlineData("DOTNET_GCName")]
    [InlineData("DOTNET_GCPath")]
    [InlineData("COMPlus_AltJitName")]
    public void InjectingVariablesAreRecognised(string name)
    {
        Assert.True(GameEnvironment.IsInjecting(name));
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("LD_PRELOAD")]
    [InlineData("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT")]
    [InlineData("DOTNET_gcServer")]
    [InlineData("DOTNET_GCHeapHardLimit")]
    [InlineData("ROBUST_INTEGRATED_GPU")]
    [InlineData("ROBUST_CVARS")]
    [InlineData("SS14_LOADER_CONTENT_POOL_SIZE")]
    [InlineData("DOTNETX_STARTUP_HOOKS")]
    public void EverythingElseIsPassedOn(string name)
    {
        Assert.False(GameEnvironment.IsInjecting(name));
    }

    [Fact]
    public void HardenStripsInjectionAndClosesDiagnostics()
    {
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin",
            ["DOTNET_STARTUP_HOOKS"] = "/tmp/hook.dll",
            ["CORECLR_ENABLE_PROFILING"] = "1",
            ["CORECLR_PROFILER_PATH"] = "/tmp/profiler.so",
            ["SS14_LOADER_OVERLAY_ZIP"] = "/tmp/overlay.zip",
            ["ROBUST_DISABLE_SANDBOX"] = "1",
            ["DOTNET_EnableDiagnostics_IPC"] = "1",
            ["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1",
        };

        GameEnvironment.Harden(env);

        Assert.Equal(new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin",
            ["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1",
            ["DOTNET_EnableDiagnostics_IPC"] = "0",
            ["DOTNET_EnableDiagnostics_Debugger"] = "0",
            ["DOTNET_EnableDiagnostics_Profiler"] = "0",
        }, env);
    }
}
