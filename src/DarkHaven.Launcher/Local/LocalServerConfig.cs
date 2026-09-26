using System.Text;

namespace DarkHaven.Launcher.Local;

/// <summary>
/// The <c>server_config.toml</c> for a local server, rewritten on every start from the profile — so
/// what ЛОКАЛКА shows is what the server runs with.
/// </summary>
public static class LocalServerConfig
{
    /// <summary>The game's developer preset, shipped inside every server build.</summary>
    public const string DevelopPreset = "Build/development";

    public static string Toml(LocalServerProfile p, string logsDir)
    {
        var s = new StringBuilder();
        s.AppendLine("# Written by the Frontier 15 launcher (ЛОКАЛКА) on every start — change the server there, not here.");

        // Only this PC: no one else can reach it, and it never shows up on the public hub.
        Section(s, "net");
        Value(s, "port", p.Port);
        Value(s, "bindto", "127.0.0.1");
        Section(s, "status");
        Value(s, "bind", $"127.0.0.1:{p.Port}");
        Section(s, "hub");
        Value(s, "advertise", false);
        // Optional, not the engine's Required: a server only this PC reaches still works offline or
        // with the SS14 auth server down; a signed-in player is verified all the same.
        Section(s, "auth");
        Value(s, "mode", 0);

        Section(s, "game");
        Value(s, "hostname", $"Локалка: {p.Name}");
        Value(s, "soft_max_players", p.MaxPlayers);
        // New accounts on a home server shouldn't be locked out of jobs by playtime.
        Value(s, "role_timers", false);
        if (!string.IsNullOrWhiteSpace(p.Map))
            Value(s, "map", p.Map.Trim());
        Section(s, "game.panic_bunker");
        Value(s, "enabled", false);

        // Whoever connects from this PC is the host: full admin, console included.
        Section(s, "console");
        Value(s, "loginlocal", true);

        if (p.Mode == LocalServerMode.Develop)
        {
            Section(s, "config");
            Value(s, "presets", DevelopPreset);
        }

        Section(s, "database");
        Value(s, "engine", "sqlite");
        Value(s, "sqlite_dbpath", "preferences.db");

        // The build's own example config turns file logs off; ours keeps them, for "why did it crash".
        Section(s, "log");
        Value(s, "enabled", true);
        Value(s, "path", logsDir);
        return s.ToString();
    }

    /// <summary>The profile's own cvars, for <c>--cvar name=value</c>; odd names are dropped, not passed through.</summary>
    public static IEnumerable<string> ExtraCvars(LocalServerProfile p) =>
        p.Cvars
            .Where(kv => IsCvarName(kv.Key) && !kv.Value.Contains('\n') && !kv.Value.Contains('\r'))
            .Select(kv => $"{kv.Key.Trim()}={kv.Value.Trim()}");

    internal static bool IsCvarName(string name) =>
        name.Trim() is { Length: > 0 and <= 100 } n && n.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_');

    private static void Section(StringBuilder s, string name) => s.AppendLine().Append('[').Append(name).AppendLine("]");

    private static void Value(StringBuilder s, string key, int value) => s.Append(key).Append(" = ").Append(value).AppendLine();

    private static void Value(StringBuilder s, string key, bool value) => s.Append(key).Append(" = ").AppendLine(value ? "true" : "false");

    private static void Value(StringBuilder s, string key, string value) => s.Append(key).Append(" = ").AppendLine(Quote(value));

    /// <summary>A TOML basic string: quotes, backslashes and control characters escaped.</summary>
    internal static string Quote(string value)
    {
        var s = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': s.Append("\\\""); break;
                case '\\': s.Append("\\\\"); break;
                case '\n': s.Append("\\n"); break;
                case '\r': s.Append("\\r"); break;
                case '\t': s.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                        s.Append($"\\u{(int)c:X4}");
                    else
                        s.Append(c);
                    break;
            }
        }
        return s.Append('"').ToString();
    }
}
