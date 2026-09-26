using Serilog;
using Velopack;
using Velopack.Sources;

namespace DarkHaven.Launcher.Update;

public enum UpdatePhase
{
    /// <summary>Nothing to do, or self-update isn't available (running a dev / unpacked build).</summary>
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToRestart,
    Failed,
}

/// <summary>
/// Thin wrapper over Velopack's <see cref="UpdateManager"/>. Releases live as GitHub Releases on
/// the public, releases-only <c>Dark-Haven-inc/frontier15-launcher</c> (the source repo can then be
/// private: the updater reads the feed anonymously). Launchers up to 0.3.4 still read
/// <c>Dark-Haven-inc/dh-launcher</c>, which is why release.yml mirrors there while it's public.
/// The feed is overridable via the <c>UpdateFeedUrl</c> config key (a GitHub repo URL or a plain
/// static-file base URL).
/// Self-update only works from an installed build — a dev <c>dotnet run</c> reports
/// <see cref="Supported"/> <c>false</c> and every call is a no-op.
/// </summary>
public sealed class LauncherUpdater
{
    public const string DefaultFeedUrl = "https://github.com/Dark-Haven-inc/frontier15-launcher";

    private readonly UpdateManager? _mgr;
    private UpdateInfo? _pending;

    public LauncherUpdater(string? feedUrlOverride = null, string? channel = null)
    {
        var feed = string.IsNullOrWhiteSpace(feedUrlOverride) ? DefaultFeedUrl : feedUrlOverride!.Trim();
        try
        {
            IUpdateSource source = feed.Contains("github.com", StringComparison.OrdinalIgnoreCase)
                ? new GithubSource(feed, accessToken: null, prerelease: false)
                : new SimpleWebSource(feed);

            var options = string.IsNullOrWhiteSpace(channel) ? null : new UpdateOptions { ExplicitChannel = channel };
            _mgr = new UpdateManager(source, options);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Launcher self-update init failed — feature disabled this session");
            _mgr = null;
        }
    }

    /// <summary>True only when running an installed build that can actually apply updates.</summary>
    public bool Supported => _mgr is { IsInstalled: true };

    public string CurrentVersion => _mgr?.CurrentVersion?.ToString() ?? LauncherInfo.Version;

    public string? PendingVersion => _pending?.TargetFullRelease?.Version.ToString();

    /// <summary>Returns true if a newer release is available (and remembers it for <see cref="DownloadAsync"/>).</summary>
    public async Task<bool> CheckAsync(CancellationToken cancel = default)
    {
        if (_mgr is not { IsInstalled: true })
            return false;

        try
        {
            _pending = await Task.Run(() => _mgr.CheckForUpdatesAsync(), cancel);
            if (_pending is not null)
                Log.Information("Launcher update available: {Version}", PendingVersion);
            return _pending is not null;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Launcher update check failed");
            return false;
        }
    }

    public async Task DownloadAsync(Action<int>? progress = null, CancellationToken cancel = default)
    {
        if (_mgr is null || _pending is null)
            return;

        await _mgr.DownloadUpdatesAsync(_pending, p => progress?.Invoke(p), cancelToken: cancel);
    }

    /// <summary>Applies the downloaded update and relaunches the launcher. Does not return on success.</summary>
    public void ApplyAndRestart()
    {
        if (_mgr is null || _pending is null)
            return;

        _mgr.ApplyUpdatesAndRestart(_pending);
    }
}
