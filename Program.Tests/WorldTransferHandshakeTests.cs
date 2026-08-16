using System.Globalization;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The transfer handshake as a receiver sees it, driven by a second launcher on
/// an in-memory peer network. Everything above the stream is the protocol that
/// shipped over TCP, so these cases carried over unchanged.
/// </summary>
public sealed class WorldTransferHandshakeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PrepareHandshake_ThenSilence_ReleasesTheTransferGate()
    {
        await using var fixture = ServiceFixture.Create(TimeSpan.FromMilliseconds(600));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await fixture.StartAcceptingAsync(timeout.Token);

        await using var connection = await fixture.ConnectAsSenderAsync(timeout.Token);
        var stream = connection.Stream;
        await PortableProtocol.WriteJsonAsync(stream, NewPrepareHeader(), JsonOptions, timeout.Token);

        var ack = PortableProtocol.Deserialize<WorldTransferAck>(
            await PortableProtocol.ReadFrameAsync(stream, timeout.Token), JsonOptions);
        Assert.NotNull(ack);
        Assert.True(ack.Ok);
        Assert.Equal("Preparing", ack.Stage);
        Assert.True(fixture.Service.IsOperationActive);

        // The peer now goes silent while keeping the connection open, which is
        // what a hung launcher looks like. The gate has to come back on its own.
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

    /// <summary>
    /// Steam authenticates the account behind a connection, so a header that
    /// names a different sender is a forgery attempt and never gets a world.
    /// </summary>
    [Fact]
    public async Task AHeaderFromAnotherSteamAccount_IsRejected()
    {
        await using var fixture = ServiceFixture.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await fixture.StartAcceptingAsync(timeout.Token);

        await using var connection = await fixture.ConnectAsSenderAsync(timeout.Token);
        var header = NewPrepareHeader();
        header.SenderSteamId64 = "76561198000000009";
        await PortableProtocol.WriteJsonAsync(connection.Stream, header, JsonOptions, timeout.Token);

        var ack = PortableProtocol.Deserialize<WorldTransferAck>(
            await PortableProtocol.ReadFrameAsync(connection.Stream, timeout.Token), JsonOptions);
        Assert.NotNull(ack);
        Assert.False(ack.Ok);
        Assert.Equal("Rejected", ack.Stage);
        Assert.False(fixture.Service.IsOperationActive);
    }

    /// <summary>
    /// Under Steam the sender is any friend running the launcher, so the
    /// receiving player is asked before a world is taken - and a refusal has to
    /// be answered, not left to time out.
    /// </summary>
    [Fact]
    public async Task ADeclinedWorld_IsRejectedBeforeAnythingIsWritten()
    {
        var confirmation = new ScriptedConfirmation(accept: false);
        await using var fixture = ServiceFixture.Create(confirmation: confirmation);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await fixture.StartAcceptingAsync(timeout.Token);

        await using var connection = await fixture.ConnectAsSenderAsync(timeout.Token);
        await PortableProtocol.WriteJsonAsync(
            connection.Stream, NewPrepareHeader(), JsonOptions, timeout.Token);

        var ack = PortableProtocol.Deserialize<WorldTransferAck>(
            await PortableProtocol.ReadFrameAsync(connection.Stream, timeout.Token), JsonOptions);
        Assert.NotNull(ack);
        Assert.False(ack.Ok);
        Assert.Equal("Rejected", ack.Stage);
        Assert.Contains("отклонил", ack.Message, StringComparison.Ordinal);
        Assert.NotNull(confirmation.LastOffer);
        Assert.Equal(ServiceFixture.SenderSteamId, confirmation.LastOffer!.SenderSteamId.Value);
        Assert.Equal("HandshakeWorld", confirmation.LastOffer.WorldName);

        // Nothing was staged, and the gate is free for the next attempt.
        await WaitUntilAsync(() => !fixture.Service.IsOperationActive, timeout.Token);
        Assert.False(Directory.Exists(fixture.TransfersRoot) &&
                     Directory.EnumerateFileSystemEntries(fixture.TransfersRoot).Any());
    }

    private sealed class ScriptedConfirmation(bool accept) : IWorldTransferConfirmation
    {
        public WorldTransferOffer? LastOffer { get; private set; }

        public Task<bool> ConfirmAsync(WorldTransferOffer offer, CancellationToken token)
        {
            LastOffer = offer;
            return Task.FromResult(accept);
        }
    }

    [Fact]
    public async Task PrepareHandshake_ForwardsSenderProgressToTheLocalUi()
    {
        await using var fixture = ServiceFixture.Create(TimeSpan.FromSeconds(15));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stages = new List<WorldTransferProgress>();
        var sawCompress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.ProgressChanged += progress =>
        {
            lock (stages) stages.Add(progress);
            if (progress.Stage.Contains("Сжатие", StringComparison.Ordinal)) sawCompress.TrySetResult();
        };

        await fixture.StartAcceptingAsync(timeout.Token);
        await using var connection = await fixture.ConnectAsSenderAsync(timeout.Token);
        var stream = connection.Stream;
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
        await using var fixture = ServiceFixture.Create(TimeSpan.FromSeconds(15));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var becameHost = 0;
        fixture.Service.BecameHost += () => Interlocked.Increment(ref becameHost);
        await fixture.StartAcceptingAsync(timeout.Token);
        // Build a sender-side world with valid manifests, exactly as SendWorldAsync would.
        var senderRoot = Path.Combine(
            Path.GetTempPath(), $"minecraft-world-transfer-sender-{Guid.NewGuid():N}");
        try
        {
            var senderPaths = new AppPaths(senderRoot);
            senderPaths.Ensure();
            var senderLogger = new Logger(senderPaths.LogFile);
            var senderProfiles = new WorldPlayerProfileService(senderPaths, senderLogger);
            var senderIdentity = TestIdentity.CreateContext("SenderE2E", ServiceFixture.SenderSteamId);
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
                world, senderIdentity.MinecraftUuid, senderIdentity.IdentityName, transferred: false));
            var waypointStore = new WaypointStoreService(
                senderMetadata, new WaypointProviderRegistry(senderLogger), senderLogger);
            waypointStore.EnsureManifest(world);
            var waypointManifestSha = waypointStore.GetManifestHash(world);

            var archivePath = Path.Combine(senderRoot, "world.zip");
            var worldSha = await WorldTransferService.CreateWorldArchiveWithHashAsync(
                world, archivePath, (_, _) => Task.CompletedTask, heartbeat: null, timeout.Token);

            await using var connection = await fixture.ConnectAsSenderAsync(timeout.Token);
            var stream = connection.Stream;
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
                SenderIdentityId = senderIdentity.MinecraftUuid,
                SenderSteamId64 = ServiceFixture.SenderSteamId.ToString(CultureInfo.InvariantCulture),
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
        SenderIdentityId = Guid.NewGuid().ToString("D"),
        SenderSteamId64 = ServiceFixture.SenderSteamId.ToString(CultureInfo.InvariantCulture),
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
        public AppSettings Settings { get; } = new() { PlayerName = "TransferTest" };
        public string TransfersRoot => Path.Combine(Paths.Personal, "Transfers");

        public static ServiceFixture Create(
            TimeSpan? idleTimeout = null,
            IWorldTransferConfirmation? confirmation = null)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"minecraft-world-transfer-{Guid.NewGuid():N}");
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
                receiverTransport,
                idleTimeout is null
                    ? new WorldTransferRuntimeOptions()
                    : new WorldTransferRuntimeOptions { PeerIdleTimeout = idleTimeout.Value },
                confirmation);
            var router = new PeerConnectionRouter(receiverTransport, logger);
            router.RegisterFallback(service);
            return new ServiceFixture(
                root, paths, logger, waypoints, skins, packRuntimes, router, senderTransport, service);
        }

        /// <summary>Starts accepting transfers, as the window does once Steam is up.</summary>
        public async Task StartAcceptingAsync(CancellationToken token)
        {
            await _router.StartAsync(token);
            Service.UseSettingsForIncomingTransfers(Settings);
        }

        /// <summary>The stream a sending launcher would write its handshake to.</summary>
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
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
