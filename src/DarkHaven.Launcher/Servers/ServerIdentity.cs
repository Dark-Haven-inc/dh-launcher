using DarkHaven.Launcher.Models;

namespace DarkHaven.Launcher.Servers;

/// <summary>
/// Checks that the server answering a region's address is the one we meant to reach.
///
/// An address is not an identity: DH's server shares a machine with a neighbour's server, so when
/// ours is down the other one can end up on the same port — and a player who clicked ХЕЙВЕН lands
/// in a different game entirely. The server's <c>build.fork_id</c> is set in its own config and
/// survives restarts, which makes it the marker to check. (<c>auth.public_key</c> looks tempting
/// and is wrong: the engine makes a new keypair every start.)
/// </summary>
public static class ServerIdentity
{
    /// <summary>
    /// Null when the server is the expected one — or when nothing was pinned, or the server told us
    /// no fork id at all, since a missing value can't prove anything either way. Otherwise the
    /// sentence to show the player.
    /// </summary>
    public static string? Mismatch(string? expectedFork, ServerInfo info, string regionName)
    {
        // Several accepted ids, comma-separated: the one the server reports today and the one it's
        // about to move to, so switching doesn't lock players out of their own region.
        var expected = (expectedFork ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (expected.Length == 0)
            return null;

        var actual = info.Build?.ForkId?.Trim();
        if (string.IsNullOrEmpty(actual) || expected.Contains(actual, StringComparer.OrdinalIgnoreCase))
            return null;

        var who = info.Desc is { Length: > 0 } d ? $" Он представляется так: «{d.Trim()}»." : "";
        return $"По адресу региона {regionName} отвечает другой сервер (сборка «{actual}»).{who} " +
               "Скорее всего, сервер Frontier 15 сейчас выключен, а его адрес занял чужой сервер. " +
               "Заходить туда не стоит — сообщите администрации.";
    }
}

/// <summary>Thrown instead of connecting when <see cref="ServerIdentity"/> says it's a stranger.</summary>
public sealed class WrongServerException(string message) : Exception(message);
