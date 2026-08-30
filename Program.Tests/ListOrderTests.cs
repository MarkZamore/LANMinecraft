using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// One order for one set of things, wherever the window lists them.
/// </summary>
public sealed class ListOrderTests
{
    // Real account ids, because SteamId64 refuses anything else.
    private const ulong FirstAccount = 76_561_197_960_265_729UL;

    private static PeerViewModel Peer(string playerName, ulong account)
    {
        Assert.True(SteamId64.TryFrom(FirstAccount + account, out var steamId));
        return new PeerViewModel { SteamId = steamId, PlayerName = playerName, PersonaName = playerName };
    }

    /// <summary>
    /// The players a world is handed to and the players a report is sent to are
    /// the same friends in two drop-downs a few rows apart. One of them used to
    /// read in whatever order Steam answered in.
    /// </summary>
    [Fact]
    public void PlayersComeOutByName()
    {
        var peers = new[] { Peer("anuvenn", 3), Peer("Oskar", 1), Peer("bob", 2) };

        // The name only: what a row shows after it is the player's state.
        var ordered = ListOrder.Players(peers).Select(peer => peer.PlayerName).ToArray();

        Assert.Equal(["anuvenn", "bob", "Oskar"], ordered);
    }

    /// <summary>Two people under one name still come out in one order.</summary>
    [Fact]
    public void PlayersSharingAName_AreOrderedByWhoTheyAre()
    {
        var first = Peer("anuvenn", 7);
        var second = Peer("anuvenn", 3);

        var ordered = ListOrder.Players([first, second]).ToArray();
        var reversed = ListOrder.Players([second, first]).ToArray();

        Assert.Equal(reversed, ordered);
        Assert.Equal(FirstAccount + 3, ordered[0].SteamId.Value);
    }

    private static ClientBuildViewModel Build(string name, bool installed = true) =>
        new()
        {
            Name = installed ? name : name + "*",
            RelativePath = name,
            FullPath = @"C:\packs\" + name,
            IsInstalled = installed
        };

    /// <summary>
    /// A build only offered is listed among the rest and not after them. The
    /// star that marks it is part of the row, not of the name.
    /// </summary>
    [Fact]
    public void BuildsComeOutByName_WhetherOrNotTheyAreDownloaded()
    {
        var builds = new[]
        {
            Build("LL8 Extended"),
            Build("All The Mods 10", installed: false),
            Build("RPG Ars Nouveau"),
            Build("All The Fabric 3", installed: false)
        };

        var ordered = ListOrder.Builds(builds).Select(build => build.RelativePath).ToArray();

        Assert.Equal(
            ["All The Fabric 3", "All The Mods 10", "LL8 Extended", "RPG Ars Nouveau"],
            ordered);
    }
}
