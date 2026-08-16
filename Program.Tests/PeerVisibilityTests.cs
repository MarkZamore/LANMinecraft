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

    [Fact]
    public async Task AFriendWhoIsNotRunningTheLauncher_IsNotAPeer()
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
