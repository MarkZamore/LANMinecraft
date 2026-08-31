using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// What happens to a player in everyone else's list when they close the
/// launcher.
///
/// Steam does not take a launcher's rich presence down when the launcher goes
/// away: the keys sit in Steam and friends keep reading them, so the player
/// stayed listed as available. Sending them a bug report then failed on a
/// connection nobody was waiting on. The launcher now writes one leaving state
/// on the way out, and a reader who sees it stops treating that peer as
/// available at once rather than waiting out the grace period that exists for
/// keys Steam merely refuses to serve.
///
/// Whether they leave the list depends on where they went. Somebody still in
/// the app this launcher shares closed the launcher and kept playing, and the
/// keys they left behind still name their build and their release - the
/// goodbye clears the state, the world and the skin and nothing else - so they
/// stay listed as being elsewhere, and cannot be sent anything. Somebody who
/// left the app as well is simply gone.
/// </summary>
public sealed class PeerDepartureTests
{
    private const ulong LocalSteamId = 76561198000000001;
    private const ulong FriendSteamId = 76561198000000002;

    [Fact]
    public async Task AFriendWhoClosedTheirLauncher_StopsBeingAvailableAtOnce()
    {
        var api = ReadyApi();
        Assert.True(SteamId64.TryFrom(FriendSteamId, out var friendId));
        Publish(api, FriendPresence(friendId));

        await using var client = new SteamClientService(api);
        await client.StartAsync(CancellationToken.None);
        var directory = new SteamPeerDirectory(client);
        directory.Refresh();
        Assert.False(Assert.Single(directory.Peers).IsOutsideLauncher);

        // Their launcher says goodbye; the keys stay readable, as Steam leaves
        // them, and say "offline". This friend is still in the shared app - the
        // game is what they closed the launcher to keep playing.
        Publish(api, FriendPresence(friendId) with { State = SteamPresenceCodec.StateOffline });
        directory.Refresh();

        // Listed, but as somebody the launcher can no longer reach: nothing is
        // sent to them, which is what the goodbye was written for.
        var peer = Assert.Single(directory.Peers);
        Assert.True(peer.IsOutsideLauncher);
        Assert.False(peer.IsMinecraftRunning);
    }

    /// <summary>
    /// And leaving the game as well is leaving the list.
    /// </summary>
    [Fact]
    public async Task AFriendWhoClosedEverything_LeavesTheListAtOnce()
    {
        var api = ReadyApi();
        api.FriendList.Clear();
        api.FriendList.Add(new SteamFriendInfo(FriendSteamId, "anuvenn", IsInSharedApp: false, LobbyId: 0));
        Assert.True(SteamId64.TryFrom(FriendSteamId, out var friendId));
        Publish(api, FriendPresence(friendId));

        await using var client = new SteamClientService(api);
        await client.StartAsync(CancellationToken.None);
        var directory = new SteamPeerDirectory(client);
        directory.Refresh();
        Assert.Single(directory.Peers);

        Publish(api, FriendPresence(friendId) with { State = SteamPresenceCodec.StateOffline });
        directory.Refresh();

        Assert.Empty(directory.Peers);
    }

    /// <summary>
    /// The goodbye is written once and Steam keeps serving it. Reading it twice
    /// must not put them back as available, and must not flicker.
    /// </summary>
    [Fact]
    public async Task TheGoodbyeKeepsThemUnavailable()
    {
        var api = ReadyApi();
        api.FriendList.Clear();
        api.FriendList.Add(new SteamFriendInfo(FriendSteamId, "anuvenn", IsInSharedApp: false, LobbyId: 0));
        Assert.True(SteamId64.TryFrom(FriendSteamId, out var friendId));
        Publish(api, FriendPresence(friendId) with { State = SteamPresenceCodec.StateOffline });

        await using var client = new SteamClientService(api);
        await client.StartAsync(CancellationToken.None);
        var directory = new SteamPeerDirectory(client);

        directory.Refresh();
        directory.Refresh();

        Assert.Empty(directory.Peers);
    }

    /// <summary>
    /// The launcher's own goodbye reaches Steam, and it says leaving rather
    /// than any of the states that mean somebody is there.
    /// </summary>
    [Fact]
    public async Task LeavingIsPublished()
    {
        var api = ReadyApi();
        Assert.True(SteamId64.TryFrom(LocalSteamId, out var localId));

        await using var client = new SteamClientService(api);
        await client.StartAsync(CancellationToken.None);
        var directory = new SteamPeerDirectory(client);

        directory.PublishLocalPresence(FriendPresence(localId) with
        {
            State = SteamPresenceCodec.StateInGame
        });
        Assert.Equal(SteamPresenceCodec.StateInGame, api.Presence[SteamPresenceCodec.StateKey]);

        directory.PublishDeparture(FriendPresence(localId) with
        {
            State = SteamPresenceCodec.StateInGame
        });

        Assert.Equal(SteamPresenceCodec.StateOffline, api.Presence[SteamPresenceCodec.StateKey]);
        // A leaving launcher is not hosting a world and has no skin to fetch;
        // saying otherwise would send somebody after both.
        Assert.Equal(string.Empty, api.Presence[SteamPresenceCodec.WorldKey]);
        Assert.Equal(string.Empty, api.Presence[SteamPresenceCodec.SkinKey]);
    }

    /// <summary>
    /// A goodbye survives the round trip. Without this the state would arrive
    /// as any other unknown string and the peer would simply be listed.
    /// </summary>
    [Fact]
    public void TheStateIsCarriedByThePresenceKeys()
    {
        Assert.True(SteamId64.TryFrom(FriendSteamId, out var friendId));
        var values = SteamPresenceCodec.Encode(
            FriendPresence(friendId) with { State = SteamPresenceCodec.StateOffline });

        var decoded = SteamPresenceCodec.TryDecode(
            friendId, "anuvenn", key => values.TryGetValue(key, out var value) ? value : "");

        Assert.NotNull(decoded);
        Assert.True(decoded!.HasLeft);
        Assert.False(decoded.IsMinecraftRunning);
    }

    [Theory]
    [InlineData(SteamPresenceCodec.StateIdle)]
    [InlineData(SteamPresenceCodec.StatePreparing)]
    [InlineData(SteamPresenceCodec.StateInGame)]
    [InlineData(SteamPresenceCodec.StateHosting)]
    public void EveryOtherStateMeansTheyAreHere(string state)
    {
        Assert.True(SteamId64.TryFrom(FriendSteamId, out var friendId));
        Assert.False((FriendPresence(friendId) with { State = state }).HasLeft);
    }

    private static FakeSteamApi ReadyApi() => new()
    {
        SteamRunning = true,
        LoggedOn = true,
        SteamId = LocalSteamId,
        Persona = "MarkZamore",
        FriendList = { new SteamFriendInfo(FriendSteamId, "anuvenn", IsInSharedApp: true, LobbyId: 0) }
    };

    private static void Publish(FakeSteamApi api, SteamPeerPresence presence)
    {
        foreach (var (key, value) in SteamPresenceCodec.Encode(presence))
        {
            api.FriendPresence[(FriendSteamId, key)] = value;
        }
    }

    private static SteamPeerPresence FriendPresence(SteamId64 peer) => new()
    {
        SteamId = peer,
        PersonaName = "anuvenn",
        ProtocolVersion = PortableFormat.ProtocolVersion,
        PlayerName = "anuvenn",
        MinecraftUuid = Guid.NewGuid().ToString("D"),
        PackHash = "pack-hash",
        IsSkinAvailable = true,
        SkinSha256 = new string('a', 64),
        SkinModel = "classic",
        HostedWorldId = "world-1",
        DiagnosticProtocolVersion = PortableFormat.ProtocolVersion
    };
}
