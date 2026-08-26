using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Shutdown and refusal behaviour of the receiving side. The listener itself
/// now belongs to the connection router, so what is pinned here is what the
/// transfer service still owns: an in-flight transfer must not be cut off, and
/// a disposed service must never accept another one.
/// </summary>
public sealed class WorldTransferLifecycleTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task DisposeAsync_WaitsForAnInFlightTransfer_ThenRefusesNewOnes()
    {
        await using var fixture = ServiceFixture.Create(TimeSpan.FromSeconds(30));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await fixture.StartAcceptingAsync(timeout.Token);

        var connection = await fixture.ConnectAsSenderAsync(timeout.Token);
        await PortableProtocol.WriteJsonAsync(
            connection.Stream, NewPrepareHeader(), JsonOptions, timeout.Token);
        var preparing = PortableProtocol.Deserialize<WorldTransferAck>(
            await PortableProtocol.ReadFrameAsync(connection.Stream, timeout.Token), JsonOptions);
        Assert.NotNull(preparing);
        Assert.True(preparing.Ok);
        await connection.DisposeAsync();

        // Disposal unwinds the in-flight receive instead of returning while it
        // still holds the transfer gate, and the service never takes another.
        await fixture.Service.DisposeAsync().AsTask().WaitAsync(timeout.Token);

        Assert.False(fixture.Service.IsOperationActive);
        Assert.Throws<ObjectDisposedException>(
            () => fixture.Service.UseSettingsForIncomingTransfers(fixture.Settings));
        await fixture.Service.DisposeAsync();
    }

    [Fact]
    public async Task UnexpectedIncomingFailure_IsObserved_AndTheServiceStaysUsable()
    {
        await using var fixture = ServiceFixture.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
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
            if (line.Contains("Incoming world transfer from", StringComparison.Ordinal))
            {
                failureObserved.TrySetResult();
            }
        };

        await fixture.StartAcceptingAsync(timeout.Token);
        await using (var connection = await fixture.ConnectAsSenderAsync(timeout.Token))
        {
            await PortableProtocol.WriteJsonAsync(
                connection.Stream,
                new
                {
                    Protocol = WorldTransferService.ProtocolName,
                    ProtocolVersion = WorldTransferService.ProtocolVersion + 1,
                    MessageType = WorldTransferService.ProbeMessageType
                },
                JsonOptions,
                timeout.Token);
            await failureObserved.Task.WaitAsync(timeout.Token);
        }

        Assert.False(fixture.Service.IsOperationActive);
        fixture.Service.StopAcceptingIncomingTransfers();
    }

    /// <summary>
    /// Between "Steam went away" and "the window says so" a connection can still
    /// arrive; it must be dropped rather than half-processed.
    /// </summary>
    [Fact]
    public async Task WithIncomingTransfersStopped_AConnectionIsDropped()
    {
        await using var fixture = ServiceFixture.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var refused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Logger.LineWritten += line =>
        {
            if (line.Contains("incoming transfers are disabled", StringComparison.Ordinal))
            {
                refused.TrySetResult();
            }
        };

        await fixture.StartAcceptingAsync(timeout.Token);
        fixture.Service.StopAcceptingIncomingTransfers();

        await using var connection = await fixture.ConnectAsSenderAsync(timeout.Token);
        await PortableProtocol.WriteJsonAsync(
            connection.Stream, NewPrepareHeader(), JsonOptions, timeout.Token);

        await refused.Task.WaitAsync(timeout.Token);
        Assert.False(fixture.Service.IsOperationActive);
    }

    private static WorldTransferHeader NewPrepareHeader() => new()
    {
        Protocol = WorldTransferService.ProtocolName,
        ProtocolVersion = WorldTransferService.ProtocolVersion,
        MessageType = WorldTransferService.PrepareMessageType,
        TransferId = Guid.NewGuid().ToString("N"),
        SenderName = "LifecycleTest",
        SenderIdentityId = Guid.NewGuid().ToString("D"),
        SenderSteamId64 = ServiceFixture.SenderSteamId.ToString(
            System.Globalization.CultureInfo.InvariantCulture),
        SenderIdentityName = "LifecycleTest",
        Size = 0,
        FileName = "world.zip",
        WorldName = "LifecycleWorld"
    };

    /// <summary>
    /// One receiver on an in-memory peer network, reachable the way a friend
    /// reaches it: a connection through the transport, demultiplexed by the
    /// router. The VPN-era fixture bound a loopback TCP port for the same job.
    /// </summary>
    private sealed class ServiceFixture : IAsyncDisposable
    {
        internal const ulong ReceiverSteamId = 76561198000000001;
        internal const ulong SenderSteamId = 76561198000000002;

        private readonly string _root;
        private readonly WaypointSyncService _waypoints;
        private readonly SkinService _skins;
        private readonly PackRuntimeService _packRuntimes;
        private readonly PeerConnectionRouter _router;
        private readonly InMemoryPeerTransport _senderTransport;

        private ServiceFixture(
            string root,
            AppPaths paths,
            Logger logger,
            WaypointSyncService waypoints,
            SkinService skins,
            PackRuntimeService packRuntimes,
            PeerConnectionRouter router,
            InMemoryPeerTransport senderTransport,
            WorldTransferService service)
        {
            _root = root;
            Paths = paths;
            Logger = logger;
            _waypoints = waypoints;
            _skins = skins;
            _packRuntimes = packRuntimes;
            _router = router;
            _senderTransport = senderTransport;
            Service = service;
        }

        public AppPaths Paths { get; }
        public Logger Logger { get; }
        public WorldTransferService Service { get; }
        public AppSettings Settings { get; } = new() { PlayerName = "LifecycleTest" };

        public static ServiceFixture Create(TimeSpan? idleTimeout = null)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"minecraft-world-transfer-lifecycle-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var paths = new AppPaths(root);
            paths.Ensure();
            var logger = new Logger(paths.LogFile);
            var network = new InMemoryPeerNetwork();
            var receiverTransport = network.CreateTransport(ReceiverSteamId, "Receiver");
            var senderTransport = network.CreateTransport(SenderSteamId, "Sender");
            network.MakeFriends(ReceiverSteamId, SenderSteamId);
            var metadata = new WorldMetadataService();
            var identity = TestIdentity.CreateBound(ReceiverSteamId, "Receiver");
            var profiles = new WorldPlayerProfileService(paths, logger);
            var waypoints = new WaypointSyncService(paths, logger, metadata, receiverTransport);
            var skins = new SkinService(paths, logger, receiverTransport);
            var identityAdapter = new PortableIdentityAdapterService(paths, logger);
            var packInstances = new PackInstanceService(paths, logger);
            var packRuntimes = new PackRuntimeService(paths, logger);
            var minecraft = new MinecraftProcessService(
                paths, logger, identity, identityAdapter, profiles, packInstances, packRuntimes, waypoints, skins,
                new PortableIdentityRegistryService(paths, logger));
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
                receiverTransport,
                idleTimeout is null
                    ? new WorldTransferRuntimeOptions()
                    : new WorldTransferRuntimeOptions { PeerIdleTimeout = idleTimeout.Value });
            var router = new PeerConnectionRouter(receiverTransport, logger);
            router.RegisterFallback(service);
            return new ServiceFixture(
                root, paths, logger, waypoints, skins, packRuntimes, router, senderTransport, service);
        }

        public async Task StartAcceptingAsync(CancellationToken token)
        {
            await _router.StartAsync(token);
            Service.UseSettingsForIncomingTransfers(Settings);
        }

        public async Task<PeerConnection> ConnectAsSenderAsync(CancellationToken token)
        {
            Assert.True(SteamId64.TryFrom(ReceiverSteamId, out var receiver));
            return await _senderTransport.ConnectAsync(
                receiver, WorldTransferService.ProtocolName, token);
        }

        public async ValueTask DisposeAsync()
        {
            await _router.DisposeAsync();
            await Service.DisposeAsync();
            await _waypoints.DisposeAsync();
            await _skins.DisposeAsync();
            _packRuntimes.Dispose();
            TempTree.Delete(_root);
        }
    }
}
