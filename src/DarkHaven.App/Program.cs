using System.Text;
using Avalonia;
using DarkHaven.Launcher;
using Serilog;
using Velopack;

namespace DarkHaven.App;

internal static class Program
{
    /// <summary>The <c>ss14(s)://</c> address to connect to on launch (a link click, or a redial), if any.</summary>
    public static string? LaunchUri { get; private set; }

    /// <summary>True when this launch is the engine asking to reconnect elsewhere (a region gate).</summary>
    public static bool IsRedial { get; private set; }

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

        // The loader re-invokes us as `--commands :RedialWait R<hex> C<hex>` when the engine wants
        // to reconnect elsewhere (a region gate). Decode the target address.
        if (LaunchUri is null && ParseRedial(args) is { } target)
        {
            LaunchUri = target;
            IsRedial = true;
        }

        // Hand off to an already-running launcher (and exit) rather than opening a second window.
        var forward = IsRedial && LaunchUri is not null ? "redial\n" + LaunchUri : LaunchUri;
        if (!IsDevBuild() && !SingleInstance.TryAcquire(forward))
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

    /// <summary>Decodes the <c>C&lt;hex&gt;</c> connect address out of a <c>--commands</c> redial batch.</summary>
    private static string? ParseRedial(string[] args)
    {
        var i = Array.IndexOf(args, "--commands");
        if (i < 0)
            return null;

        foreach (var token in args.Skip(i + 1))
        {
            if (token.Length > 1 && token[0] == 'C')
            {
                try { return Encoding.UTF8.GetString(Convert.FromHexString(token[1..])); }
                catch { return null; }
            }
        }
        return null;
    }

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
