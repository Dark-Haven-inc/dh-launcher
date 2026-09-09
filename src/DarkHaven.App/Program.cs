using Avalonia;
using DarkHaven.Launcher;
using Serilog;
using Velopack;

namespace DarkHaven.App;

internal static class Program
{
    /// <summary>The <c>ss14(s)://</c> address passed on the command line, if any.</summary>
    public static string? LaunchUri { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Must run before anything else: handles Velopack's install / update / uninstall hooks
        // (the process exits inside Run() when invoked for one of those).
        var velo = VelopackApp.Build();
        if (OperatingSystem.IsWindows())
        {
            velo = velo
                .OnFirstRun(_ => RegisterUriScheme())
                .OnAfterUpdateFastCallback(_ => RegisterUriScheme());
        }
        velo.Run();

        LauncherPaths.EnsureDirectories();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(LauncherPaths.LogsDir, "launcher-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();

        // Keep the ss14:// association pointed at the current install on every normal launch.
        // Skipped for a dev build so `dotnet run` doesn't hijack the scheme from an install.
        if (!IsDevBuild())
            RegisterUriScheme();

        LaunchUri = args.FirstOrDefault(a => a.StartsWith("ss14://") || a.StartsWith("ss14s://"));

        // Hand off to an already-running launcher (and exit) rather than opening a second window.
        if (!IsDevBuild() && !SingleInstance.TryAcquire(LaunchUri))
        {
            Log.Information("Another launcher instance is running — forwarded and exiting");
            Log.CloseAndFlush();
            return 0;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            Log.Fatal(e, "Unhandled exception");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void RegisterUriScheme()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
            UriScheme.EnsureRegistered(exe);
    }

    /// <summary>True when running straight from <c>bin/Debug</c> or <c>bin/Release</c> build output.</summary>
    private static bool IsDevBuild()
    {
        var dir = AppContext.BaseDirectory.Replace('\\', '/');
        return dir.Contains("/bin/Debug/", StringComparison.OrdinalIgnoreCase)
               || dir.Contains("/bin/Release/", StringComparison.OrdinalIgnoreCase);
    }
}
