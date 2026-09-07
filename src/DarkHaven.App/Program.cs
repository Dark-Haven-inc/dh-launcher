using Avalonia;
using DarkHaven.Launcher;
using Serilog;

namespace DarkHaven.App;

internal static class Program
{
    /// <summary>The <c>ss14(s)://</c> address passed on the command line, if any.</summary>
    public static string? LaunchUri { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        LauncherPaths.EnsureDirectories();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(LauncherPaths.LogsDir, "launcher-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();

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
}
