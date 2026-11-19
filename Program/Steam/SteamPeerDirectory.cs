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
    IPeerTransport? transport = null,
    Logger? logger = null,
    // Two of the answers here are about how long something has been true, and
    // a test cannot wait ten seconds to ask one of them.
    Func<DateTimeOffset>? clock = null)
{
    private readonly ISteamApiFacade api = client.Api;
    private readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// How long a peer survives after its keys stop being readable. Generous
    /// because Steam serves a friend's presence on its own schedule and can
    /// simply not have it for a while - especially while both machines are
    /// busy moving a world.
    /// </summary>
    internal static readonly TimeSpan PresenceGrace = TimeSpan.FromMinutes(3);

    private readonly Dictionary<ulong, (SteamPeerPresence Presence, DateTimeOffset LastSeen)> _peers = [];
    private readonly object _gate = new();

    // Two players once sat looking at empty player lists with nothing in either
    // log to say why - this whole path was silent. These remember what was last
    // reported so a two-second timer can say when the answer changes without
    // repeating itself.
    private int _lastLoggedFriendCount = -1;
    private int _lastLoggedPeerCount = -1;
    private bool _publishedPresence;

    /// <summary>
    /// When each friend was last asked for their presence. Valve rate-limits
    /// RequestFriendRichPresence per user and answers a too-frequent caller
    /// from the local cache without ever asking the server - so asking every
    /// friend every two seconds, which is what this did, is the documented way
    /// to guarantee the cache never fills. Reading stays on the fast timer;
    /// reads are local and free. Only the asking is slowed down.
    /// </summary>
    private readonly Dictionary<ulong, DateTimeOffset> _lastRequested = [];

    private static readonly TimeSpan RequestInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When each friend was first asked, and how long to wait after that before
    /// concluding they are in something else.
    /// </summary>
    /// <remarks>
    /// Asking is not answering: RequestFriendRichPresence returns at once and
    /// Steam delivers whenever it likes. Read the keys in the same breath and
    /// they are empty - which looks exactly like a friend who is not running
    /// this launcher at all. Calling that "in another game" would libel every
    /// friend for the first seconds of their session.
    /// </remarks>
    private readonly Dictionary<ulong, DateTimeOffset> _firstAsked = [];

    private static readonly TimeSpan OutsideGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The last build and release each player published, kept after they stop
    /// publishing anything.
    /// </summary>
    /// <remarks>
    /// Somebody who closes the launcher and keeps playing stops answering, but
    /// has not changed which build he is in or which launcher put him there.
    /// Dropping to "in another game" the moment the keys go would take away the
    /// two things the list is read for, and neither of them stopped being true.
    /// </remarks>
    private readonly Dictionary<ulong, (string PackName, int Release)> _lastTold = [];

    public event EventHandler<IReadOnlyList<SteamPeerPresence>>? PeersChanged;

    /// <summary>
    /// Whether a friend without our keys can be called elsewhere yet.
    /// </summary>
    /// <remarks>
    /// Two things have to be true before saying it, and both are about not
    /// saying it wrongly. Steam has to have had time to answer the request for
    /// their keys, or a friend who just opened the launcher is announced as
    /// being in another game for as long as it takes Steam to get round to it.
    /// And whoever is already known to be here stays here: a read that comes
    /// back empty is Steam being Steam, not somebody leaving, which is the
    /// whole reason a peer is given three minutes of grace in the first place.
    /// Their launcher says goodbye on the way out, and that clears them at
    /// once, so the honest case is not slowed by this.
    /// </remarks>
    /// <summary>A player in the shared app who is not answering, told by what he last said.</summary>
    private SteamPeerPresence Elsewhere(SteamId64 peerId, SteamFriendInfo friend)
    {
        _lastTold.TryGetValue(friend.SteamId64, out var told);
        return new SteamPeerPresence
        {
            SteamId = peerId,
            PersonaName = friend.PersonaName,
            IsOutsideLauncher = true,
            PackName = told.PackName ?? "",
            Release = told.Release
        };
    }

    private bool IsElsewhere(SteamFriendInfo friend, DateTimeOffset now)
    {
        if (!friend.IsInSharedApp) return false;
        if (!_firstAsked.TryGetValue(friend.SteamId64, out var asked) ||
            now - asked < OutsideGrace)
        {
            return false;
        }

        lock (_gate)
        {
            return !_peers.TryGetValue(friend.SteamId64, out var known) ||
                   known.Presence.IsOutsideLauncher;
        }
    }

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
        var refused = 0;
        foreach (var (key, value) in SteamPresenceCodec.Encode(presence))
        {
            if (!client.SetPresence(key, value)) refused++;
        }

        if (!_publishedPresence || refused > 0)
        {
            _publishedPresence = true;
            logger?.Info(refused == 0
                ? $"Published this launcher's presence to Steam as {presence.PlayerName} " +
                  $"(protocol {presence.ProtocolVersion}, state {presence.State})."
                : $"Steam refused {refused} of this launcher's presence keys; " +
                  "friends will not see this launcher.");
        }
    }

    /// <summary>
    /// Says goodbye. Steam does not take a launcher's rich presence down when
    /// the launcher closes - the keys sit there and friends keep reading them,
    /// so a player who has quit stays in everyone's list, and a report sent to
    /// them fails on a connection nobody is waiting on. Writing the leaving
    /// state is the one message that says the difference between gone and
    /// merely unreadable.
    /// </summary>
    /// <param name="presence">This launcher's presence, as last published.</param>
    public void PublishDeparture(SteamPeerPresence presence)
    {
        ArgumentNullException.ThrowIfNull(presence);
        if (!client.Status.IsReady) return;
        var refused = 0;
        foreach (var (key, value) in SteamPresenceCodec.Encode(presence with
                 {
                     State = SteamPresenceCodec.StateOffline,
                     HostedWorldId = string.Empty,
                     WaypointProviders = [],
                     IsSkinAvailable = false
                 }))
        {
            if (!client.SetPresence(key, value)) refused++;
        }

        logger?.Info(refused == 0
            ? "Told Steam this launcher is leaving; friends drop it from their lists on the next read."
            : $"Steam refused {refused} of the leaving keys; friends will wait out the grace period instead.");
    }

    /// <summary>
    /// A friend told us about themselves directly, over a connection, because
    /// Steam would not. Their account of their presence outranks the nothing
    /// Steam offered, and it lives as long as any other entry does.
    /// </summary>
    public void Introduce(SteamPeerPresence presence)
    {
        ArgumentNullException.ThrowIfNull(presence);
        var now = clock();
        bool changed;
        lock (_gate)
        {
            changed = !_peers.TryGetValue(presence.SteamId.Value, out var previous) ||
                      previous.Presence != presence;
            _peers[presence.SteamId.Value] = (presence, now);
        }
        if (!changed) return;
        logger?.Info($"{presence.PlayerName} introduced themselves over a launcher connection " +
                     $"(protocol {presence.ProtocolVersion}, state {presence.State}).");
        PeersChanged?.Invoke(this, Peers);
    }

    /// <summary>
    /// Re-reads the friend list. Cheap enough for the UI timer: Steam serves
    /// the cached presence of friends the client already knows about.
    /// </summary>
    public void Refresh()
    {
        if (!client.Status.IsReady) return;

        var now = clock();
        var changed = false;
        var local = client.Status.SteamId64;

        var friends = client.Friends;
        if (friends.Count != _lastLoggedFriendCount)
        {
            _lastLoggedFriendCount = friends.Count;
            logger?.Info($"Steam reports {friends.Count} friend(s) to look for this launcher among.");
        }

        foreach (var friend in friends)
        {
            if (friend.SteamId64 == local) continue;
            if (!SteamId64.TryFrom(friend.SteamId64, out var peerId)) continue;

            // Whether Steam calls a friend "in Spacewar" is not the question -
            // that flag depends on how their launcher was started and on their
            // privacy settings, and skipping on it made friends who were
            // plainly running the launcher invisible. Publishing our keys is
            // the proof, so every friend is asked and the keys decide.
            if (!_lastRequested.TryGetValue(friend.SteamId64, out var asked) ||
                now - asked >= RequestInterval)
            {
                _lastRequested[friend.SteamId64] = now;
                _firstAsked.TryAdd(friend.SteamId64, now);
                api.RequestFriendRichPresence(friend.SteamId64);
            }

            var presence = SteamPresenceCodec.TryDecode(
                peerId,
                friend.PersonaName,
                key => api.GetFriendRichPresence(friend.SteamId64, key));
            // Somebody in the same app publishing none of our keys is in
            // something else that uses it. Spacewar is Valve's example app and
            // half the internet borrows its id - engines default to it while a
            // game is in development, projects that never shipped on Steam use
            // it for the friends list and the peer-to-peer punch-through, and
            // cracked games announce themselves as it. So this says "another
            // game" and not "Minecraft": it has no way of knowing which.
            //
            // They are listed rather than hidden because "not in the list at
            // all" is what a player reads as offline, and then asks why a world
            // cannot be sent to somebody they can plainly see playing.
            if (presence is not null &&
                (presence.Release > 0 || presence.PackName.Length > 0))
            {
                _lastTold[friend.SteamId64] = (presence.PackName, presence.Release);
            }
            presence ??= IsElsewhere(friend, now)
                ? Elsewhere(peerId, friend)
                : null;
            if (presence is null) continue;

            // A goodbye is not a presence. Their launcher wrote it on the way
            // out, so they go now rather than in three minutes - and they stay
            // gone, because the keys that say it will still be there next time.
            //
            // Unless they are still in the app this launcher shares, which
            // means they closed the launcher and kept playing. The keys they
            // left behind still name the build and the release - the goodbye
            // clears the state, the world and the skin, and nothing else - so
            // they are moved to that rather than dropped. It reads the same to
            // somebody who starts their launcher afterwards and has never seen
            // them: the answer is in the keys, not in what we happened to
            // witness.
            if (presence.HasLeft)
            {
                if (friend.IsInSharedApp)
                {
                    var stillPlaying = new SteamPeerPresence
                    {
                        SteamId = peerId,
                        PersonaName = friend.PersonaName,
                        IsOutsideLauncher = true,
                        PackName = presence.PackName,
                        Release = presence.Release
                    };
                    lock (_gate)
                    {
                        changed |= !_peers.TryGetValue(friend.SteamId64, out var before) ||
                                   before.Presence != stillPlaying;
                        _peers[friend.SteamId64] = (stillPlaying, now);
                    }
                    continue;
                }

                lock (_gate)
                {
                    if (_peers.Remove(friend.SteamId64))
                    {
                        changed = true;
                        logger?.Info($"{friend.PersonaName} closed their launcher.");
                    }
                }
                continue;
            }
            // A friend who introduced themselves ages by the introduction, not
            // by a Steam read that keeps coming back empty.

            lock (_gate)
            {
                changed |= !_peers.TryGetValue(friend.SteamId64, out var previous) ||
                           previous.Presence != presence;
                _peers[friend.SteamId64] = (presence, now);
            }
        }

        // Anyone we hold a connection to is here, whatever presence says.
        // Presence is Steam's opinion and it can simply refuse to serve it -
        // one player's launcher connected twice and sent a bug report while
        // staying invisible in the other's list, because reading a friend's
        // rich presence depends on their profile privacy and talking to them
        // does not. A conversation in progress outranks any opinion about
        // whether they are there.
        foreach (var connected in transport?.ConnectedPeers ?? [])
        {
            lock (_gate)
            {
                if (_peers.TryGetValue(connected.Value, out var live))
                {
                    _peers[connected.Value] = (live.Presence, now);
                    continue;
                }
            }

            // Not decodable, but demonstrably here. List them under the name
            // Steam does give us, with an unknown protocol so nothing assumes
            // a compatibility it has not seen proof of.
            if (!SteamId64.TryFrom(connected.Value, out var connectedId)) continue;
            var known = friends.FirstOrDefault(friend => friend.SteamId64 == connected.Value);
            var persona = string.IsNullOrWhiteSpace(known?.PersonaName)
                ? connectedId.ToString()
                : known.PersonaName;

            lock (_gate)
            {
                _peers[connected.Value] = (new SteamPeerPresence
                {
                    SteamId = connectedId,
                    PersonaName = persona,
                    PlayerName = persona,
                    ProtocolVersion = 0
                }, now);
            }
            changed = true;
            logger?.Info(
                $"{persona} is connected to this launcher but Steam will not serve their " +
                "presence; listing them from the live connection.");
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

        int count;
        lock (_gate) count = _peers.Count;
        if (count != _lastLoggedPeerCount)
        {
            _lastLoggedPeerCount = count;
            if (count > 0)
            {
                logger?.Info($"{count} friend(s) are running this launcher.");
            }
            else
            {
                // An empty list has three different causes and they need
                // different answers, so say which one this is. Steam holding no
                // keys at all for a friend means it never served their presence
                // to us; keys without ours means they are running something
                // else. Guessing between those cost two players an evening.
                var detail = friends
                    .Select(friend =>
                        $"{friend.PersonaName}: {api.GetFriendRichPresenceKeyCount(friend.SteamId64)} key(s)" +
                        (friend.IsInSharedApp ? ", in app" : ""))
                    .Where(line => !line.EndsWith(": 0 key(s)", StringComparison.Ordinal))
                    .Take(8)
                    .ToArray();
                logger?.Info(
                    $"No friend of {friends.Count} is publishing launcher presence right now" +
                    (detail.Length == 0
                        ? "; Steam holds no presence keys for any of them."
                        : $"; Steam holds keys for {string.Join(", ", detail)}."));
            }
        }
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
