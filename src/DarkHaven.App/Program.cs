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
        VelopackApp.Build()
            .OnFirstRun(_ => RegisterUriScheme())
            .OnAfterUpdateFastCallback(_ => RegisterUriScheme())
            .Run();

        LauncherPaths.EnsureDirectories();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(LauncherPaths.LogsDir, "launcher-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();

        // Keep the ss14:// association pointed at the current install on every normal launch too.
        if (IsInstalledBuild())
            RegisterUriScheme();

        LaunchUri = args.FirstOrDefault(a => a.StartsWith("ss14://") || a.StartsWith("ss14s://"));

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

    /// <summary>Velopack lays the app out as <c>&lt;root&gt;\current\</c> with <c>Update.exe</c> in <c>&lt;root&gt;</c>.</summary>
    private static bool IsInstalledBuild()
    {
        try
        {
            var parent = Directory.GetParent(AppContext.BaseDirectory)?.FullName;
            return parent is not null && File.Exists(Path.Combine(parent, "Update.exe"));
        }
        catch
        {
            return false;
        }
    }
}
