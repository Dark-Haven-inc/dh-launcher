using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace DarkHaven.Launcher.Api;

/// <summary>
/// Client for the SS14 "Wizard's Den" authentication server. Base URL can be overridden with the
/// <c>SS14_LAUNCHER_OVERRIDE_AUTH</c> env var (dev / private-auth testing).
/// </summary>
public sealed class AuthApi(HttpClient http, string? overrideBaseUrl = null)
{
    public const string DefaultBaseUrl = "https://auth.spacestation14.com/";

    public string BaseUrl { get; } = Normalize(
        Environment.GetEnvironmentVariable("SS14_LAUNCHER_OVERRIDE_AUTH") is { Length: > 0 } env
            ? env
            : overrideBaseUrl is { Length: > 0 } ? overrideBaseUrl : DefaultBaseUrl);

    private static string Normalize(string url) => url.EndsWith('/') ? url : url + "/";

    public async Task<AuthResult> AuthenticateAsync(
        string? username, Guid? userId, string password, string? tfaCode = null, CancellationToken cancel = default)
    {
        var request = new AuthenticateRequest(username, userId, password, tfaCode);
        using var resp = await http.PostAsJsonAsync(BaseUrl + "api/auth/authenticate", request, LauncherJson.Options, cancel);

        if (resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadFromJsonAsync<AuthenticateResponse>(LauncherJson.Options, cancel)
                       ?? throw new InvalidDataException("empty authenticate response");
            return AuthResult.Success(new AuthLogin(body.UserId, body.Username, body.Token, body.ExpireTime));
        }

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            var deny = await resp.Content.ReadFromJsonAsync<AuthenticateDenyResponse>(LauncherJson.Options, cancel);
            return AuthResult.Denied(deny?.Code ?? AuthDenyCode.UnknownError, deny?.Errors ?? ["login refused"]);
        }

        var text = await resp.Content.ReadAsStringAsync(cancel);
        Log.Error("authenticate: unexpected {Status}: {Body}", resp.StatusCode, text);
        return AuthResult.Denied(AuthDenyCode.UnknownError, [$"auth server returned {(int)resp.StatusCode}"]);
    }

    /// <summary>Returns a refreshed token, or null if the server rejected it (expired / revoked).</summary>
    public async Task<AuthToken?> RefreshTokenAsync(string token, CancellationToken cancel = default)
    {
        using var resp = await http.PostAsJsonAsync(
            BaseUrl + "api/auth/refresh", new RefreshRequest(token), LauncherJson.Options, cancel);

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            return null;

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<RefreshResponse>(LauncherJson.Options, cancel)
                   ?? throw new InvalidDataException("empty refresh response");
        return new AuthToken(body.NewToken, body.ExpireTime);
    }

    /// <summary>True if the token is still valid server-side.</summary>
    public async Task<bool> PingAsync(string token, CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "api/auth/ping");
        request.Headers.Authorization = new AuthenticationHeaderValue("SS14Auth", token);
        using var resp = await http.SendAsync(request, cancel);

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            return false;
        resp.EnsureSuccessStatusCode();
        return true;
    }

    public async Task LogoutAsync(string token, CancellationToken cancel = default)
    {
        try
        {
            using var resp = await http.PostAsJsonAsync(
                BaseUrl + "api/auth/logout", new LogoutRequest(token), LauncherJson.Options, cancel);
            // Best effort; the token expires on its own regardless.
        }
        catch (Exception e)
        {
            Log.Warning(e, "logout request failed (token will expire on its own)");
        }
    }

    // --- wire types ---

    private sealed record AuthenticateRequest(
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("userId")] Guid? UserId,
        [property: JsonPropertyName("password")] string Password,
        [property: JsonPropertyName("tfaCode")] string? TfaCode);

    private sealed record AuthenticateResponse(string Token, string Username, Guid UserId, DateTimeOffset ExpireTime);

    private sealed record AuthenticateDenyResponse(string[] Errors, AuthDenyCode Code);

    private sealed record RefreshRequest(string Token);
    private sealed record RefreshResponse(DateTimeOffset ExpireTime, string NewToken);
    private sealed record LogoutRequest(string Token);
}

[JsonConverter(typeof(JsonStringEnumConverter<AuthDenyCode>))]
public enum AuthDenyCode
{
    None = 0,
    InvalidCredentials = 1,
    AccountUnconfirmed = 2,
    TfaRequired = 3,
    TfaInvalid = 4,
    AccountLocked = 5,
    UnknownError = -1,
}

public readonly record struct AuthToken(string Token, DateTimeOffset ExpireTime)
{
    public bool IsTimeExpired => DateTimeOffset.UtcNow >= ExpireTime;

    /// <summary>Refresh once we're within 15 days of expiry (tokens last ~30d).</summary>
    public bool ShouldRefresh => DateTimeOffset.UtcNow >= ExpireTime - TimeSpan.FromDays(15);
}

public sealed record AuthLogin(Guid UserId, string Username, string Token, DateTimeOffset ExpireTime)
{
    public AuthToken AsToken() => new(Token, ExpireTime);
}

public sealed class AuthResult
{
    public bool IsSuccess => Login is not null;
    public AuthLogin? Login { get; private init; }
    public AuthDenyCode DenyCode { get; private init; }
    public IReadOnlyList<string> Errors { get; private init; } = [];

    public static AuthResult Success(AuthLogin login) => new() { Login = login };
    public static AuthResult Denied(AuthDenyCode code, IReadOnlyList<string> errors) =>
        new() { DenyCode = code, Errors = errors };
}
