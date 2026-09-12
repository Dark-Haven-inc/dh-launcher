namespace DarkHaven.Launcher.Servers;

public sealed record ServerNetworkGroup(string Label, IReadOnlyList<ServerEntry> Servers);

/// <summary>
/// Clusters РУхаб servers into their likely operator/network instead of leaving them as one
/// undifferentiated pile — the way players already think of them ("Corvax", "МЁРТВЫЙ КОСМОС", "SS220"…).
///
/// Servers that share a hosting domain are almost always run by the same team, so that's the grouping
/// key. The group's display label is the longest common prefix of its members' (cleaned-up) names —
/// e.g. "CorvaxGoob — Люмен" and "Corvax — Вайтлист" both start with "Corvax", so the group becomes
/// "Corvax". When names share nothing (a hosting platform running several unrelated projects), the
/// label falls back to the domain itself. Servers that don't share a domain with anyone else land in
/// one "Другие сервера" bucket instead of each getting a pointless one-member "group". The floor is
/// 4 servers, not 2 or 3 — measured empirically against the real hub: at 3+ there were still ~8
/// domains competing for map space, which is more labeled beacons than a single panel can fit without
/// their name plates overlapping regardless of how they're arranged; at 4+ it's the 4 domains a player
/// would actually recognize by name (station14.ru, deadspace14.net, ss14.org, shizainc.com), each with
/// real room on the map. Smaller domains still show up individually in the plain list — this floor
/// only decides who gets their own permanent map beacon vs. joining "Другие сервера" there.
/// </summary>
public static class NetworkGrouping
{
    private const int MinNetworkSize = 4;

    public static IReadOnlyList<ServerNetworkGroup> Group(IEnumerable<ServerEntry> servers)
    {
        var byKey = new Dictionary<string, List<ServerEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in servers)
        {
            var key = HostKey(s.Address);
            if (!byKey.TryGetValue(key, out var list))
                byKey[key] = list = [];
            list.Add(s);
        }

        var groups = new List<ServerNetworkGroup>();
        var misc = new List<ServerEntry>();
        foreach (var (key, members) in byKey)
        {
            if (members.Count < MinNetworkSize || IsIp(key))
                misc.AddRange(members);
            else
                groups.Add(new ServerNetworkGroup(LabelFor(key, members), members));
        }

        groups = groups
            .OrderByDescending(g => g.Servers.Sum(s => s.Players))
            .ThenByDescending(g => g.Servers.Count)
            .ToList();

        if (misc.Count > 0)
            groups.Add(new ServerNetworkGroup("Другие сервера", misc.OrderByDescending(s => s.Players).ToList()));

        return groups;
    }

    /// <summary>The domain (last two labels) a server's address resolves to, or the bare IP if it's
    /// addressed by one directly — IPs don't reliably imply a shared operator, so they're never merged.</summary>
    public static string HostKey(string address)
    {
        var host = HostOf(address);
        if (IsIp(host)) return host;

        var parts = host.Split('.');
        return parts.Length < 2 ? host : string.Join('.', parts[^2..]);
    }

    private static string HostOf(string address)
    {
        var s = address;
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        var slash = s.IndexOfAny(['/', '\\']);
        if (slash >= 0) s = s[..slash];
        var colon = s.IndexOf(':');
        if (colon >= 0) s = s[..colon];
        return s;
    }

    private static bool IsIp(string host) => System.Net.IPAddress.TryParse(host, out _);

    private static string LabelFor(string domainKey, IReadOnlyList<ServerEntry> members)
    {
        var cleaned = members.Select(m => CleanName(m.DisplayName)).Where(s => s.Length > 0).ToList();
        if (cleaned.Count > 0)
        {
            var prefix = cleaned[0];
            foreach (var c in cleaned.Skip(1))
            {
                var max = Math.Min(prefix.Length, c.Length);
                var i = 0;
                while (i < max && char.ToUpperInvariant(prefix[i]) == char.ToUpperInvariant(c[i])) i++;
                prefix = prefix[..i];
            }
            prefix = prefix.TrimEnd();
            if (prefix.Length >= 3)
                return prefix;
        }
        return domainKey;
    }

    /// <summary>Strips leading emoji/decoration and cuts at the first strong separator, so
    /// "🛰️ CorvaxGoob — 🌕 Люмен 🪐 [MRP+]" becomes "CorvaxGoob" for prefix comparison.</summary>
    private static string CleanName(string raw)
    {
        var i = 0;
        while (i < raw.Length && !char.IsLetterOrDigit(raw[i])) i++;
        var s = raw[i..];

        var cut = s.IndexOfAny(['—', '–', '|', ':', '[']);
        var dash = s.IndexOf(" - ", StringComparison.Ordinal);
        if (dash >= 0 && (cut < 0 || dash < cut)) cut = dash;

        if (cut > 0) s = s[..cut];
        return s.Trim().TrimEnd('!', '.', ',');
    }
}
