using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Who shows up in the player list, and when the launcher will talk to them.
///
/// Both cases here come from the same complaint: a friend was plainly running
/// the launcher and simply was not there. Steam only reports a friend as "in
/// Spacewar" depending on how their launcher was started and on their privacy
/// settings, and the directory used to skip everyone else - so the presence
/// keys that prove they are running it were never even read.
/// </summary>
public sealed class PeerVisibilityTests
{
    private const ulong LocalSteamId = 76561198000000001;
    private const ulong FriendSteamId = 76561198000000002;

    [Fact]
    public async Task AFriendSteamIsNotReportedAsPlaying_IsStillAPeer()
    {
        var api = new FakeSteamApi
        {
            SteamRunning = true,
            LoggedOn = true,
            SteamId = LocalSteamId,
            Persona = "MarkZamore"
        };
        // Steam does not say this friend is in the app - which is exactly the
        // state that used to hide them.
        api.FriendList.Add(new SteamFriendInfo(FriendSteamId, "anuvenn", IsInSharedApp: false, LobbyId: 0));
        Assert.True(SteamId64.TryFrom(FriendSteamId, out var friendId));
        foreach (var (key, value) in SteamPresenceCodec.Encode(FriendPresence(friendId)))
        {
            api.FriendPresence[(FriendSteamId, key)] = value;
        }

        await using var client = new SteamClientService(api);
        await client.StartAsync(CancellationToken.None);
        var directory = new SteamPeerDirectory(client);

        directory.Refresh();

        var peer = Assert.Single(directory.Peers);
        Assert.Equal(friendId, peer.SteamId);
        Assert.Equal("anuvenn", peer.PlayerName);
    }

    /// <summary>
    /// Somebody in the app this launcher shares with e4steam, publishing none
    /// of its keys, is in Minecraft without it. They are listed and marked as
    /// being elsewhere rather than hidden: a player who can see a friend
    /// playing reads an empty list as a fault in the launcher, and then asks
    /// why a world cannot be sent to somebody who is plainly there.
    /// </summary>
    [Fact]
    public async Task AFriendInTheGameWithoutTheLauncher_IsListedAsElsewhere()
    {
        var api = new FakeSteamApi
        {
            SteamRunning = true,
            LoggedOn = true,
            SteamId = LocalSteamId,
            Persona = "MarkZamore"
        };
        api.FriendList.Add(new SteamFriendInfo(FriendSteamId, "anuvenn", IsInSharedApp: true, LobbyId: 0));

        await using var client = new SteamClientService(api);
        await client.StartAsync(CancellationToken.None);
        var at = DateTimeOffset.UnixEpoch;
        var directory = new SteamPeerDirectory(client, clock: () => at);

        // Asking Steam for their keys is not the same as being answered, so
        // nothing is concluded on the first look.
        directory.Refresh();
        Assert.Empty(directory.Peers);

        // Once Steam has had its chance and still says nothing, they are in
        // something else that borrows the same app id.
        at = at.AddSeconds(30);
        directory.Refresh();

        var peer = Assert.Single(directory.Peers);
        Assert.True(peer.IsOutsideLauncher);
        Assert.Equal("anuvenn", peer.PersonaName);
        Assert.False(peer.IsMinecraftRunning);
    }

    /// <summary>
    /// And somebody already known to be in the launcher is never demoted to it.
    /// Steam serves a friend's keys on its own schedule and a read that comes
    /// back empty is ordinary; announcing "in another game" on one such read
    /// would flicker for everybody, permanently.
    /// </summary>
    [Fact]
    public async Task AFriendAlreadyInTheLauncher_IsNotCalledElsewhereOnAnEmptyRead()
    {
        var api = new FakeSteamApi
        {
            SteamRunning = true,
            LoggedOn = true,
            SteamId = LocalSteamId,
            Persona = "MarkZamore"
        };
        api.FriendList.Add(new SteamFriendInfo(FriendSteamId, "anuvenn", IsInSharedApp: true, LobbyId: 0));
        foreach (var (key, value) in SteamPresenceCodec.Encode(Presence()))
        {
            api.FriendPresence[(FriendSteamId, key)] = value;
        }

        await using var client = new SteamClientService(api);
        await client.StartAsync(CancellationToken.None);
        var at = DateTimeOffset.UnixEpoch;
        var directory = new SteamPeerDirectory(client, clock: () => at);

        directory.Refresh();
        Assert.False(Assert.Single(directory.Peers).IsOutsideLauncher);

        // Steam simply stops serving the keys for a while, as it does.
        api.FriendPresence.Clear();
        at = at.AddSeconds(30);
        directory.Refresh();

        var peer = Assert.Single(directory.Peers);
        Assert.False(peer.IsOutsideLauncher);
        Assert.Equal("anuvenn", peer.PlayerName);
    }

    /// <summary>
    /// Closing the launcher while the game runs. The goodbye keys keep the
    /// build and the release - only the state, the world and the skin are
    /// cleared - so somebody reading them, whether they had seen this player
    /// before or not, still learns both.
    /// </summary>
    [Fact]
    public async Task AGoodbyeFromSomebodyStillInTheGame_KeepsWhatTheyLastSaid()
    {
        var api = new FakeSteamApi
        {
            SteamRunning = true,
            LoggedOn = true,
            SteamId = LocalSteamId,
            Persona = "MarkZamore"
        };
        api.FriendList.Add(new SteamFriendInfo(FriendSteamId, "anuvenn", IsInSharedApp: true, LobbyId: 0));
        var leaving = Presence() with
        {
            PackName = "LL8 Extended",
            Release = 312,
            State = SteamPresenceCodec.StateOffline
        };
        foreach (var (key, value) in SteamPresenceCodec.Encode(leaving))
        {
            api.FriendPresence[(FriendSteamId, key)] = value;
        }

        await using var client = new SteamClientService(api);
        await client.StartAsync(CancellationToken.None);
        // A launcher that has never seen this player before: everything it
        // knows has to come out of the keys.
        var directory = new SteamPeerDirectory(client);

        directory.Refresh();

        var peer = Assert.Single(directory.Peers);
        Assert.True(peer.IsOutsideLauncher);
        Assert.Equal("LL8 Extended", peer.PackName);
        Assert.Equal(312, peer.Release);
    }

    /// <summary>
    /// And a goodbye from somebody who left the game as well is what it always
    /// was: gone, at once, rather than in three minutes.
    /// </summary>
    [Fact]
    public async Task AGoodbyeFromSomebodyWhoLeftAltogether_DropsThem()
    {
        var api = new FakeSteamApi
        {
            SteamRunning = true,
            LoggedOn = true,
            SteamId = LocalSteamId,
            Persona = "MarkZamore"
        };
        api.FriendList.Add(new SteamFriendInfo(FriendSteamId, "anuvenn", IsInSharedApp: false, LobbyId: 0));
        foreach (var (key, value) in SteamPresenceCodec.Encode(
                     Presence() with { State = SteamPresenceCodec.StateOffline }))
        {
            api.FriendPresence[(FriendSteamId, key)] = value;
        }

        await using var client = new SteamClientService(api);
        await client.StartAsync(CancellationToken.None);
        var directory = new SteamPeerDirectory(client);

        directory.Refresh();

        Assert.Empty(directory.Peers);
    }

    private static SteamPeerPresence Presence()
    {
        Assert.True(SteamId64.TryFrom(FriendSteamId, out var peer));
        return new SteamPeerPresence
        {
            SteamId = peer,
            PersonaName = "anuvenn",
            ProtocolVersion = SteamPresenceCodec.ProtocolVersion,
            PlayerName = "anuvenn",
            State = SteamPresenceCodec.StateIdle
        };
    }

    /// <summary>
    /// A friend doing something else entirely is not in the list at all. The
    /// list is of people who could be played with, and Steam has a friend list
    /// of its own for everybody else.
    /// </summary>
    [Fact]
    public async Task AFriendOutsideTheGameAltogether_IsNotAPeer()
    {
        var api = new FakeSteamApi
        {
            SteamRunning = true,
            LoggedOn = true,
            SteamId = LocalSteamId,
            Persona = "MarkZamore"
        };
        api.FriendList.Add(new SteamFriendInfo(FriendSteamId, "anuvenn", IsInSharedApp: false, LobbyId: 0));

        await using var client = new SteamClientService(api);
        await client.StartAsync(CancellationToken.None);
        var directory = new SteamPeerDirectory(client);

        directory.Refresh();

        Assert.Empty(directory.Peers);
    }

    /// <summary>
    /// The directory keeps a peer alive as long as their keys are readable,
    /// even when nothing about them changes - a friend sitting in the launcher
    /// publishes the same presence for as long as they sit there, and used to
    /// disappear from the list once the window's own timeout passed.
    /// </summary>
    [Fact]
    public async Task APeerWhosePresenceNeverChanges_StaysListed()
    {
        var api = new FakeSteamApi
        {
            SteamRunning = true,
            LoggedOn = true,
            SteamId = LocalSteamId,
            Persona = "MarkZamore"
        };
        api.FriendList.Add(new SteamFriendInfo(FriendSteamId, "anuvenn", IsInSharedApp: true, LobbyId: 0));
        Assert.True(SteamId64.TryFrom(FriendSteamId, out var friendId));
        foreach (var (key, value) in SteamPresenceCodec.Encode(FriendPresence(friendId)))
        {
            api.FriendPresence[(FriendSteamId, key)] = value;
        }

        await using var client = new SteamClientService(api);
        await client.StartAsync(CancellationToken.None);
        var directory = new SteamPeerDirectory(client);

        var changes = 0;
        directory.PeersChanged += (_, _) => changes++;
        directory.Refresh();
        directory.Refresh();
        directory.Refresh();

        // Only the first refresh is news, and the peer is still there after the
        // quiet ones - the list, not the event, is the source of truth.
        Assert.Equal(1, changes);
        Assert.Single(directory.Peers);
    }

    /// <summary>
    /// A world transfer takes both machines' attention for a long time, and
    /// Steam serves a friend's presence on its own schedule. The friend
    /// disappeared from the list mid-transfer while their world was still
    /// arriving; holding a connection to somebody is proof enough.
    /// </summary>
    [Fact]
    public async Task APeerWeAreTalkingTo_SurvivesAGapInTheirPresence()
    {
        var api = new FakeSteamApi
        {
            SteamRunning = true,
            LoggedOn = true,
            SteamId = LocalSteamId,
            Persona = "MarkZamore"
        };
        api.FriendList.Add(new SteamFriendInfo(FriendSteamId, "anuvenn", IsInSharedApp: true, LobbyId: 0));
        Assert.True(SteamId64.TryFrom(FriendSteamId, out var friendId));
        foreach (var (key, value) in SteamPresenceCodec.Encode(FriendPresence(friendId)))
        {
            api.FriendPresence[(FriendSteamId, key)] = value;
        }

        var network = new InMemoryPeerNetwork();
        var transport = network.CreateTransport(LocalSteamId, "MarkZamore");
        await using var client = new SteamClientService(api);
        await client.StartAsync(CancellationToken.None);
        var directory = new SteamPeerDirectory(client, transport);
        directory.Refresh();
        Assert.Single(directory.Peers);

        // Steam stops serving their keys, but the transfer connection is open.
        api.FriendPresence.Clear();
        transport.Connected.Add(friendId);
        directory.Refresh();

        Assert.Single(directory.Peers);
    }

    /// <summary>
    /// A report is what a player sends when something went wrong, which is
    /// usually while the game is running or right after it failed to start.
    /// Nothing about being busy may take that away.
    /// </summary>
    [Fact]
    public void APeerInGame_IsStillSomewhereToSendAReport()
    {
        Assert.True(SteamId64.TryFrom(FriendSteamId, out var friendId));
        var peer = new PeerViewModel { SteamId = friendId };
        peer.Apply(
            FriendPresence(friendId) with { State = SteamPresenceCodec.StateInGame },
            "pack-hash");

        Assert.True(peer.IsMinecraftRunning);
        Assert.True(peer.SupportsDiagnosticLogs);
        Assert.True(peer.IsCompatible);
    }

    private static SteamPeerPresence FriendPresence(SteamId64 peer) => new()
    {
        SteamId = peer,
        PersonaName = "anuvenn",
        ProtocolVersion = PortableFormat.ProtocolVersion,
        PlayerName = "anuvenn",
        MinecraftUuid = Guid.NewGuid().ToString("D"),
        PackHash = "pack-hash",
        DiagnosticProtocolVersion = PortableFormat.ProtocolVersion
    };
}
