using DarkHaven.Launcher.Models;

namespace DarkHaven.Launcher.Servers;

public enum ServerSort { Players, Name }

/// <summary>Search / tag / population filters for the server browser.</summary>
public sealed class ServerFilter
{
    public string Search { get; set; } = "";
    public bool HidePassworded { get; set; }
    public bool HideEmpty { get; set; }
    public bool HideFull { get; set; }
    public bool Hide18Plus { get; set; }

    /// <summary>e.g. "en", "ru" — empty means any.</summary>
    public HashSet<string> Languages { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>e.g. "none", "low", "med", "high" — empty means any.</summary>
    public HashSet<string> RolePlay { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ServerSort Sort { get; set; } = ServerSort.Players;

    public IEnumerable<ServerEntry> Apply(IEnumerable<ServerEntry> servers)
    {
        var q = servers.Where(Matches);
        q = Sort switch
        {
            ServerSort.Name => q.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase),
            _ => q.OrderByDescending(s => s.Players).ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase),
        };
        return q;
    }

    public bool Matches(ServerEntry s)
    {
        if (Search.Length > 0
            && !(s.DisplayName.Contains(Search, StringComparison.OrdinalIgnoreCase)
                 || s.Address.Contains(Search, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (HideEmpty && s.Players == 0) return false;
        if (HideFull && s.SoftMaxPlayers > 0 && s.Players >= s.SoftMaxPlayers) return false;
        if (Hide18Plus && s.Tags.Contains("18+")) return false;

        if (Languages.Count > 0 && !TagValues(s, "lang:").Any(Languages.Contains))
            return false;

        if (RolePlay.Count > 0 && !TagValues(s, "rp:").Any(RolePlay.Contains))
            return false;

        return true;
    }

    private static IEnumerable<string> TagValues(ServerEntry s, string prefix)
        => s.Tags.Where(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                 .Select(t => t[prefix.Length..]);
}
