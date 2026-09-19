using System.Net.Http.Headers;
using System.Net.Http.Json;
using Serilog;

namespace DarkHaven.Launcher.Api;

/// <summary>
/// Client for <c>DarkHaven.Platform.Api</c> — profile, news, notifications, admin. Everything here
/// degrades gracefully: with no <c>PlatformApiUrl</c> configured, or the service unreachable, every
/// call just returns null/empty and the caller falls back to local-only behaviour (see
/// <c>dh-platform</c>'s docs/BACKEND.md §7 for the design).
/// </summary>
public sealed class PlatformApi(HttpClient http, string? baseUrl)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(baseUrl);
    public bool IsSignedIn => _jwt is not null;

    /// <summary>
    /// When the current platform JWT was issued. It lives an hour (the platform's JwtHelper.Lifetime),
    /// so a launcher left open longer has to sign in again — see AppServices' heartbeat.
    /// </summary>
    public DateTimeOffset? SignedInAt { get; private set; }
    public IReadOnlyList<string> Roles { get; private set; } = [];
    public bool CanAdmin => Roles.Contains("admin") || Roles.Contains("owner");
    public bool CanModerate => CanAdmin || Roles.Contains("moderator");

    private string? _jwt;

    /// <summary>
    /// Trades a Wizard's Den token (the same raw token the launcher already holds for the game
    /// server) for the platform's own short-lived JWT. <paramref name="userId"/>/<paramref name="username"/>
    /// are the identity the launcher already trusts from its own earlier Wizard's Den login —
    /// Wizard's Den's own token-liveness check (<c>/api/auth/ping</c>) only ever confirms a
    /// *username*, never a UserId, so the platform can't re-derive the UserId from the bare token
    /// alone. It closes the loop itself by pinging and checking the username matches.
    /// </summary>
    public async Task<bool> SignInAsync(Guid userId, string username, string wizardsToken, CancellationToken cancel = default)
    {
        _jwt = null;
        Roles = [];
        if (!IsConfigured) return false;

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Url("/api/session"))
            {
                Headers = { Authorization = new AuthenticationHeaderValue("SS14Auth", wizardsToken) },
                Content = JsonContent.Create(new { userId, username }, options: LauncherJson.Options),
            };
            var res = await http.SendAsync(req, cancel);
            if (!res.IsSuccessStatusCode)
            {
                Log.Debug("Platform sign-in rejected: {Code}", res.StatusCode);
                return false;
            }

            var body = await res.Content.ReadFromJsonAsync<SessionResponse>(LauncherJson.Options, cancel);
            _jwt = body?.Token;
            Roles = body?.Roles ?? [];
            SignedInAt = _jwt is null ? null : DateTimeOffset.UtcNow;
            return _jwt is not null;
        }
        catch (Exception e)
        {
            Log.Debug(e, "Platform sign-in failed (service unreachable?)");
            return false;
        }
    }

    public void SignOut()
    {
        _jwt = null;
        Roles = [];
        SignedInAt = null;
    }

    // --- Friends & presence ---

    public Task<PlatformFriends?> GetFriendsAsync(CancellationToken cancel = default) =>
        GetAuthed<PlatformFriends>("/api/friends", cancel);

    /// <summary>
    /// Sends a friend request by SS14 username. Returns the platform's own message on failure
    /// ("no such player", "already friends", …) so the UI can show it as-is.
    /// </summary>
    public async Task<(bool Ok, string Message)> SendFriendRequestAsync(string username, CancellationToken cancel = default)
    {
        if (_jwt is null) return (false, "Нет связи с платформой Frontier 15.");
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Url("/api/friends/request"))
            {
                Content = JsonContent.Create(new { username }, options: LauncherJson.Options),
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _jwt) },
            };
            var res = await http.SendAsync(req, cancel);

            FriendRequestResult? body = null;
            try { body = await res.Content.ReadFromJsonAsync<FriendRequestResult>(LauncherJson.Options, cancel); }
            catch (Exception) { /* not every error response has a JSON body */ }

            if (res.IsSuccessStatusCode)
                return (true, body?.Status == "accepted" ? "Вы теперь друзья." : "Заявка отправлена.");
            return (false, body?.Error ?? $"Не получилось (код {(int)res.StatusCode}).");
        }
        catch (Exception e)
        {
            Log.Debug(e, "Friend request failed");
            return (false, "Нет связи с платформой Frontier 15.");
        }
    }

    public async Task<bool> AcceptFriendAsync(int friendshipId, CancellationToken cancel = default) =>
        await PostAuthed($"/api/friends/{friendshipId}/accept", new { }, cancel);

    /// <summary>Unfriend, decline an incoming request, or cancel an outgoing one.</summary>
    public async Task<bool> RemoveFriendAsync(int friendshipId, CancellationToken cancel = default) =>
        await DeleteAuthed($"/api/friends/{friendshipId}", cancel);

    /// <summary>Heartbeat: running, and playing on <paramref name="serverAddress"/> if not null.</summary>
    public async Task<bool> UpdatePresenceAsync(string? serverAddress, string? serverName, CancellationToken cancel = default) =>
        await PutAuthed("/api/presence", new { serverAddress, serverName }, cancel);

    public async Task<bool> ClearPresenceAsync(CancellationToken cancel = default) =>
        await DeleteAuthed("/api/presence", cancel);

    /// <summary>The player's own characters and their Frontier bank balance, from the game DB.</summary>
    public Task<PlatformCharacters?> GetCharactersAsync(CancellationToken cancel = default) =>
        GetAuthed<PlatformCharacters>("/api/profile/me/characters", cancel);

    public Task<PlatformProfile?> GetProfileAsync(CancellationToken cancel = default) =>
        GetAuthed<PlatformProfile>("/api/profile/me", cancel);

    /// <summary>Unread notifications only (bans, warnings, admin announcements), newest first.</summary>
    public Task<IReadOnlyList<PlatformNotification>> GetNotificationsAsync(CancellationToken cancel = default) =>
        GetAuthedList<PlatformNotification>("/api/notifications", cancel);

    public async Task<bool> MarkNotificationReadAsync(int id, CancellationToken cancel = default) =>
        await PostAuthed($"/api/notifications/{id}/read", new { }, cancel);

    /// <summary>
    /// The text half of the Discord-style look. Null leaves a field alone, "" clears it. The platform
    /// validates (frame names, #RRGGBB, 40/190 chars) and says why in <c>Error</c> when it refuses.
    /// </summary>
    public async Task<(bool Ok, string? Error)> UpdateLookAsync(
        string? frame, string? title, string? accentColor, string? aboutMe, CancellationToken cancel = default)
    {
        if (_jwt is null) return (false, "Нет связи с платформой Frontier 15.");
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Put, Url("/api/profile/me"))
            {
                Content = JsonContent.Create(new { frame, title, accentColor, aboutMe }, options: LauncherJson.Options),
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _jwt) },
            };
            var res = await http.SendAsync(req, cancel);
            return res.IsSuccessStatusCode ? (true, null) : (false, await ErrorOf(res, cancel));
        }
        catch (Exception e)
        {
            Log.Debug(e, "Platform profile update failed");
            return (false, "Нет связи с платформой Frontier 15.");
        }
    }

    /// <summary>
    /// Uploads an avatar or banner (<paramref name="slot"/> is "avatar" or "banner"). The platform
    /// crops and re-encodes it; the returned URL is relative to the platform, see <see cref="MediaUri"/>.
    /// </summary>
    public async Task<(string? Url, string? Error)> UploadPictureAsync(string slot, byte[] image, CancellationToken cancel = default)
    {
        if (_jwt is null) return (null, "Нет связи с платформой Frontier 15.");
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Put, Url($"/api/profile/me/{slot}"))
            {
                Content = new ByteArrayContent(image),
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _jwt) },
            };
            var res = await http.SendAsync(req, cancel);
            if (!res.IsSuccessStatusCode)
                return (null, res.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? "Слишком много загрузок подряд — попробуйте через час."
                    : await ErrorOf(res, cancel));
            var body = await res.Content.ReadFromJsonAsync<UploadResult>(LauncherJson.Options, cancel);
            return (body?.Url, null);
        }
        catch (Exception e)
        {
            Log.Debug(e, "Platform picture upload failed");
            return (null, "Нет связи с платформой Frontier 15.");
        }
    }

    public async Task<bool> DeletePictureAsync(string slot, CancellationToken cancel = default) =>
        await DeleteAuthed($"/api/profile/me/{slot}", cancel);

    /// <summary>Anyone's profile card: look, title, member-since. Nothing private.</summary>
    public Task<PlatformPublicProfile?> GetPublicProfileAsync(Guid userId, CancellationToken cancel = default) =>
        GetAuthed<PlatformPublicProfile>($"/api/profile/{userId}", cancel);

    /// <summary>
    /// Absolute address of a platform media path (<c>/api/media/…</c>). Only ever the platform's own
    /// host: anything that isn't a bare path from it is refused, so a profile can't make the launcher
    /// fetch from somewhere else.
    /// </summary>
    public Uri? MediaUri(string? path) =>
        IsConfigured && path is { Length: > 1 } && path.StartsWith("/api/media/", StringComparison.Ordinal)
            ? new Uri(Url(path))
            : null;

    /// <summary>Public, cacheable bytes of an avatar or banner; null if unavailable.</summary>
    public async Task<byte[]?> GetMediaAsync(string? path, CancellationToken cancel = default)
    {
        if (MediaUri(path) is not { } uri) return null;
        try
        {
            using var res = await http.GetAsync(uri, cancel);
            return res.IsSuccessStatusCode ? await res.Content.ReadAsByteArrayAsync(cancel) : null;
        }
        catch (Exception e)
        {
            Log.Debug(e, "Platform media {Path} failed", path);
            return null;
        }
    }

    private static async Task<string> ErrorOf(HttpResponseMessage res, CancellationToken cancel)
    {
        try
        {
            if (await res.Content.ReadFromJsonAsync<FriendRequestResult>(LauncherJson.Options, cancel) is { Error: { } e })
                return e;
        }
        catch (Exception) { /* not every error has a JSON body */ }
        return $"Платформа отказала (код {(int)res.StatusCode}).";
    }

    // --- Admin (only meaningful when CanAdmin) ---

    public Task<IReadOnlyList<PlatformNewsAdmin>> GetAllNewsAsync(CancellationToken cancel = default) =>
        GetAuthedList<PlatformNewsAdmin>("/api/admin/news", cancel);

    public Task<IReadOnlyList<PlatformBan>> GetLauncherBansAsync(CancellationToken cancel = default) =>
        GetAuthedList<PlatformBan>("/api/admin/launcher-bans", cancel);

    /// <summary>Full standing on a player, by typed username — roles, ban status, playtime — so an
    /// admin can look before acting. Only finds players who've signed in to the platform at least
    /// once (i.e. opened ПРОФИЛЬ with an account).</summary>
    public Task<PlatformPlayerInfo?> GetPlayerAsync(string username, CancellationToken cancel = default) =>
        GetAuthed<PlatformPlayerInfo>($"/api/admin/player?username={Uri.EscapeDataString(username)}", cancel);

    public async Task<bool> IssueLauncherBanAsync(Guid userId, string reason, DateTimeOffset? expiresAt, CancellationToken cancel = default) =>
        await PostAuthed("/api/admin/launcher-ban", new { userId, reason, expiresAt }, cancel);

    public async Task<bool> RevokeLauncherBanAsync(int banId, CancellationToken cancel = default) =>
        await DeleteAuthed($"/api/admin/launcher-ban/{banId}", cancel);

    public async Task<bool> PostNewsAsync(string title, string bodyMarkdown, string? imageUrl, bool draft, CancellationToken cancel = default) =>
        await PostAuthed("/api/admin/news", new { title, bodyMarkdown, imageUrl, draft }, cancel);

    public async Task<bool> EditNewsAsync(int id, string title, string bodyMarkdown, string? imageUrl, bool draft, CancellationToken cancel = default) =>
        await PutAuthed($"/api/admin/news/{id}", new { title, bodyMarkdown, imageUrl, draft }, cancel);

    public async Task<bool> DeleteNewsAsync(int id, CancellationToken cancel = default) =>
        await DeleteAuthed($"/api/admin/news/{id}", cancel);

    public async Task<bool> SendWarningAsync(Guid userId, string text, CancellationToken cancel = default) =>
        await PostAuthed("/api/admin/warning", new { userId, text }, cancel);

    /// <summary>Moderation: take down a player's avatar, banner or "о себе" ("avatar"/"banner"/"about").</summary>
    public async Task<bool> ClearLookAsync(Guid userId, string part, CancellationToken cancel = default) =>
        await DeleteAuthed($"/api/admin/player/{userId}/look/{part}", cancel);

    // --- Roles (owner-only to grant/revoke — enforced server-side too) ---

    public Task<IReadOnlyList<PlatformRole>> GetRolesAsync(CancellationToken cancel = default) =>
        GetAuthedList<PlatformRole>("/api/admin/roles", cancel);

    public async Task<bool> SetRoleAsync(Guid userId, string role, CancellationToken cancel = default) =>
        await PostAuthed("/api/admin/roles", new { userId, role }, cancel);

    public async Task<bool> RemoveRoleAsync(Guid userId, CancellationToken cancel = default) =>
        await DeleteAuthed($"/api/admin/roles/{userId}", cancel);

    // --- Audit log (admin/owner only, enforced server-side) ---

    public Task<IReadOnlyList<PlatformAuditEntry>> GetAuditLogAsync(CancellationToken cancel = default) =>
        GetAuthedList<PlatformAuditEntry>("/api/admin/audit", cancel);

    // --- Announcements (admin/owner only) ---

    public async Task<bool> PostAnnouncementAsync(string text, CancellationToken cancel = default) =>
        await PostAuthed("/api/admin/announcement", new { text }, cancel);

    // --- Live game-server bridge (moderator+): chat + ahelp reply, per region ---

    public Task<IReadOnlyList<string>> GetGameServersAsync(CancellationToken cancel = default) =>
        GetAuthedList<string>("/api/admin/game-servers", cancel);

    public async Task<bool> SendGameChatAsync(string region, string channel, string text, CancellationToken cancel = default) =>
        await PostAuthed("/api/admin/game-chat", new { region, channel, text }, cancel);

    /// <summary>Recent messages on a channel, so the chat panel shows what's actually being said —
    /// not just what was sent blind. Pass <paramref name="afterUtc"/> (from the last call's newest
    /// entry) to poll incrementally instead of re-fetching everything each time.</summary>
    public Task<IReadOnlyList<PlatformChatMessage>> GetRecentGameChatAsync(
        string region, string channel, DateTimeOffset? afterUtc = null, CancellationToken cancel = default)
    {
        var path = $"/api/admin/game-chat/recent?region={Uri.EscapeDataString(region)}&channel={Uri.EscapeDataString(channel)}";
        if (afterUtc is { } after)
            path += $"&afterUtc={Uri.EscapeDataString(after.UtcDateTime.ToString("o"))}";
        return GetAuthedList<PlatformChatMessage>(path, cancel);
    }

    public async Task<bool> ReplyAhelpAsync(string region, Guid userId, string text, bool adminOnly, CancellationToken cancel = default) =>
        await PostAuthed($"/api/admin/ahelp/{userId}/reply", new { region, text, adminOnly }, cancel);

    // --- Discord account linking ---

    /// <summary>Requests a short-lived code to give the "Дозорный" bot via <c>!link &lt;код&gt;</c> in
    /// Discord. Null if not signed in or the platform is unreachable.</summary>
    public async Task<(string Code, int ExpiresInSeconds)?> StartDiscordLinkAsync(CancellationToken cancel = default)
    {
        if (_jwt is null) return null;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Url("/api/discord/link/start"))
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _jwt) },
            };
            var res = await http.SendAsync(req, cancel);
            if (!res.IsSuccessStatusCode) return null;
            var body = await res.Content.ReadFromJsonAsync<DiscordLinkStartResponse>(LauncherJson.Options, cancel);
            return body is null ? null : (body.Code, body.ExpiresInSeconds);
        }
        catch (Exception e)
        {
            Log.Debug(e, "Discord link-start failed");
            return null;
        }
    }

    private async Task<bool> PostAuthed(string path, object body, CancellationToken cancel)
    {
        if (_jwt is null) return false;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Url(path))
            {
                Content = JsonContent.Create(body, options: LauncherJson.Options),
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _jwt) },
            };
            var res = await http.SendAsync(req, cancel);
            return res.IsSuccessStatusCode;
        }
        catch (Exception e)
        {
            Log.Debug(e, "Platform admin POST {Path} failed", path);
            return false;
        }
    }

    private async Task<bool> PutAuthed(string path, object body, CancellationToken cancel)
    {
        if (_jwt is null) return false;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Put, Url(path))
            {
                Content = JsonContent.Create(body, options: LauncherJson.Options),
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _jwt) },
            };
            var res = await http.SendAsync(req, cancel);
            return res.IsSuccessStatusCode;
        }
        catch (Exception e)
        {
            Log.Debug(e, "Platform admin PUT {Path} failed", path);
            return false;
        }
    }

    private async Task<bool> DeleteAuthed(string path, CancellationToken cancel)
    {
        if (_jwt is null) return false;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Delete, Url(path))
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _jwt) },
            };
            var res = await http.SendAsync(req, cancel);
            return res.IsSuccessStatusCode;
        }
        catch (Exception e)
        {
            Log.Debug(e, "Platform admin DELETE {Path} failed", path);
            return false;
        }
    }

    private async Task<T?> GetAuthed<T>(string path, CancellationToken cancel)
    {
        if (!IsConfigured || _jwt is null) return default;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, Url(path))
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _jwt) },
            };
            var res = await http.SendAsync(req, cancel);
            return res.IsSuccessStatusCode
                ? await res.Content.ReadFromJsonAsync<T>(LauncherJson.Options, cancel)
                : default;
        }
        catch (Exception e)
        {
            Log.Debug(e, "Platform GET {Path} failed", path);
            return default;
        }
    }

    private async Task<IReadOnlyList<T>> GetAuthedList<T>(string path, CancellationToken cancel) =>
        await GetAuthed<T[]>(path, cancel) ?? [];

    private string Url(string path) => baseUrl!.TrimEnd('/') + path;

    private sealed record SessionResponse(string Token, int ExpiresIn, Guid UserId, string Username, string[]? Roles);
    private sealed record DiscordLinkStartResponse(string Code, int ExpiresInSeconds);
}

public sealed record PlatformProfile(
    Guid UserId, string Username, string? AvatarUrl, string Frame, string? Title,
    string? DiscordId, string? DiscordAvatar, DateTimeOffset MemberSince,
    long TotalPlaytimeSeconds, string PlaytimeSource,
    bool LauncherBanned, string? LauncherBanReason, DateTimeOffset? LauncherBanExpires,
    string? BannerUrl = null, string? AccentColor = null, string? AboutMe = null);

/// <summary>Someone's card as <c>GET /api/profile/{id}</c> returns it.</summary>
public sealed record PlatformPublicProfile(
    Guid UserId, string Username, string? AvatarUrl, string? BannerUrl, string? AccentColor, string? AboutMe,
    string Frame, string? Title, DateTimeOffset MemberSince);

internal sealed record UploadResult(string? Url);

public sealed record PlatformFriend(
    int Id, Guid UserId, string Username, string Frame, bool Online, string? ServerName, string? ServerAddress,
    string? AvatarUrl = null);

public sealed record PlatformFriends(
    PlatformFriend[] Friends, PlatformFriend[] IncomingRequests, PlatformFriend[] OutgoingRequests);

internal sealed record FriendRequestResult(string? Status, string? Error);

public sealed record PlatformCharacter(int Slot, string Name, int BankBalance, bool Selected);

/// <summary><c>Source</c> is "game-server", or "unavailable" when the platform has no game DB access.</summary>
public sealed record PlatformCharacters(string Source, PlatformCharacter[] Characters);

public sealed record PlatformNotification(int Id, Guid UserId, string Kind, string Text, DateTimeOffset CreatedAt, bool Read);

public sealed record PlatformNewsAdmin(int Id, string Title, string BodyMarkdown, string? ImageUrl, Guid AuthorId, DateTimeOffset PublishedAt, bool Draft);

public sealed record PlatformBan(
    int Id, Guid UserId, string Username, string Reason,
    DateTimeOffset IssuedAt, string IssuedByUsername, DateTimeOffset? ExpiresAt, bool Active);

public sealed record PlatformPlayerInfo(
    Guid UserId, string Username, DateTimeOffset MemberSince, DateTimeOffset LastSeen, string? Role,
    long TotalPlaytimeSeconds, bool LauncherBanned, string? LauncherBanReason, DateTimeOffset? LauncherBanExpires);

public sealed record PlatformRole(Guid UserId, string Username, string Role);

public sealed record PlatformChatMessage(string Sender, string Text, DateTimeOffset AtUtc);

public sealed record PlatformAuditEntry(
    int Id, Guid ActorUserId, string ActorUsername, string Action,
    Guid? TargetUserId, string? TargetUsername, string Details, DateTimeOffset At);
