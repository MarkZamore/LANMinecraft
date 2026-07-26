using System.Net;
using System.Net.Sockets;
using System.Text;
using Minecraft;

namespace Minecraft.Tests;

public sealed class PeerSupportLogServiceTests
{
    [Fact]
    public async Task ReceiverRestart_RestoresProtocolStreamsBeforeNextData()
    {
        using var fixture = new TemporaryPortableRoot();
        var descriptor = new SupportLogSessionDescriptor(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Remote",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>());
        var firstStorage = new SupportLogStorage(fixture.Paths);
        var first = await firstStorage.CreateSessionAsync(descriptor);
        var receiverName = await first.RegisterSourceAsync(
            new SupportLogStreamDescriptor(
                "stream_0000100",
                SupportLogSourceKind.Game,
                "latest.log"));
        await first.CommitAcceptedFrameAsync(
            1,
            new string('A', 64),
            _ => Task.CompletedTask);

        var restartedStorage = new SupportLogStorage(fixture.Paths);
        var resumed = await restartedStorage.CreateSessionAsync(descriptor);
        var payload = Encoding.UTF8.GetBytes("continued after reconnect\n");
        await PeerSupportLogService.AppendResumedFrameForTestingAsync(
            resumed,
            new PeerSupportFrame(
                PeerSupportFrameType.Data,
                100,
                2,
                1,
                payload));

        Assert.Contains(
            "continued after reconnect",
            await File.ReadAllTextAsync(
                Path.Combine(resumed.SessionDirectory, receiverName)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncomingPeerValidation_RequiresObservedIdentityRouteAndSelectedInterface()
    {
        using var fixture = new TemporaryPortableRoot();
        using var remoteCertificate = PeerSupportCertificate.CreateEphemeral();
        var now = DateTimeOffset.UtcNow;
        var localIdentity = Guid.NewGuid();
        var remoteIdentity = Guid.NewGuid();
        var localAddress = IPAddress.Parse("10.80.0.1");
        var remoteAddress = IPAddress.Parse("10.80.0.2");
        var endpoint = NetworkTestData.Endpoint(
            "software-interface",
            localAddress.ToString(),
            interfaceIndex: 42);
        var snapshot = new NetworkEnvironmentSnapshot
        {
            CapturedAtUtc = now,
            Endpoints = [endpoint],
            PrimaryEndpoint = endpoint
        };
        var network = new SnapshotNetworkTransport(snapshot);
        var clock = new NetworkTestData.FakeNetworkClock(now);
        var routes = new PeerRouteResolver(clock);
        var announcement = new PeerAnnouncement
        {
            ProtocolVersion = PeerDiscoveryService.ProtocolVersion,
            IdentityId = remoteIdentity.ToString("D"),
            PlayerName = "Remote",
            NetworkAddress = remoteAddress.ToString(),
            LocalAddress = localAddress.ToString(),
            LocalInterfaceId = endpoint.InterfaceId,
            DiagnosticLogProtocolVersion = PeerSupportProtocol.ProtocolVersion,
            DiagnosticTlsFingerprint = remoteCertificate.Fingerprint
        };
        routes.UpsertFromAnnouncement(announcement, remoteAddress, endpoint);

        await using var service = CreateService(
            fixture.Paths,
            network,
            routes,
            localIdentity,
            now);
        service.ObservePeer(announcement);

        var hello = new PeerSupportHello
        {
            SessionId = Guid.NewGuid(),
            SenderIdentityId = remoteIdentity.ToString("D"),
            RecipientIdentityId = localIdentity.ToString("D"),
            StartedAtUtc = now
        };
        var context = new PortableConnectionContext(
            remoteAddress,
            50000,
            localAddress,
            WorldTransferService.TransferPort,
            endpoint.InterfaceId,
            endpoint.InterfaceIndex,
            now);

        service.ValidateIncomingPeerForTesting(
            hello,
            remoteCertificate.Fingerprint,
            context);

        Assert.Throws<InvalidDataException>(() =>
            service.ValidateIncomingPeerForTesting(
                hello,
                new string('0', 64),
                context));
        Assert.Throws<InvalidDataException>(() =>
            service.ValidateIncomingPeerForTesting(
                hello with { SenderIdentityId = Guid.NewGuid().ToString("D") },
                remoteCertificate.Fingerprint,
                context));
        Assert.Throws<InvalidDataException>(() =>
            service.ValidateIncomingPeerForTesting(
                hello,
                remoteCertificate.Fingerprint,
                context with { RemoteAddress = IPAddress.Parse("10.80.0.3") }));
        Assert.Throws<InvalidDataException>(() =>
            service.ValidateIncomingPeerForTesting(
                hello,
                remoteCertificate.Fingerprint,
                context with { LocalInterfaceIndex = 43 }));
        Assert.Throws<InvalidDataException>(() =>
            service.ValidateIncomingPeerForTesting(
                hello,
                remoteCertificate.Fingerprint,
                context with { RemoteAddress = IPAddress.Loopback }));
    }

    [Fact]
    public async Task SelectedNetworkChange_ImmediatelyResetsInMemoryTarget()
    {
        using var fixture = new TemporaryPortableRoot();
        using var remoteCertificate = PeerSupportCertificate.CreateEphemeral();
        var now = DateTimeOffset.UtcNow;
        var localIdentity = Guid.NewGuid();
        var remoteIdentity = Guid.NewGuid();
        var endpoint = NetworkTestData.Endpoint(
            "software-interface",
            "10.81.0.1",
            interfaceIndex: 10);
        var snapshot = new NetworkEnvironmentSnapshot
        {
            CapturedAtUtc = now,
            Endpoints = [endpoint],
            PrimaryEndpoint = endpoint
        };
        var network = new SnapshotNetworkTransport(snapshot);
        var routes = new PeerRouteResolver(
            new NetworkTestData.FakeNetworkClock(now));
        var announcement = new PeerAnnouncement
        {
            ProtocolVersion = PeerDiscoveryService.ProtocolVersion,
            IdentityId = remoteIdentity.ToString("D"),
            PlayerName = "Remote",
            NetworkAddress = "10.81.0.2",
            LocalAddress = endpoint.NetworkAddress,
            LocalInterfaceId = endpoint.InterfaceId,
            DiagnosticLogProtocolVersion = PeerSupportProtocol.ProtocolVersion,
            DiagnosticTlsFingerprint = remoteCertificate.Fingerprint
        };
        routes.UpsertFromAnnouncement(
            announcement,
            IPAddress.Parse(announcement.NetworkAddress),
            endpoint);

        await using var service = CreateService(
            fixture.Paths,
            network,
            routes,
            localIdentity,
            now);
        service.ObservePeer(announcement);
        await service.SetTargetAsync(new DiagnosticLogTargetOption(
            remoteIdentity.ToString("D"),
            $"Remote - {announcement.NetworkAddress}",
            announcement.NetworkAddress,
            remoteCertificate.Fingerprint));
        Assert.Equal(remoteIdentity.ToString("D"), service.CurrentTargetIdentityId);

        var additional = NetworkTestData.Endpoint(
            "additional-interface",
            "10.90.0.1",
            interfaceIndex: 99);
        network.Snapshot = new NetworkEnvironmentSnapshot
        {
            CapturedAtUtc = now.AddMilliseconds(500),
            Endpoints = [endpoint],
            AvailableEndpoints = [endpoint, additional],
            PrimaryEndpoint = endpoint
        };
        await service.UpdateNetworkContextAsync(network.Snapshot);
        Assert.Equal(
            remoteIdentity.ToString("D"),
            service.CurrentTargetIdentityId);

        var replacement = NetworkTestData.Endpoint(
            "other-interface",
            "10.82.0.1",
            interfaceIndex: 11);
        network.Snapshot = new NetworkEnvironmentSnapshot
        {
            CapturedAtUtc = now.AddSeconds(1),
            Endpoints = [replacement],
            PrimaryEndpoint = replacement
        };
        await service.UpdateNetworkContextAsync(network.Snapshot);

        Assert.Empty(service.CurrentTargetIdentityId);
    }

    private static PeerSupportLogService CreateService(
        AppPaths paths,
        ISelectedNetworkTransport network,
        PeerRouteResolver routes,
        Guid localIdentity,
        DateTimeOffset now) =>
        new(
            paths,
            network,
            routes,
            () => (localIdentity.ToString("D"), "Local"),
            () => null,
            _ => Task.FromResult(new SupportEnvironmentSnapshot(
                now,
                "22",
                "22",
                ".NET",
                "Windows",
                "X64",
                "Java",
                "Pack",
                "hash",
                [],
                network.GetSnapshot().PrimaryEndpoint,
                [],
                new KnownPeerCache(),
                new Dictionary<string, string>(),
                string.Empty,
                string.Empty,
                string.Empty)),
            () => new SupportNetworkMetrics(
                now,
                network.GetSnapshot().PrimaryEndpoint?.InterfaceId ?? string.Empty,
                network.GetSnapshot().PrimaryEndpoint?.NetworkAddress ?? string.Empty,
                network.GetSnapshot().Fingerprint,
                1,
                1,
                [],
                new KnownPeerCache(),
                true,
                false,
                false,
                0,
                false,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                new Dictionary<string, string>()));

    private sealed class SnapshotNetworkTransport(
        NetworkEnvironmentSnapshot snapshot) : ISelectedNetworkTransport
    {
        public NetworkEnvironmentSnapshot Snapshot { get; set; } = snapshot;

        public NetworkEnvironmentSnapshot GetSnapshot() => Snapshot;

        public TcpClient CreateBoundTcpClient(
            IPAddress remoteAddress,
            string? localAddress = null,
            string? localInterfaceId = null) =>
            throw new SocketException((int)SocketError.NetworkUnreachable);

        public UdpClient CreateBoundUdpClient(
            NetworkEndpointInfo endpoint,
            int port,
            bool reuseAddress) =>
            throw new InvalidOperationException();

        public Task<IReadOnlyList<IPAddress>> GetDynamicPeerTargetsAsync(
            NetworkEnvironmentSnapshot value,
            CancellationToken token) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([]);
    }

    private sealed class TemporaryPortableRoot : IDisposable
    {
        public TemporaryPortableRoot()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "MinecraftPeerSupportTests",
                Guid.NewGuid().ToString("N"));
            Paths = new AppPaths(Root);
            Paths.Ensure();
        }

        public string Root { get; }
        public AppPaths Paths { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
