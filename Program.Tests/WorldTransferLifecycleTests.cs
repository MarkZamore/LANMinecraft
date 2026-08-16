using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class WorldTransferLifecycleTests
{
    [Fact]
    public async Task DisposeAsync_WaitsForInFlightStart_ThenPreventsRestart()
    {
        await using var fixture = ServiceFixture.Create(GetFreeTcpPort());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        fixture.Network.BlockNextSnapshot();

        var start = Task.Run(
            () => fixture.Service.StartListenerAsync(fixture.Settings, timeout.Token),
            timeout.Token);
        await fixture.Network.SnapshotEntered.Task.WaitAsync(timeout.Token);

        var dispose = fixture.Service.DisposeAsync().AsTask();
        await Task.Delay(100, timeout.Token);
        Assert.False(dispose.IsCompleted);

        fixture.Network.ReleaseSnapshot();
        await start.WaitAsync(timeout.Token);
        await dispose.WaitAsync(timeout.Token);

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fixture.Service.StartListenerAsync(fixture.Settings, timeout.Token));
        await fixture.Service.DisposeAsync();

        using var listener = new TcpListener(IPAddress.Loopback, fixture.Port);
        listener.Start();
    }

    [Fact]
    public async Task UnexpectedIncomingFailure_IsObserved_AndStopStillCompletes()
    {
        await using var fixture = ServiceFixture.Create(GetFreeTcpPort());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var failureObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var injectUnexpectedFailure = 1;
        fixture.Logger.LineWritten += line =>
        {
            if (line.Contains("World receive failed:", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref injectUnexpectedFailure, 0) != 0)
            {
                throw new InvalidOperationException("Injected logger callback failure.");
            }
            if (line.Contains("Incoming world transfer failed:", StringComparison.Ordinal))
            {
                failureObserved.TrySetResult();
            }
        };

        await fixture.Service.StartListenerAsync(fixture.Settings, timeout.Token);
        using (var client = new TcpClient(AddressFamily.InterNetwork))
        {
            await client.ConnectAsync(IPAddress.Loopback, fixture.Port, timeout.Token);
            await PortableProtocol.WriteJsonAsync(
                client.GetStream(),
                new
                {
                    Protocol = WorldTransferService.ProtocolName,
                    ProtocolVersion = WorldTransferService.ProtocolVersion + 1,
                    MessageType = WorldTransferService.ProbeMessageType
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
                timeout.Token);
            await failureObserved.Task.WaitAsync(timeout.Token);
        }

        await fixture.Service.StopListenerAsync().WaitAsync(timeout.Token);
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class ServiceFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly WaypointSyncService _waypoints;
        private readonly SkinService _skins;
        private readonly PackRuntimeService _packRuntimes;

        private ServiceFixture(
            string root,
            int port,
            Logger logger,
            BlockingNetworkTransport network,
            WaypointSyncService waypoints,
            SkinService skins,
            PackRuntimeService packRuntimes,
            WorldTransferService service)
        {
            _root = root;
            Port = port;
            Logger = logger;
            Network = network;
            _waypoints = waypoints;
            _skins = skins;
            _packRuntimes = packRuntimes;
            Service = service;
        }

        public int Port { get; }
        public Logger Logger { get; }
        public BlockingNetworkTransport Network { get; }
        public WorldTransferService Service { get; }
        public AppSettings Settings { get; } = new() { PlayerName = "LifecycleTest" };

        public static ServiceFixture Create(int port)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"minecraft-world-transfer-lifecycle-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var paths = new AppPaths(root);
            paths.Ensure();
            var logger = new Logger(paths.LogFile);
            var endpoint = new NetworkEndpointInfo
            {
                InterfaceId = "loopback-test",
                InterfaceIndex = 0,
                InterfaceName = "loopback-test",
                NetworkAddress = IPAddress.Loopback.ToString(),
                PrefixLength = 8,
                BroadcastAddress = ""
            };
            var network = new BlockingNetworkTransport(endpoint);
            var routes = new PeerRouteResolver();
            var metadata = new WorldMetadataService();
            var identity = TestIdentity.CreateBound(paths);
            var profiles = new WorldPlayerProfileService(paths, logger);
            var waypoints = new WaypointSyncService(paths, logger, metadata, network, routes);
            var skins = new SkinService(paths, logger, network, routes);
            var identityAdapter = new PortableIdentityAdapterService(paths, logger);
            var packInstances = new PackInstanceService(paths, logger);
            var packRuntimes = new PackRuntimeService(paths, logger);
            var minecraft = new MinecraftProcessService(
                paths,
                logger,
                identity,
                identityAdapter,
                profiles,
                packInstances,
                packRuntimes,
                waypoints,
                skins);
            var service = new WorldTransferService(
                paths,
                logger,
                minecraft,
                new SettingsService(paths),
                metadata,
                identity,
                profiles,
                waypoints,
                skins,
                network,
                routes,
                new WorldTransferRuntimeOptions
                {
                    Port = port,
                    ListenAddress = IPAddress.Loopback
                });
            return new ServiceFixture(
                root,
                port,
                logger,
                network,
                waypoints,
                skins,
                packRuntimes,
                service);
        }

        public async ValueTask DisposeAsync()
        {
            Network.ReleaseSnapshot();
            await Service.DisposeAsync();
            await _waypoints.DisposeAsync();
            await _skins.DisposeAsync();
            _packRuntimes.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class BlockingNetworkTransport(
        NetworkEndpointInfo endpoint) : ISelectedNetworkTransport
    {
        private readonly ManualResetEventSlim _releaseSnapshot = new(false);
        private int _blockNextSnapshot;

        public TaskCompletionSource SnapshotEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public NetworkEnvironmentSnapshot GetSnapshot()
        {
            if (Interlocked.Exchange(ref _blockNextSnapshot, 0) != 0)
            {
                SnapshotEntered.TrySetResult();
                _releaseSnapshot.Wait();
            }
            return new NetworkEnvironmentSnapshot
            {
                Endpoints = [endpoint],
                PrimaryEndpoint = endpoint
            };
        }

        public TcpClient CreateBoundTcpClient(
            IPAddress remoteAddress,
            string? localAddress = null,
            string? localInterfaceId = null) =>
            throw new InvalidOperationException("No outgoing connection was expected.");

        public UdpClient CreateBoundUdpClient(
            NetworkEndpointInfo networkEndpoint,
            int port,
            bool reuseAddress) =>
            throw new InvalidOperationException("No datagram socket was expected.");

        public Task<IReadOnlyList<IPAddress>> GetDynamicPeerTargetsAsync(
            NetworkEnvironmentSnapshot snapshot,
            CancellationToken token) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([]);

        public void BlockNextSnapshot() =>
            Interlocked.Exchange(ref _blockNextSnapshot, 1);

        public void ReleaseSnapshot() => _releaseSnapshot.Set();
    }
}
