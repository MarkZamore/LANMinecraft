using System.Net;
using Minecraft;
using static Minecraft.Tests.NetworkTestData;

namespace Minecraft.Tests;

public sealed class PeerRouteResolverTests
{
    [Fact]
    public void Announcement_UsesObservedPacketSource_NotDeclaredMetadata()
    {
        var identityId = Guid.NewGuid().ToString("D");
        var local = Endpoint("selected-interface", "10.20.0.1", 20);
        var announcement = new PeerAnnouncement
        {
            IdentityId = identityId,
            PlayerName = "Host",
            NetworkAddress = "198.51.100.99",
            LocalAddress = "198.51.100.100",
            LocalInterfaceId = "remote-declared-interface"
        };
        var resolver = new PeerRouteResolver();

        resolver.UpsertFromAnnouncement(
            announcement,
            IPAddress.Parse("10.20.0.25"),
            local);

        var route = Assert.Single(resolver.GetSendCandidates(identityId));
        Assert.Equal("10.20.0.25", route.Address);
        Assert.Equal(local.NetworkAddress, route.LocalAddress);
        Assert.Equal(local.InterfaceId, route.LocalInterfaceId);
        Assert.True(route.IsObserved);
        Assert.True(route.IsConfirmed);
        Assert.DoesNotContain(
            resolver.GetSendCandidates(identityId),
            candidate => candidate.Address == announcement.NetworkAddress);
    }

    [Fact]
    public void LoopbackPacketSource_IsNeverAcceptedAsPeerRoute()
    {
        var identityId = Guid.NewGuid().ToString("D");
        var resolver = new PeerRouteResolver();

        resolver.UpsertFromAnnouncement(
            new PeerAnnouncement { IdentityId = identityId, PlayerName = "Host" },
            IPAddress.Loopback,
            Endpoint("selected-interface", "10.20.0.1", 20));

        Assert.Empty(resolver.GetSendCandidates(identityId));
        Assert.False(resolver.IsKnownEndpoint(
            identityId,
            IPAddress.Loopback,
            "selected-interface"));
    }

    [Fact]
    public void LegacyV4Cache_IsImportedAsUnconfirmedProbe()
    {
        var identityId = Guid.NewGuid().ToString("D");
        var resolver = new PeerRouteResolver();
        resolver.Load(new KnownPeerCache
        {
            SchemaVersion = 4,
            Peers =
            [
                new KnownPeerIdentityRecord
                {
                    IdentityId = identityId,
                    PlayerName = "Cached",
                    Endpoints =
                    [
                        new KnownPeerEndpointRecord
                        {
                            Address = "10.30.0.25",
                            LocalAddress = "10.99.0.1",
                            LocalInterfaceId = "legacy-interface",
                            IsObserved = true,
                            IsConfirmed = true,
                            LastSuccessUtc = DateTimeOffset.UtcNow
                        }
                    ]
                }
            ]
        });

        Assert.Empty(resolver.GetSendCandidates(identityId));
        var route = Assert.Single(resolver.GetDiscoveryBatch(
            Endpoint("new-selected-interface", "10.30.0.1", 30),
            cursor: 0,
            maxCount: 10).Candidates).Endpoint;
        Assert.Equal("10.30.0.25", route.Address);
        Assert.Empty(route.LocalAddress);
        Assert.Empty(route.LocalInterfaceId);
        Assert.False(route.IsObserved);
        Assert.False(route.IsConfirmed);
        Assert.Equal(default, route.LastSuccessUtc);

        var probe = resolver.GetDiscoveryBatch(
            Endpoint("new-selected-interface", "10.30.0.1", 30),
            cursor: 0,
            maxCount: 10);
        Assert.Equal("10.30.0.25", Assert.Single(probe.Candidates).Endpoint.Address);
        Assert.Equal(5, resolver.Export().SchemaVersion);
    }

    [Fact]
    public void DiscoveryCandidates_AreScopedToReceivingLocalInterface()
    {
        var identityId = Guid.NewGuid().ToString("D");
        var localA = Endpoint("interface-a", "10.40.0.1", 40);
        var localB = Endpoint("interface-b", "10.50.0.1", 50);
        var remoteA = IPAddress.Parse("10.40.0.25");
        var remoteB = IPAddress.Parse("10.50.0.25");
        var resolver = new PeerRouteResolver();
        var announcement = new PeerAnnouncement
        {
            IdentityId = identityId,
            PlayerName = "Host"
        };

        resolver.UpsertFromAnnouncement(announcement, remoteA, localA);
        resolver.UpsertFromAnnouncement(announcement, remoteB, localB);

        var batchA = resolver.GetDiscoveryBatch(localA, cursor: 0, maxCount: 10);
        var candidateA = Assert.Single(batchA.Candidates);
        Assert.Equal(remoteA.ToString(), candidateA.Endpoint.Address);
        Assert.Equal(localA.InterfaceId, candidateA.Endpoint.LocalInterfaceId);

        var batchB = resolver.GetDiscoveryBatch(localB, cursor: 0, maxCount: 10);
        Assert.Equal(remoteB.ToString(), Assert.Single(batchB.Candidates).Endpoint.Address);
        Assert.True(resolver.IsKnownEndpoint(identityId, remoteA, localA.InterfaceId));
        Assert.False(resolver.IsKnownEndpoint(identityId, remoteA, localB.InterfaceId));
    }

    [Fact]
    public void UnconfirmedLegacyProbe_ExpiresUsingInjectedClock()
    {
        var identityId = Guid.NewGuid().ToString("D");
        var clock = new FakeNetworkClock(new DateTimeOffset(
            2026,
            7,
            26,
            12,
            0,
            0,
            TimeSpan.Zero));
        var resolver = new PeerRouteResolver(clock);
        resolver.Load(new KnownPeerCache
        {
            SchemaVersion = 4,
            Peers =
            [
                new KnownPeerIdentityRecord
                {
                    IdentityId = identityId,
                    Endpoints = [new KnownPeerEndpointRecord { Address = "10.70.0.25" }]
                }
            ]
        });

        Assert.Empty(resolver.GetSendCandidates(identityId));
        Assert.Single(resolver.GetDiscoveryBatch(
            Endpoint("selected-interface", "10.70.0.1", 70),
            cursor: 0,
            maxCount: 10).Candidates);

        clock.UtcNow += TimeSpan.FromMinutes(16);

        Assert.Empty(resolver.GetSendCandidates(identityId));
        Assert.Empty(resolver.GetDiscoveryBatch(
            Endpoint("selected-interface", "10.70.0.1", 70),
            cursor: 0,
            maxCount: 10).Candidates);
    }

    [Fact]
    public void UnhealthyRoute_FailsOverToAnotherObservedAddressOnSelectedInterface()
    {
        var now = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeNetworkClock(now);
        var resolver = new PeerRouteResolver(clock);
        var identityId = Guid.NewGuid().ToString("D");
        var local = Endpoint("selected-interface", "10.90.0.1", 90);
        var announcement = new PeerAnnouncement { IdentityId = identityId };
        var firstAddress = IPAddress.Parse("10.90.0.25");
        var secondAddress = IPAddress.Parse("10.90.0.26");

        resolver.UpsertFromAnnouncement(announcement, firstAddress, local);
        clock.UtcNow += TimeSpan.FromSeconds(1);
        resolver.UpsertFromAnnouncement(announcement, secondAddress, local);
        var failed = resolver.GetSendCandidates(identityId)
            .Single(candidate => candidate.Address == secondAddress.ToString());

        resolver.MarkEndpointHealthy(identityId, failed);
        resolver.MarkEndpointUnhealthy(identityId, failed);

        var ordered = resolver.GetSendCandidates(identityId);
        Assert.Equal(firstAddress.ToString(), ordered[0].Address);
        Assert.True(ordered[1].IsConfirmed);
        Assert.Equal(1, ordered[1].FailureScore);
    }
}
