using System.Diagnostics;

namespace DarkHaven.App;

/// <summary>
/// The one place that hands a URL to the OS shell (<c>ShellExecute</c>) to open in the default
/// browser. <c>ProcessStartInfo(url) { UseShellExecute = true }</c> runs whatever the shell
/// associates with the string — for an http(s) URL that's the browser, but for a UNC path
/// (<c>\\host\share\evil.exe</c>), a <c>file:</c> URI, or a registered custom protocol it can mean
/// running an arbitrary program instead. Some of the callers only ever pass a hardcoded literal
/// (safe regardless), but others pass a link pulled from server-supplied content (news items) —
/// content nobody here authors by hand — so every caller goes through this one check instead of
/// each deciding for itself whether its particular source can be trusted.
/// </summary>
public static class SafeUrl
{
    public static bool Open(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
