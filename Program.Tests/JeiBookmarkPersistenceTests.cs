using System.Net;
using System.Net.Sockets;
using Minecraft;

namespace Minecraft.Tests;

// JEI keeps bookmarks per world under config/jei/world/**, and for a peer's
// world that path is derived from the address Minecraft joined. Both the folder
// and the address have to survive a launcher restart or bookmarks come back
// empty every session.
public sealed class JeiBookmarkPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-jei-bookmarks-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task InstancePreparation_KeepsJeiBookmarksForPeerWorlds()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var logger = new Logger(paths.LogFile);
        var packDir = Path.Combine(paths.Packs, "TestPack");
        Directory.CreateDirectory(packDir);
        File.WriteAllText(Path.Combine(packDir, "client.jar"), "jar");
        File.WriteAllText(
            Path.Combine(packDir, PackManifestService.ManifestFileName),
            """
            {
              "schemaVersion": 1,
              "minecraftVersion": "1.21.1",
              "loader": { "type": "vanilla" },
              "clientJar": "client.jar"
            }
            """);

        using var service = new PackInstanceService(paths, logger);
        await service.PrepareAsync("TestPack");

        var gameDir = service.GetInstanceDirectory("TestPack");
        var serverBookmarks = Path.Combine(
            gameDir, "config", "jei", "world", "server", "MinecraftPortable_1a2b3c4d");
        Directory.CreateDirectory(serverBookmarks);
        var bookmarksFile = Path.Combine(serverBookmarks, "bookmarks.json");
        File.WriteAllText(bookmarksFile, """[{"ingredient":{"id":"minecraft:diamond"}}]""");
        var localBookmarks = Path.Combine(gameDir, "config", "jei", "world", "local", "bookmarks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(localBookmarks)!);
        File.WriteAllText(localBookmarks, """[{"ingredient":{"id":"minecraft:emerald"}}]""");

        // Closing the game and starting it again must not touch either file.
        await service.CleanupGeneratedLocalArtifactsAsync("TestPack", removeSessionLogs: false);
        await service.PrepareAsync("TestPack");

        Assert.True(File.Exists(bookmarksFile), "Bookmarks for a peer's world were deleted.");
        Assert.Contains("minecraft:diamond", File.ReadAllText(bookmarksFile), StringComparison.Ordinal);
        // The local folder was never deleted; this only guards against a future
        // sanitizer growing to cover it too.
        Assert.True(File.Exists(localBookmarks), "Bookmarks for the local world were deleted.");
    }

    [Fact]
    public void PortStore_ReusesTheSamePortForAPeerAcrossRestarts()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var logger = new Logger(paths.LogFile);
        var peer = Guid.NewGuid().ToString("D");
        var otherPeer = Guid.NewGuid().ToString("D");

        var first = new LanRelayPortStore(paths, logger);
        Assert.Equal(0, first.GetRememberedPort(peer));
        first.RememberPort(peer, 31234);
        first.RememberPort(otherPeer, 31235);

        // A fresh instance stands in for the next launcher run.
        var second = new LanRelayPortStore(paths, logger);
        Assert.Equal(31234, second.GetRememberedPort(peer));
        Assert.Equal(31235, second.GetRememberedPort(otherPeer));
        Assert.Equal(0, second.GetRememberedPort(Guid.NewGuid().ToString("D")));

        // Peer ids are normalized, so formatting differences still match.
        Assert.Equal(31234, second.GetRememberedPort(peer.ToUpperInvariant()));
        Assert.Equal(31234, second.GetRememberedPort(Guid.Parse(peer).ToString("N")));
        Assert.Equal(0, second.GetRememberedPort("not-a-guid"));
        Assert.Equal(0, second.GetRememberedPort(null));
    }

    [Fact]
    public void PortStore_WritesFromASecondInstanceAreNotLost()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var logger = new Logger(paths.LogFile);
        var peerA = Guid.NewGuid().ToString("D");
        var peerB = Guid.NewGuid().ToString("D");

        var first = new LanRelayPortStore(paths, logger);
        var second = new LanRelayPortStore(paths, logger);
        first.RememberPort(peerA, 31000);
        second.RememberPort(peerB, 31001);
        first.RememberPort(peerA, 31002);

        var reloaded = new LanRelayPortStore(paths, logger);
        Assert.Equal(31002, reloaded.GetRememberedPort(peerA));
        Assert.Equal(31001, reloaded.GetRememberedPort(peerB));
    }

    [Fact]
    public void DerivedPorts_AreStableForAPeerAndAvoidTheDynamicRange()
    {
        var peer = Guid.NewGuid().ToString("D");
        var candidates = LanRelayPortStore.DerivePortCandidates(peer);

        Assert.NotEmpty(candidates);
        // Golden values: the derivation must be a real digest, not a runtime
        // hash. String.GetHashCode is seeded per process, so swapping it in
        // would keep every other assertion here green while moving each user's
        // port on every launcher restart - the exact bug this prevents.
        Assert.Equal(
            [39594, 39595, 39596, 39597, 39598, 39599, 39600, 39601],
            LanRelayPortStore.DerivePortCandidates("00000000-0000-0000-0000-000000000001"));
        Assert.Equal(
            [32438, 32439, 32440, 32441, 32442, 32443, 32444, 32445],
            LanRelayPortStore.DerivePortCandidates("11111111-2222-3333-4444-555555555555"));

        Assert.Equal(candidates, LanRelayPortStore.DerivePortCandidates(peer));
        Assert.Equal(candidates, LanRelayPortStore.DerivePortCandidates(Guid.Parse(peer).ToString("N")));
        Assert.Equal(candidates.Count, candidates.Distinct().Count());
        // Windows hands out 49152-65535 to any process, so staying below it is
        // what makes the port still free next session.
        Assert.All(candidates, port => Assert.InRange(port, 30000, 39999));
        Assert.NotEqual(candidates, LanRelayPortStore.DerivePortCandidates(Guid.NewGuid().ToString("D")));
        Assert.Empty(LanRelayPortStore.DerivePortCandidates("not-a-guid"));
    }

    [Fact]
    public void PortStore_IgnoresUnusablePortsAndSurvivesDamagedState()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var logger = new Logger(paths.LogFile);
        var peer = Guid.NewGuid().ToString("D");

        var store = new LanRelayPortStore(paths, logger);
        store.RememberPort(peer, 0);
        store.RememberPort(peer, 80);
        store.RememberPort(peer, 70000);
        Assert.Equal(0, store.GetRememberedPort(peer));

        File.WriteAllText(paths.LanRelayPortsFile, "{ not json");
        var rebuilt = new LanRelayPortStore(paths, logger);
        Assert.Equal(0, rebuilt.GetRememberedPort(peer));
        rebuilt.RememberPort(peer, 31999);
        Assert.Equal(31999, new LanRelayPortStore(paths, logger).GetRememberedPort(peer));
    }

    [Fact]
    public async Task ClientRelay_BindsTheRememberedPortForAKnownPeer()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var logger = new Logger(paths.LogFile);
        var peer = Guid.NewGuid().ToString("D");
        var route = new PeerCandidateEndpoint
        {
            Address = "10.200.0.2",
            LocalAddress = "10.200.0.1",
            LocalInterfaceId = "selected-interface",
            IsConfirmed = true
        };

        int firstPort;
        var store = new LanRelayPortStore(paths, logger);
        await using (var relay = new LanRelayService(
            logger, new PeerRouteResolver(), new UnreachablePeerConnector(), clock: null, portStore: store))
        {
            var info = await relay.GetOrCreateClientRelayAsync(
                peer, Guid.NewGuid().ToString("N"), [route], 41000);
            firstPort = info.LocalPort;
            Assert.Contains(firstPort, LanRelayPortStore.DerivePortCandidates(peer));
        }

        Assert.Equal(firstPort, new LanRelayPortStore(paths, logger).GetRememberedPort(peer));

        // The next launcher run has to advertise the very same address.
        await using (var restarted = new LanRelayService(
            logger,
            new PeerRouteResolver(),
            new UnreachablePeerConnector(),
            clock: null,
            portStore: new LanRelayPortStore(paths, logger)))
        {
            var reconnected = await restarted.GetOrCreateClientRelayAsync(
                peer, Guid.NewGuid().ToString("N"), [route], 41000);
            Assert.Equal(firstPort, reconnected.LocalPort);
        }

        // Even a lost history file must not move the address.
        File.Delete(paths.LanRelayPortsFile);
        await using var withoutHistory = new LanRelayService(
            logger,
            new PeerRouteResolver(),
            new UnreachablePeerConnector(),
            clock: null,
            portStore: new LanRelayPortStore(paths, logger));
        var derived = await withoutHistory.GetOrCreateClientRelayAsync(
            peer, Guid.NewGuid().ToString("N"), [route], 41000);
        Assert.Equal(firstPort, derived.LocalPort);
    }

    private sealed class UnreachablePeerConnector : ILanRelayPeerConnector
    {
        public Task<TcpClient> ConnectAsync(
            LanRelayTarget target,
            int remotePort,
            CancellationToken token) =>
            throw new SocketException((int)SocketError.HostUnreachable);
    }
}
