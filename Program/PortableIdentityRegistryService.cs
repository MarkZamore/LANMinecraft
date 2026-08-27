using System.Collections.Concurrent;
using System.IO;

namespace Minecraft;

/// <summary>
/// Tells the running game which player each in-game name belongs to.
/// </summary>
/// <remarks>
/// Offline Minecraft has one answer to "who is this": the name. It derives the
/// UUID from the name alone, and a world keeps a player's inventory in a file
/// called after that UUID. So changing machines used to cost a player their
/// things unless the name came along with them, and two people who chose the
/// same name were one person to every world they visited.
///
/// The launcher knows better. Every peer announces its Steam account and the
/// UUID derived from it, so the launcher holds the mapping the game lacks. This
/// writes that mapping where the game can read it, and the adapter uses it at
/// the one moment every Minecraft version shares: building the profile.
///
/// A name two accounts both answer to is left out rather than guessed at, so
/// both of them keep the UUID the name gives them and neither is handed the
/// other's inventory.
///
/// Each line also carries the UUID a Steam tunnel would give that account
/// (<see cref="E4steamIdentity"/>). e4steam stamps its own identity onto every
/// profile it admits, after the launcher's login hook has handed over the
/// portable one, so the adapter needs to recognise the stamp to undo it.
/// </remarks>
public sealed class PortableIdentityRegistryService(AppPaths paths, Logger logger)
{
    // Keyed by the account, not by the name. One Steam account has one UUID,
    // but two accounts may well answer to one name - and keying by the name
    // would let the second of them quietly overwrite the first, which is how
    // one player ends up opening the other's inventory.
    private readonly ConcurrentDictionary<string, Player> _players = new(StringComparer.Ordinal);

    /// <summary>What one account answers to, and what a Steam tunnel calls it.</summary>
    private sealed record Player(string Name, string TunnelUuid);

    /// <summary>Records what a peer calls itself and whose progress that is.</summary>
    public void ObservePeer(PeerViewModel peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        Remember((peer.PlayerName ?? "").Trim(), Normalize(peer.MinecraftUuid), peer.SteamId);
    }

    private void Remember(string name, string uuid, SteamId64 steamId)
    {
        if (name.Length == 0 || uuid.Length == 0) return;
        var player = new Player(
            name,
            steamId.IsValid ? E4steamIdentity.ProfileUuid(steamId).ToString("D") : "");
        if (_players.TryGetValue(uuid, out var known) && known == player) return;
        _players[uuid] = player;
        Write();
    }

    /// <summary>
    /// Adds the player at this machine and hands back the file to point the
    /// game at.
    /// </summary>
    public string Prepare(LocalIdentityContext identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Remember((identity.IdentityName ?? "").Trim(), Normalize(identity.MinecraftUuid), identity.SteamId64);
        Write();
        return paths.IdentityRegistryFile;
    }

    private static string Normalize(string? uuid) =>
        Guid.TryParse(uuid, out var parsed) ? parsed.ToString("D") : "";

    // The third field is left off rather than left empty when Steam has no
    // answer, so a line the adapter has always understood still looks the way
    // it always did.
    private static string Compose(string name, string uuid, string tunnelUuid) =>
        tunnelUuid.Length == 0
            ? string.Join('|', name, uuid)
            : string.Join('|', name, uuid, tunnelUuid);

    private void Write()
    {
        try
        {
            // A name two accounts both answer to is left out rather than
            // guessed at: both then keep the UUID the name gives them, which is
            // what they had before any of this, and neither is handed the
            // other's things.
            var contents = string.Join(
                Environment.NewLine,
                _players
                    .GroupBy(pair => pair.Value.Name, StringComparer.Ordinal)
                    .Where(group => group.Select(pair => pair.Key).Distinct(StringComparer.Ordinal).Count() == 1)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => Compose(group.Key, group.First().Key, group.First().Value.TunnelUuid)));
            if (contents.Length == 0)
            {
                if (File.Exists(paths.IdentityRegistryFile)) File.Delete(paths.IdentityRegistryFile);
                return;
            }
            // The game re-reads this when its timestamp moves, so a rewrite that
            // says the same thing would cost every profile a re-parse.
            if (File.Exists(paths.IdentityRegistryFile) &&
                string.Equals(File.ReadAllText(paths.IdentityRegistryFile), contents, StringComparison.Ordinal))
            {
                return;
            }
            AtomicFile.WriteAllText(paths.IdentityRegistryFile, contents);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warn($"Identity registry could not be updated: {ex.Message}");
        }
    }
}
