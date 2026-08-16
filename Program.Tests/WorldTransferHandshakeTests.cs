using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class WorldTransferHandshakeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PrepareHandshake_ThenSilence_ReleasesTheTransferGate()
    {
        await using var fixture = ServiceFixture.Create(
            GetFreeTcpPort(), TimeSpan.FromMilliseconds(600));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await fixture.Service.StartListenerAsync(fixture.Settings, timeout.Token);

        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, fixture.Port, timeout.Token);
        var stream = client.GetStream();
        await PortableProtocol.WriteJsonAsync(stream, NewPrepareHeader(), JsonOptions, timeout.Token);

        var ack = PortableProtocol.Deserialize<WorldTransferAck>(
            await PortableProtocol.ReadFrameAsync(stream, timeout.Token), JsonOptions);
        Assert.NotNull(ack);
        Assert.True(ack.Ok);
        Assert.Equal("Preparing", ack.Stage);
        Assert.True(fixture.Service.IsOperationActive);

        // The peer now goes silent while keeping the socket open, which is what
        // a hung launcher looks like. The gate has to come back on its own.
        var rejected = PortableProtocol.Deserialize<WorldTransferAck>(
            await PortableProtocol.ReadFrameAsync(stream, timeout.Token), JsonOptions);
        Assert.NotNull(rejected);
        Assert.False(rejected.Ok);
        Assert.Equal("Rejected", rejected.Stage);
        Assert.Contains("stopped responding", rejected.Message, StringComparison.OrdinalIgnoreCase);

        await WaitUntilAsync(() => !fixture.Service.IsOperationActive, timeout.Token);
        // The transaction root is deleted by a background task after the gate
        // frees, so poll instead of asserting immediately.
        await WaitUntilAsync(
            () => !Directory.EnumerateFileSystemEntries(fixture.TransfersRoot).Any(),
            timeout.Token);
    }

    [Fact]
    public async Task PrepareHandshake_ForwardsSenderProgressToTheLocalUi()
    {
        await using var fixture = ServiceFixture.Create(
            GetFreeTcpPort(), TimeSpan.FromSeconds(15));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stages = new List<WorldTransferProgress>();
        var sawCompress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.ProgressChanged += progress =>
        {
            lock (stages) stages.Add(progress);
            if (progress.Stage.Contains("Сжатие", StringComparison.Ordinal)) sawCompress.TrySetResult();
        };

        await fixture.Service.StartListenerAsync(fixture.Settings, timeout.Token);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, fixture.Port, timeout.Token);
        var stream = client.GetStream();
        var header = NewPrepareHeader();
        await PortableProtocol.WriteJsonAsync(stream, header, JsonOptions, timeout.Token);
        await PortableProtocol.ReadFrameAsync(stream, timeout.Token);

        await PortableProtocol.WriteJsonAsync(
            stream,
            new WorldTransferProgressFrame
            {
                Protocol = WorldTransferService.ProtocolName,
                ProtocolVersion = WorldTransferService.ProtocolVersion,
                MessageType = WorldTransferService.ProgressMessageType,
                TransferId = header.TransferId,
                Stage = "Compress",
                Current = 512,
                Total = 2048
            },
            JsonOptions,
            timeout.Token);

        await sawCompress.Task.WaitAsync(timeout.Token);
        WorldTransferProgress compress;
        lock (stages)
        {
            compress = stages.Last(progress => progress.Stage.Contains("Сжатие", StringComparison.Ordinal));
        }

        // The numbers shown are exactly what the peer reported - never invented.
        Assert.Equal(512, compress.Current);
        Assert.Equal(2048, compress.Total);
        Assert.True(compress.IsActive);
    }

    [Fact]
    public async Task FullTransfer_ProgressFrameBeforeCommit_IsNotMistakenForCommit()
    {
        await using var fixture = ServiceFixture.Create(
            GetFreeTcpPort(), TimeSpan.FromSeconds(15));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var becameHost = 0;
        fixture.Service.BecameHost += () => Interlocked.Increment(ref becameHost);
        await fixture.Service.StartListenerAsync(fixture.Settings, timeout.Token);

        // Build a sender-side world with valid manifests, exactly as SendWorldAsync would.
        var senderRoot = Path.Combine(
            Path.GetTempPath(), $"minecraft-world-transfer-sender-{Guid.NewGuid():N}");
        try
        {
            var senderPaths = new AppPaths(senderRoot);
            senderPaths.Ensure();
            var senderLogger = new Logger(senderPaths.LogFile);
            var senderProfiles = new WorldPlayerProfileService(senderPaths, senderLogger);
            var senderIdentity = new LocalIdentityService(senderPaths)
                .ResolveContext(new AppSettings { PlayerName = "SenderE2E" });
            var world = Path.Combine(senderPaths.Worlds, "E2EWorld");
            Directory.CreateDirectory(Path.Combine(world, "region"));
            new NbtFile("", new NbtCompoundTag()).Write(Path.Combine(world, "level.dat"));
            var regionBytes = new byte[256 * 1024];
            new Random(20260815).NextBytes(regionBytes);
            File.WriteAllBytes(Path.Combine(world, "region", "r.0.0.mca"), regionBytes);

            senderProfiles.PrepareWorldForOutgoingTransfer(world, senderIdentity);
            var playerManifestSha = senderProfiles.GetPlayerManifestHash(world);
            var senderMetadata = new WorldMetadataService();
            Assert.True(senderMetadata.TryWriteCurrentHolderMetadata(
                world, senderIdentity.IdentityId, senderIdentity.IdentityName, transferred: false));
            var waypointStore = new WaypointStoreService(
                senderMetadata, new WaypointProviderRegistry(senderLogger), senderLogger);
            waypointStore.EnsureManifest(world);
            var waypointManifestSha = waypointStore.GetManifestHash(world);

            var archivePath = Path.Combine(senderRoot, "world.zip");
            var worldSha = await WorldTransferService.CreateWorldArchiveWithHashAsync(
                world, archivePath, (_, _) => Task.CompletedTask, heartbeat: null, timeout.Token);

            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, fixture.Port, timeout.Token);
            var stream = client.GetStream();
            var prepare = NewPrepareHeader();
            await PortableProtocol.WriteJsonAsync(stream, prepare, JsonOptions, timeout.Token);
            var preparing = PortableProtocol.Deserialize<WorldTransferAck>(
                await PortableProtocol.ReadFrameAsync(stream, timeout.Token), JsonOptions);
            Assert.NotNull(preparing);
            Assert.True(preparing.Ok);

            var archiveInfo = new FileInfo(archivePath);
            await PortableProtocol.WriteJsonAsync(stream, new WorldTransferHeader
            {
                Protocol = WorldTransferService.ProtocolName,
                ProtocolVersion = WorldTransferService.ProtocolVersion,
                MessageType = WorldTransferService.TransferMessageType,
                TransferId = prepare.TransferId,
                SenderName = "SenderE2E",
                SenderIdentityId = senderIdentity.IdentityId,
                SenderIdentityName = senderIdentity.IdentityName,
                Size = archiveInfo.Length,
                WorldSha256 = worldSha,
                PlayerManifestSha256 = playerManifestSha,
                WaypointManifestSha256 = waypointManifestSha,
                FileName = "world.zip",
                WorldName = "E2EWorld"
            }, JsonOptions, timeout.Token);
            await using (var archiveStream = File.OpenRead(archivePath))
            {
                await archiveStream.CopyToAsync(stream, timeout.Token);
            }

            var ready = await ReadNonProgressAckAsync(stream, timeout.Token);
            Assert.True(ready.Ok, ready.Message);
            Assert.Equal("Ready", ready.Stage);
            Assert.Equal(worldSha, ready.WorldSha256);

            // Regression guard: a Progress frame between Ready and Commit used to
            // deserialize into WorldTransferControl with the default Command and
            // trigger installation before the sender escrowed its world.
            await PortableProtocol.WriteJsonAsync(stream, new WorldTransferProgressFrame
            {
                Protocol = WorldTransferService.ProtocolName,
                ProtocolVersion = WorldTransferService.ProtocolVersion,
                MessageType = WorldTransferService.ProgressMessageType,
                TransferId = prepare.TransferId,
                Stage = "Escrow",
                Current = 0,
                Total = 0
            }, JsonOptions, timeout.Token);
            await Task.Delay(500, timeout.Token);
            Assert.Equal(0, Volatile.Read(ref becameHost));
            Assert.False(Directory.Exists(Path.Combine(fixture.Paths.Worlds, "E2EWorld")));

            await PortableProtocol.WriteJsonAsync(stream, new WorldTransferControl
            {
                Protocol = WorldTransferService.ProtocolName,
                ProtocolVersion = WorldTransferService.ProtocolVersion,
                TransferId = prepare.TransferId,
                MessageType = WorldTransferService.ControlMessageType,
                Command = "Commit"
            }, JsonOptions, timeout.Token);

            var committed = await ReadNonProgressAckAsync(stream, timeout.Token);
            Assert.True(committed.Ok, committed.Message);
            Assert.Equal("Committed", committed.Stage);
            Assert.Equal(worldSha, committed.WorldSha256);

            var installed = Path.Combine(fixture.Paths.Worlds, "E2EWorld");
            Assert.True(Directory.Exists(installed));
            Assert.Equal(regionBytes, File.ReadAllBytes(Path.Combine(installed, "region", "r.0.0.mca")));
            // The Committed ack is written before the receiver raises BecameHost,
            // so the event may trail the ack by a few milliseconds.
            await WaitUntilAsync(() => Volatile.Read(ref becameHost) == 1, timeout.Token);
            await WaitUntilAsync(() => !fixture.Service.IsOperationActive, timeout.Token);
        }
        finally
        {
            if (Directory.Exists(senderRoot)) Directory.Delete(senderRoot, recursive: true);
        }
    }

    private static async Task<WorldTransferAck> ReadNonProgressAckAsync(
        Stream stream,
        CancellationToken token)
    {
        while (true)
        {
            var frame = await PortableProtocol.ReadFrameAsync(stream, token);
            using (var document = System.Text.Json.JsonDocument.Parse(frame))
            {
                if (document.RootElement.TryGetProperty("messageType", out var messageType) &&
                    messageType.GetString() == WorldTransferService.ProgressMessageType)
                {
                    continue;
                }
            }
            var ack = PortableProtocol.Deserialize<WorldTransferAck>(frame, JsonOptions);
            Assert.NotNull(ack);
            return ack;
        }
    }

    private static WorldTransferHeader NewPrepareHeader() => new()
    {
        Protocol = WorldTransferService.ProtocolName,
        ProtocolVersion = WorldTransferService.ProtocolVersion,
        MessageType = WorldTransferService.PrepareMessageType,
        TransferId = Guid.NewGuid().ToString("N"),
        SenderName = "HandshakeTest",
        SenderIdentityId = Guid.NewGuid().ToString("N"),
        SenderIdentityName = "HandshakeTest",
        Size = 0,
        FileName = "world.zip",
        WorldName = "HandshakeWorld"
    };

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(50, token);
        }
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
        private readonly LanRelayService _relay;
        private readonly PackRuntimeService _packRuntimes;

        private ServiceFixture(
            string root,
            int port,
            AppPaths paths,
            WaypointSyncService waypoints,
            SkinService skins,
            LanRelayService relay,
            PackRuntimeService packRuntimes,
            WorldTransferService service)
        {
            _root = root;
            Port = port;
            Paths = paths;
            _waypoints = waypoints;
            _skins = skins;
            _relay = relay;
            _packRuntimes = packRuntimes;
            Service = service;
        }

        public int Port { get; }
        public AppPaths Paths { get; }
        public WorldTransferService Service { get; }
        public AppSettings Settings { get; } = new() { PlayerName = "HandshakeTest" };
        public string TransfersRoot => Path.Combine(Paths.Personal, "Transfers");

        public static ServiceFixture Create(int port, TimeSpan idleTimeout)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"minecraft-world-transfer-handshake-{Guid.NewGuid():N}");
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
            var network = new LoopbackNetworkTransport(endpoint);
            var routes = new PeerRouteResolver();
            var metadata = new WorldMetadataService();
            var identity = new LocalIdentityService(paths);
            var profiles = new WorldPlayerProfileService(paths, logger);
            var waypoints = new WaypointSyncService(paths, logger, metadata, network, routes);
            var skins = new SkinService(paths, logger, network, routes);
            var relay = new LanRelayService(logger, network, routes);
            var identityAdapter = new PortableIdentityAdapterService(paths, logger);
            var packInstances = new PackInstanceService(paths, logger);
            var packRuntimes = new PackRuntimeService(paths, logger);
            var minecraft = new MinecraftProcessService(
                paths, logger, identity, identityAdapter, profiles, packInstances, packRuntimes, waypoints, skins);
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
                relay,
                network,
                routes,
                new WorldTransferRuntimeOptions
                {
                    Port = port,
                    ListenAddress = IPAddress.Loopback,
                    PeerIdleTimeout = idleTimeout
                });
            return new ServiceFixture(root, port, paths, waypoints, skins, relay, packRuntimes, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await _waypoints.DisposeAsync();
            await _skins.DisposeAsync();
            await _relay.DisposeAsync();
            _packRuntimes.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class LoopbackNetworkTransport(
        NetworkEndpointInfo endpoint) : ISelectedNetworkTransport
    {
        public NetworkEnvironmentSnapshot GetSnapshot() => new()
        {
            Endpoints = [endpoint],
            PrimaryEndpoint = endpoint
        };

        public TcpClient CreateBoundTcpClient(
            IPAddress remoteAddress,
            string? localAddress = null,
            string? localInterfaceId = null) =>
            new(AddressFamily.InterNetwork);

        public UdpClient CreateBoundUdpClient(
            NetworkEndpointInfo networkEndpoint,
            int port,
            bool reuseAddress) =>
            throw new InvalidOperationException("No datagram socket was expected.");

        public Task<IReadOnlyList<IPAddress>> GetDynamicPeerTargetsAsync(
            NetworkEnvironmentSnapshot snapshot,
            CancellationToken token) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([]);
    }
}
