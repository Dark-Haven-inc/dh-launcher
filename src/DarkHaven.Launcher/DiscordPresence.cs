using DiscordRPC;
using Serilog;

namespace DarkHaven.Launcher;

/// <summary>
/// Publishes the launcher's state to Discord ("В игре — ХЕЙВЕН · 47 игроков"). Entirely optional:
/// a no-op unless a Discord application id is configured (<c>DiscordAppId</c> in settings). Get an
/// id at <see href="https://discord.com/developers/applications"/> and upload a Rich-Presence art
/// asset keyed <c>logo</c> for the icon.
/// </summary>
public sealed class DiscordPresence : IDisposable
{
    private readonly DiscordRpcClient? _client;
    private readonly DateTime _launchedAt = DateTime.UtcNow;

    public DiscordPresence(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
            return;

        try
        {
            _client = new DiscordRpcClient(appId.Trim());
            _client.Initialize();
            Log.Debug("Discord Rich Presence active ({App})", appId);
            SetIdle();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Discord Rich Presence init failed — disabled this session");
            _client = null;
        }
    }

    public void SetIdle() => Set("В лаунчере", "Выбирает сервер", _launchedAt);

    public void SetConnecting(string name) => Set("Подключается", Trim(name), _launchedAt);

    public void SetInGame(string name, bool isRegion) =>
        Set(isRegion ? "В секторе Frontier 15" : "Играет на сервере SS14", Trim(name), DateTime.UtcNow);

    private void Set(string details, string state, DateTime start)
    {
        if (_client is not { IsInitialized: true })
            return;

        try
        {
            _client.SetPresence(new RichPresence
            {
                Details = details,
                State = state,
                Timestamps = new Timestamps { Start = start },
                Assets = new Assets { LargeImageKey = "logo", LargeImageText = "Frontier 15" },
            });
        }
        catch (Exception e)
        {
            Log.Debug(e, "Discord presence update failed");
        }
    }

    /// <summary>Discord clamps the state string to 128 chars.</summary>
    private static string Trim(string s) => s.Length <= 128 ? s : s[..125] + "…";

    public void Dispose()
    {
        try
        {
            _client?.ClearPresence();
            _client?.Dispose();
        }
        catch
        {
            // shutting down anyway
        }
    }
}
