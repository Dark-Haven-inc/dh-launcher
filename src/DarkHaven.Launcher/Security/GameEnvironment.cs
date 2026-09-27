namespace DarkHaven.Launcher.Security;

/// <summary>
/// Strips from the game's environment everything that would load code into it or change what it loads, and closes
/// the runtime's doors for attaching to it later.
/// </summary>
/// <remarks>
/// The launch broker vouches for the process the launcher starts, so that process has to run what we ship. It
/// inherits the player's environment, and a handful of variables would let anyone slip a cheat into a genuine game:
/// a .NET startup hook or profiler, a zip mounted over the content, the content sandbox switched off, an engine
/// module read from a directory of their choosing. None of them has a use for a player. Everything else is passed on
/// untouched - graphics, locale, overlay and performance tools included.
/// </remarks>
public static class GameEnvironment
{
    /// <summary>Removed in any letter case.</summary>
    private static readonly string[] Names =
    [
        // .NET host: code run at startup, extra or replaced assemblies.
        "DOTNET_STARTUP_HOOKS",
        "DOTNET_ADDITIONAL_DEPS",
        "DOTNET_SHARED_STORE",

        // Loader and engine: a zip over the content, an unsigned engine, no content sandbox.
        "SS14_LOADER_OVERLAY_ZIP",
        "SS14_DISABLE_SIGNING",
        "ROBUST_DISABLE_SANDBOX",
    ];

    /// <summary>Every variable starting with one of these is removed.</summary>
    private static readonly string[] Prefixes =
    [
        // Profilers: native code loaded into the runtime.
        "CORECLR_",
        "COR_ENABLE_PROFILING",
        "COR_PROFILER",

        // Engine modules loaded from elsewhere. The launcher sets the ones the server asks for.
        "ROBUST_MODULE_",
    ];

    /// <summary>
    /// Runtime settings that load a native library into the runtime or open the process to diagnostic tools. The
    /// runtime reads each under <c>DOTNET_</c> and <c>COMPlus_</c>; both are removed, as prefixes.
    /// </summary>
    private static readonly string[] RuntimeSettings =
    [
        "EnableDiagnostics",
        "DiagnosticPorts",
        "DefaultDiagnosticPortSuspend",
        "GCName",
        "GCPath",
        "AltJit",
    ];

    private static readonly string[] RuntimePrefixes = ["DOTNET_", "COMPlus_"];

    /// <summary>Whether <paramref name="name"/> is a variable that must not reach the game.</summary>
    public static bool IsInjecting(string name)
    {
        foreach (var candidate in Names)
        {
            if (name.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var prefix in Prefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var runtimePrefix in RuntimePrefixes)
        {
            if (!name.StartsWith(runtimePrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var setting = name.AsSpan(runtimePrefix.Length);
            foreach (var candidate in RuntimeSettings)
            {
                if (setting.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Removes the injecting variables from <paramref name="env"/> and turns off the runtime's debugger, profiler and
    /// diagnostic IPC for the game, so nothing attaches to it once it runs either.
    /// </summary>
    public static void Harden(IDictionary<string, string?> env)
    {
        foreach (var name in env.Keys.Where(IsInjecting).ToList())
            env.Remove(name);

        env["DOTNET_EnableDiagnostics_IPC"] = "0";
        env["DOTNET_EnableDiagnostics_Debugger"] = "0";
        env["DOTNET_EnableDiagnostics_Profiler"] = "0";
    }
}
