using Minecraft;

namespace Minecraft.Tests;

public sealed class PackDisplayNameTests
{
    private const string Legacy = "All the Mods 9 Modified";
    private const string Display = "All The Mods 9 Modified";

    [Fact]
    public void LegacyCatalogAndLocalFolderUseTheSameDisplayName()
    {
        Assert.Equal(Display, PackDisplayName.For(Legacy, Legacy));
        Assert.Equal(Display, PackDisplayName.For(Legacy, Display));
        Assert.Equal("Custom name", PackDisplayName.For(Legacy, "Custom name"));
        Assert.Equal("Another pack", PackDisplayName.For("Another pack", "Another pack"));
    }

    [Fact]
    public void OldPeerPresenceUsesCanonicalNameInThePlayerListAndArrivalNotice()
    {
        Assert.True(SteamId64.TryFrom(76561198986239755, out var steamId));
        var peer = new PeerViewModel { SteamId = steamId,
            ProtocolVersion = PortableFormat.ProtocolVersion, PackName = Legacy };
        var notifier = new PeerArrivalNotifier();
        notifier.Observe([peer]);
        peer.IsMinecraftRunning = true;
        Assert.Equal(Display, peer.PackName);
        Assert.Contains(Display, peer.DisplayName);
        Assert.Equal("Зашёл в " + Display, Assert.Single(notifier.Observe([peer])).Body);
    }

    [Fact]
    public void WorldLabelChangesWithoutChangingItsOwnershipPath()
    {
        var world = new WorldViewModel { Name = "World", Path = "world",
            BuildName = Legacy, BuildRelativePath = Legacy };
        Assert.Equal("World (" + Display + ")", world.DisplayName);
        Assert.Equal(Legacy, world.BuildRelativePath);
    }
}
