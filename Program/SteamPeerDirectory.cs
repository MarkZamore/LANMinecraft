namespace Minecraft;

/// <summary>
/// Who else is playing right now: Steam friends whose launcher publishes the
/// rich-presence keys this one understands. It replaces UDP discovery and the
/// route table - there are no addresses to remember, only accounts.
///
/// A peer stays listed for a short grace period after its keys disappear,
/// because e4steam clears rich presence when it stops sharing a world and Steam
/// itself can drop keys between refreshes.
/// </summary>
public sealed class SteamPeerDirectory(
    SteamClientService client,
    Logger? logger = null)
{
    private readonly ISteamApiFacade api = client.Api;

    /// <summary>How long a peer survives after its keys stop being readable.</summary>
    internal static readonly TimeSpan PresenceGrace = TimeSpan.FromSeconds(60);

    private readonly Dictionary<ulong, (SteamPeerPresence Presence, DateTimeOffset LastSeen)> _peers = [];
    private readonly object _gate = new();

    public event EventHandler<IReadOnlyList<SteamPeerPresence>>? PeersChanged;

    /// <summary>Everyone currently considered online, newest state first seen order.</summary>
    public IReadOnlyList<SteamPeerPresence> Peers
    {
        get
        {
            lock (_gate) return _peers.Values.Select(entry => entry.Presence).ToArray();
        }
    }

    /// <summary>Publishes this launcher's own presence keys.</summary>
    public void PublishLocalPresence(SteamPeerPresence presence)
    {
        if (!client.Status.IsReady) return;
        foreach (var (key, value) in SteamPresenceCodec.Encode(presence))
        {
            client.SetPresence(key, value);
        }
    }

    /// <summary>
    /// Re-reads the friend list. Cheap enough for the UI timer: Steam serves
    /// the cached presence of friends the client already knows about.
    /// </summary>
    public void Refresh()
    {
        if (!client.Status.IsReady) return;

        var now = DateTimeOffset.UtcNow;
        var changed = false;
        var local = client.Status.SteamId64;

        foreach (var friend in client.Friends)
        {
            if (friend.SteamId64 == local) continue;
            if (!SteamId64.TryFrom(friend.SteamId64, out var peerId)) continue;

            // Whether Steam calls a friend "in Spacewar" is not the question -
            // that flag depends on how their launcher was started and on their
            // privacy settings, and skipping on it made friends who were
            // plainly running the launcher invisible. Publishing our keys is
            // the proof, so every friend is asked and the keys decide.
            api.RequestFriendRichPresence(friend.SteamId64);
            var presence = SteamPresenceCodec.TryDecode(
                peerId,
                friend.PersonaName,
                key => api.GetFriendRichPresence(friend.SteamId64, key));
            if (presence is null) continue;

            lock (_gate)
            {
                changed |= !_peers.TryGetValue(friend.SteamId64, out var previous) ||
                           previous.Presence != presence;
                _peers[friend.SteamId64] = (presence, now);
            }
        }

        lock (_gate)
        {
            foreach (var stale in _peers
                         .Where(entry => now - entry.Value.LastSeen > PresenceGrace)
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                _peers.Remove(stale);
                changed = true;
                logger?.Info($"Peer {stale} is no longer publishing a launcher presence.");
            }
        }

        if (changed) PeersChanged?.Invoke(this, Peers);
    }

    public bool TryGetPeer(SteamId64 peer, out SteamPeerPresence presence)
    {
        lock (_gate)
        {
            if (_peers.TryGetValue(peer.Value, out var entry))
            {
                presence = entry.Presence;
                return true;
            }
        }
        presence = null!;
        return false;
    }

    /// <summary>
    /// The Minecraft UUID a peer claims. It is self-reported, so it is only
    /// ever used to name their data (skins, waypoints), never to authorise
    /// anything - that is the authenticated SteamID's job.
    /// </summary>
    public string ClaimedUuid(SteamId64 peer) =>
        TryGetPeer(peer, out var presence) ? presence.MinecraftUuid : string.Empty;
}
