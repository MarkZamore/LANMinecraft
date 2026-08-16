using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using Minecraft;
using static Minecraft.Tests.NetworkTestData;

namespace Minecraft.Tests;

public sealed class NetworkProtocolTests
{
    [Fact]
    public void DiscoveryProtocol_IsVersionSix()
    {
        Assert.Equal(6, PeerDiscoveryService.ProtocolVersion);
    }

    [Fact]
    public void DiscoveryPayload_DoesNotSerializeObservedOrLocalRouteData()
    {
        var announcement = new PeerAnnouncement
        {
            ProtocolVersion = PeerDiscoveryService.ProtocolVersion,
            IdentityId = Guid.NewGuid().ToString("D"),
            PlayerName = "Player",
            NetworkAddress = "127.0.0.1",
            LocalAddress = "10.60.0.1",
            LocalInterfaceId = "private-interface-guid"
        };

        var json = JsonSerializer.Serialize(announcement, new JsonSerializerOptions(
            JsonSerializerDefaults.Web));

        Assert.DoesNotContain("127.0.0.1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("10.60.0.1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-interface-guid", json, StringComparison.Ordinal);
        Assert.DoesNotContain("vpnIp", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("providerId", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(announcement.IdentityId, json, StringComparison.Ordinal);
    }

    [Fact]
    public void PacketInformation_AcceptsOnlySelectedAddressOrDiscoveryGroup()
    {
        var endpoint = Endpoint("selected-interface", "10.61.0.1", 61);

        Assert.True(PeerDiscoveryService.IsAllowedLocalDestination(
            IPAddress.Parse("10.61.0.1"),
            endpoint));
        Assert.True(PeerDiscoveryService.IsAllowedLocalDestination(
            IPAddress.Parse(endpoint.BroadcastAddress),
            endpoint));
        Assert.True(PeerDiscoveryService.IsAllowedLocalDestination(
            IPAddress.Parse("239.255.77.67"),
            endpoint));
        Assert.False(PeerDiscoveryService.IsAllowedLocalDestination(
            IPAddress.Parse("10.61.0.2"),
            endpoint));
        Assert.True(PeerDiscoveryService.IsPacketOnSelectedEndpoint(
            AddressFamily.InterNetwork,
            endpoint.InterfaceIndex,
            IPAddress.Parse(endpoint.BroadcastAddress),
            endpoint));
        Assert.False(PeerDiscoveryService.IsPacketOnSelectedEndpoint(
            AddressFamily.InterNetwork,
            endpoint.InterfaceIndex + 1,
            IPAddress.Parse(endpoint.BroadcastAddress),
            endpoint));
    }

    [Fact]
    public async Task FirstDiscoveryStart_PreservesAndMigratesLegacyPeerCache()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"minecraft-network-cache-test-{Guid.NewGuid():N}");
        var paths = new AppPaths(root);
        paths.Ensure();
        var identityId = Guid.NewGuid().ToString("D");
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        File.WriteAllText(
            paths.NetworkPeersFile,
            JsonSerializer.Serialize(new KnownPeerCache
            {
                SchemaVersion = 4,
                Peers =
                [
                    new KnownPeerIdentityRecord
                    {
                        IdentityId = identityId,
                        Endpoints =
                        [
                            new KnownPeerEndpointRecord
                            {
                                Address = "10.81.0.25",
                                IsConfirmed = true
                            }
                        ]
                    }
                ]
            }, jsonOptions));
        var routes = new PeerRouteResolver();
        await using var discovery = new PeerDiscoveryService(
            paths,
            new Logger(paths.LogFile),
            new FakeSelectedNetworkTransport(),
            routes);

        try
        {
            await discovery.StartAsync(
                new NetworkEnvironmentSnapshot(),
                _ => throw new InvalidOperationException("No endpoint was configured."));

            Assert.Empty(routes.GetSendCandidates(identityId));
            var imported = Assert.Single(routes.GetDiscoveryBatch(
                Endpoint("selected-interface", "10.81.0.1", 81),
                cursor: 0,
                maxCount: 10).Candidates).Endpoint;
            Assert.Equal("10.81.0.25", imported.Address);
            Assert.False(imported.IsConfirmed);

            await discovery.StopAsync();

            var migrated = JsonSerializer.Deserialize<KnownPeerCache>(
                File.ReadAllText(paths.NetworkPeersFile),
                jsonOptions);
            Assert.NotNull(migrated);
            Assert.Equal(KnownPeerCache.CurrentSchemaVersion, migrated.SchemaVersion);
            Assert.Single(migrated.Peers);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(offset),
                token);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }
            offset += read;
        }
    }
}
