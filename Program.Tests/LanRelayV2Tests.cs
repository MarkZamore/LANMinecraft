using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class LanRelayV2Tests
{
    private static readonly JsonSerializerOptions WebJson =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void Announcement_RoundTripsV2Capability_WhileV22PayloadDefaultsToLegacy()
    {
        var current = new PeerAnnouncement
        {
            ProtocolVersion = PeerDiscoveryService.ProtocolVersion,
            IdentityId = Guid.NewGuid().ToString("D"),
            LanRelayProtocolVersion = LanRelayService.ResumableProtocolVersion
        };

        var payload = JsonSerializer.SerializeToUtf8Bytes(current, WebJson);
        var restored = JsonSerializer.Deserialize<PeerAnnouncement>(payload, WebJson);
        var legacy = JsonSerializer.Deserialize<PeerAnnouncement>(
            $$"""{"protocolVersion":{{PeerDiscoveryService.ProtocolVersion}}}""",
            WebJson);

        Assert.NotNull(restored);
        Assert.Equal(PeerDiscoveryService.ProtocolVersion, restored.ProtocolVersion);
        Assert.Equal(
            LanRelayService.ResumableProtocolVersion,
            restored.LanRelayProtocolVersion);
        Assert.NotNull(legacy);
        Assert.Equal(0, legacy.LanRelayProtocolVersion);
        Assert.Equal(1, LanRelayService.ProtocolVersion);

        var currentPeer = new PeerViewModel();
        currentPeer.Apply(restored, "");
        var legacyPeer = new PeerViewModel();
        legacyPeer.Apply(legacy, "");

        Assert.True(currentPeer.SupportsResumableLanRelay);
        Assert.False(legacyPeer.SupportsResumableLanRelay);
    }

    [Fact]
    public void V2Protocol_UsesTheReleaseSafetyLimits()
    {
        Assert.Equal(64 * 1024, LanRelayV2Protocol.MaxPayloadBytes);
        Assert.Equal(
            8 * 1024 * 1024,
            LanRelayV2Protocol.MaxBufferedBytesPerDirection);
        Assert.Equal(TimeSpan.FromSeconds(15), LanRelayV2Protocol.ReconnectGrace);
        Assert.Equal(TimeSpan.FromSeconds(2), LanRelayV2Protocol.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(6), LanRelayV2Protocol.TransportTimeout);
    }

    [Theory]
    [InlineData((int)LanRelayV2FrameType.Data)]
    [InlineData((int)LanRelayV2FrameType.Ack)]
    [InlineData((int)LanRelayV2FrameType.Heartbeat)]
    [InlineData((int)LanRelayV2FrameType.Close)]
    public async Task V2Protocol_RoundTripsEveryFrameType(int frameTypeValue)
    {
        var frameType = (LanRelayV2FrameType)frameTypeValue;
        var payload = frameType == LanRelayV2FrameType.Data
            ? Encoding.UTF8.GetBytes("relay-payload")
            : [];
        var expected = new LanRelayV2Frame(frameType, 1234, payload);
        await using var stream = new MemoryStream();

        await LanRelayV2Protocol.WriteFrameAsync(
            stream,
            expected,
            CancellationToken.None);
        stream.Position = 0;
        var restored = await LanRelayV2Protocol.ReadFrameAsync(
            stream,
            CancellationToken.None);

        Assert.Equal(expected.Type, restored.Type);
        Assert.Equal(expected.Offset, restored.Offset);
        Assert.Equal(expected.Payload, restored.Payload);
    }

    [Fact]
    public async Task V2Protocol_RejectsPayloadLargerThanTheWireLimit()
    {
        var oversized = new LanRelayV2Frame(
            LanRelayV2FrameType.Data,
            0,
            new byte[LanRelayV2Protocol.MaxPayloadBytes + 1]);
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LanRelayV2Protocol.WriteFrameAsync(
                stream,
                oversized,
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task V2Protocol_RejectsAnOffsetRangeOverflowAsInvalidData()
    {
        var overflowing = LanRelayV2Frame.Data(
            long.MaxValue,
            [0x2A]);
        await using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LanRelayV2Protocol.WriteFrameAsync(
                output,
                overflowing,
                CancellationToken.None).AsTask());

        var wire = new byte[14];
        wire[0] = (byte)LanRelayV2FrameType.Data;
        BinaryPrimitives.WriteInt64BigEndian(
            wire.AsSpan(1, 8),
            long.MaxValue);
        BinaryPrimitives.WriteInt32BigEndian(wire.AsSpan(9, 4), 1);
        wire[13] = 0x2A;
        await using var input = new MemoryStream(wire);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LanRelayV2Protocol.ReadFrameAsync(
                input,
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task V2Protocol_RejectsAnOversizedLengthBeforeAllocatingPayload()
    {
        // Wire header: type (1 byte), absolute offset (8 bytes), payload length
        // (4 bytes). The receiver must reject the length before reading a body.
        var malformed = new byte[13];
        malformed[0] = (byte)LanRelayV2FrameType.Data;
        BinaryPrimitives.WriteInt64BigEndian(malformed.AsSpan(1, 8), 0);
        BinaryPrimitives.WriteInt32BigEndian(
            malformed.AsSpan(9, 4),
            LanRelayV2Protocol.MaxPayloadBytes + 1);
        await using var stream = new MemoryStream(malformed);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LanRelayV2Protocol.ReadFrameAsync(
                stream,
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task V2Handshake_RejectsFramesAboveTheRelaySpecificLimit()
    {
        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"minecraft-relay-v2-handshake-limit-{Guid.NewGuid():N}.log");
        await using var relay = new LanRelayService(
            new Logger(logPath),
            new PeerRouteResolver(),
            new AlwaysFailConnector());
        await using var response = new MemoryStream();

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                relay.HandleIncomingAsync(
                    response,
                    new byte[LanRelayService.MaxHandshakeBytes + 1],
                    CancellationToken.None));
            Assert.Equal(0, response.Length);
        }
        finally
        {
            await relay.DisposeAsync();
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Fact]
    public async Task V2RejectedHandshake_DiagnosticsAreNormalizedAndBounded()
    {
        var hostIdentity = Guid.NewGuid().ToString("D");
        var invalidSession =
            "current\r\nforged-log-line-" + new string('x', 256);
        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"minecraft-relay-v2-handshake-fields-{Guid.NewGuid():N}.log");
        await using var relay = new LanRelayService(
            new Logger(logPath),
            new PeerRouteResolver(),
            new AlwaysFailConnector());
        relay.SetLocalIdentity(hostIdentity);
        relay.SetHostSession(41000, "current");
        LanRelayDiagnosticEvent? rejected = null;
        relay.DiagnosticEvent += value => rejected = value;
        await using var response = new MemoryStream();

        try
        {
            await relay.HandleIncomingAsync(
                response,
                BuildV2RelayRequestFrame(
                    41000,
                    invalidSession,
                    "not-a-guid\r\nforged",
                    hostIdentity),
                CancellationToken.None);

            Assert.NotNull(rejected);
            Assert.Equal("rejected", rejected.Phase);
            Assert.Equal("", rejected.PeerIdentityId);
            Assert.InRange(rejected.LanSessionId.Length, 1, 128);
            Assert.DoesNotContain(
                rejected.LanSessionId,
                character => char.IsControl(character));
            response.Position = 0;
            var reply = await PortableProtocol.ReadFrameAsync(
                response,
                CancellationToken.None,
                LanRelayService.MaxHandshakeBytes);
            Assert.False(ReadReplyOk(reply));
            Assert.Equal(
                "The LAN relay session identifier is invalid.",
                ReadReplyMessage(reply));
        }
        finally
        {
            await relay.DisposeAsync();
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Fact]
    public async Task ClientWithoutAdvertisedCapability_UsesLegacyV1Handshake()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var connector = new LoopbackConnector(endpoint);
        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"minecraft-relay-v1-fallback-{Guid.NewGuid():N}.log");
        await using var relay = new LanRelayService(
            new Logger(logPath),
            new PeerRouteResolver(),
            connector);
        var route = new PeerCandidateEndpoint
        {
            Address = "10.200.0.2",
            LocalAddress = "10.200.0.1",
            LocalInterfaceId = "selected-interface",
            IsConfirmed = true
        };
        var info = await relay.GetOrCreateClientRelayAsync(
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("N"),
            [route],
            41000);
        var acceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();

        try
        {
            using var minecraft = new TcpClient(AddressFamily.InterNetwork);
            await minecraft.ConnectAsync(
                IPAddress.Loopback,
                info.LocalPort,
                timeout.Token);
            using var accepted = await acceptTask;
            var stream = accepted.GetStream();
            var request = await PortableProtocol.ReadFrameAsync(
                stream,
                timeout.Token);
            using var requestJson = JsonDocument.Parse(request);

            Assert.Equal(
                LanRelayService.ProtocolVersion,
                requestJson.RootElement.GetProperty("protocolVersion").GetInt32());

            await PortableProtocol.WriteJsonAsync(
                stream,
                new
                {
                    Protocol = LanRelayService.ProtocolName,
                    ProtocolVersion = LanRelayService.ProtocolVersion,
                    Ok = true,
                    Message = ""
                },
                WebJson,
                timeout.Token);
            minecraft.Client.Shutdown(SocketShutdown.Both);
        }
        finally
        {
            listener.Stop();
            await relay.DisposeAsync();
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Fact]
    public async Task V2Tunnel_LostAckAndReconnect_DeliversEachByteExactlyOnce()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var first = Encoding.UTF8.GetBytes("first block before dropped acknowledgement|");
        var second = Encoding.UTF8.GetBytes("second block after transport resume");
        var expected = first.Concat(second).ToArray();
        var response = Encoding.UTF8.GetBytes("world response after resume");

        var (localMinecraft, clientMinecraft) =
            await CreateConnectedPairAsync(token);
        var (hostMinecraft, worldMinecraft) =
            await CreateConnectedPairAsync(token);
        using (localMinecraft)
        using (worldMinecraft)
        await using (var clientTunnel = new LanRelayV2Tunnel(
                         clientMinecraft,
                         SystemNetworkClock.Instance,
                         _ => { },
                         token))
        {
            var ackDropped = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var dropAck = 1;
            await using var hostTunnel = new LanRelayV2Tunnel(
                hostMinecraft,
                SystemNetworkClock.Instance,
                _ => { },
                token,
                (frame, _) =>
                {
                    if (frame.Type == LanRelayV2FrameType.Ack &&
                        Interlocked.Exchange(ref dropAck, 0) == 1)
                    {
                        ackDropped.TrySetResult();
                        throw new IOException("Injected lost ACK.");
                    }
                    return ValueTask.CompletedTask;
                });

            var (clientTransport1, hostTransport1) =
                await CreateConnectedPairAsync(token);
            var clientAttach1 = clientTunnel.AttachAsync(
                clientTransport1.GetStream(),
                peerReceivedOffset: 0,
                token);
            var hostAttach1 = hostTunnel.AttachAsync(
                hostTransport1.GetStream(),
                peerReceivedOffset: 0,
                token);

            await localMinecraft.GetStream().WriteAsync(first, token);
            await ackDropped.Task.WaitAsync(token);
            await Assert.ThrowsAsync<IOException>(() => hostAttach1);

            hostTransport1.Dispose();
            clientTransport1.Dispose();
            await Assert.ThrowsAnyAsync<Exception>(() => clientAttach1);

            var (clientTransport2, hostTransport2) =
                await CreateConnectedPairAsync(token);
            try
            {
                // Supplying zero deliberately forces replay of the unacknowledged
                // first block. The host must discard the duplicate prefix.
                var clientAttach2 = clientTunnel.AttachAsync(
                    clientTransport2.GetStream(),
                    peerReceivedOffset: 0,
                    token);
                var hostAttach2 = hostTunnel.AttachAsync(
                    hostTransport2.GetStream(),
                    peerReceivedOffset: 0,
                    token);

                await localMinecraft.GetStream().WriteAsync(second, token);
                var received = new byte[expected.Length];
                await ReadExactlyAsync(
                    worldMinecraft.GetStream(),
                    received,
                    token);
                Assert.Equal(expected, received);

                await worldMinecraft.GetStream().WriteAsync(response, token);
                var receivedResponse = new byte[response.Length];
                await ReadExactlyAsync(
                    localMinecraft.GetStream(),
                    receivedResponse,
                    token);
                Assert.Equal(response, receivedResponse);

                clientTunnel.Stop("test_complete");
                hostTunnel.Stop("test_complete");
                await IgnoreConnectionEndAsync(clientAttach2, hostAttach2);
            }
            finally
            {
                clientTransport2.Dispose();
                hostTransport2.Dispose();
            }
        }
    }

    [Fact]
    public async Task V2Tunnel_AttachmentCancellationDuringLocalWrite_DeduplicatesReplay()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = timeout.Token;
        var payload = Encoding.UTF8.GetBytes(
            "data committed while the portable listener restarts");
        var (hostMinecraft, worldMinecraft) =
            await CreateConnectedPairAsync(token);
        using (worldMinecraft)
        {
            var writeEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseWrite = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var hookCalls = 0;
            await using var hostTunnel = new LanRelayV2Tunnel(
                hostMinecraft,
                SystemNetworkClock.Instance,
                _ => { },
                token,
                beforeWriteMinecraftForTesting:
                async (buffer, writeToken) =>
                {
                    if (Interlocked.Increment(ref hookCalls) != 1) return;
                    Assert.Equal(payload, buffer.ToArray());
                    writeEntered.TrySetResult();
                    await releaseWrite.Task.WaitAsync(writeToken);
                });

            var (peerTransport1, hostTransport1) =
                await CreateConnectedPairAsync(token);
            using (peerTransport1)
            using (hostTransport1)
            using (var attachment =
                   CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var firstAttach = hostTunnel.AttachAsync(
                    hostTransport1.GetStream(),
                    peerReceivedOffset: 0,
                    attachment.Token);
                await LanRelayV2Protocol.WriteFrameAsync(
                    peerTransport1.GetStream(),
                    LanRelayV2Frame.Data(0, payload),
                    token);
                await writeEntered.Task.WaitAsync(token);

                attachment.Cancel();
                releaseWrite.TrySetResult();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => firstAttach);
            }

            var delivered = new byte[payload.Length];
            await ReadExactlyAsync(
                worldMinecraft.GetStream(),
                delivered,
                token);
            Assert.Equal(payload, delivered);
            Assert.Equal(
                payload.Length,
                hostTunnel.InboundReceivedOffset);

            var (peerTransport2, hostTransport2) =
                await CreateConnectedPairAsync(token);
            using (peerTransport2)
            using (hostTransport2)
            {
                var secondAttach = hostTunnel.AttachAsync(
                    hostTransport2.GetStream(),
                    peerReceivedOffset: 0,
                    token);
                await LanRelayV2Protocol.WriteFrameAsync(
                    peerTransport2.GetStream(),
                    LanRelayV2Frame.Data(0, payload),
                    token);
                var acknowledgement =
                    await LanRelayV2Protocol.ReadFrameAsync(
                        peerTransport2.GetStream(),
                        token);

                Assert.Equal(
                    LanRelayV2FrameType.Ack,
                    acknowledgement.Type);
                Assert.Equal(payload.Length, acknowledgement.Offset);
                Assert.Equal(0, worldMinecraft.Client.Available);

                hostTunnel.Stop("test_complete");
                await IgnoreConnectionEndAsync(secondAttach);
            }
        }
    }

    [Fact]
    public async Task BriefDiscoveryDisappearance_RetainsTheSameLocalRelayListener()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = timeout.Token;
        var now = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
        var clock = new NetworkTestData.FakeNetworkClock(now);
        var routes = new PeerRouteResolver(clock);
        var peerId = Guid.NewGuid().ToString("D");
        var sessionId = Guid.NewGuid().ToString("N");
        var localEndpoint = NetworkTestData.Endpoint(
            "selected-interface",
            "10.201.0.1",
            201);
        var observedAddress = IPAddress.Parse("10.201.0.2");
        routes.UpsertFromAnnouncement(
            new PeerAnnouncement
            {
                ProtocolVersion = PeerDiscoveryService.ProtocolVersion,
                IdentityId = peerId,
                PlayerName = "Transient Host"
            },
            observedAddress,
            localEndpoint);
        var peer = new PeerViewModel();
        peer.Apply(
            new PeerAnnouncement
            {
                ProtocolVersion = PeerDiscoveryService.ProtocolVersion,
                IdentityId = peerId,
                PlayerName = "Transient Host",
                NetworkAddress = observedAddress.ToString(),
                LocalAddress = localEndpoint.NetworkAddress,
                LocalInterfaceId = localEndpoint.InterfaceId,
                IsHost = true,
                ServerPort = 41000,
                LanSessionId = sessionId,
                LanWorldName = "Retained world",
                LanRelayProtocolVersion =
                    LanRelayService.ResumableProtocolVersion
            },
            "");

        using var discoveryReceiver = new UdpClient(
            new IPEndPoint(IPAddress.Loopback, 0));
        var discoveryPort =
            ((IPEndPoint)discoveryReceiver.Client.LocalEndPoint!).Port;
        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"minecraft-relay-retention-{Guid.NewGuid():N}.log");
        var connector = new AlwaysFailConnector();
        await using var relay = new LanRelayService(
            new Logger(logPath),
            routes,
            connector,
            clock);
        relay.SetLocalIdentity(Guid.NewGuid().ToString("D"));
        await using var advertisements = new LanAdvertisementService(
            new Logger(logPath),
            relay,
            routes,
            discoveryPort,
            clock);

        try
        {
            advertisements.Update(
                null,
                "",
                "",
                "",
                [localEndpoint],
                [peer]);
            await advertisements.PublishOnceAsync(token);
            var advertisement = await discoveryReceiver.ReceiveAsync(token);
            var relayPort = ReadLanAdvertisementPort(advertisement.Buffer);

            advertisements.Update(
                null,
                "",
                "",
                "",
                [localEndpoint],
                []);
            await advertisements.PublishOnceAsync(token);

            Assert.Equal(1, relay.GetDiagnosticSnapshot().ClientRelayCount);
            using var minecraft = new TcpClient(AddressFamily.InterNetwork);
            await minecraft.ConnectAsync(
                IPAddress.Loopback,
                relayPort,
                token);
            Assert.True(minecraft.Connected);
        }
        finally
        {
            await advertisements.DisposeAsync();
            await relay.DisposeAsync();
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Fact]
    public async Task BriefRouteDisappearanceWhileHostRemainsVisible_RetainsRelayUntilGraceExpires()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = timeout.Token;
        var now = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
        var clock = new NetworkTestData.FakeNetworkClock(now);
        var routes = new PeerRouteResolver(clock);
        var peerId = Guid.NewGuid().ToString("D");
        var sessionId = Guid.NewGuid().ToString("N");
        var localEndpoint = NetworkTestData.Endpoint(
            "selected-interface",
            "10.201.1.1",
            211);
        var observedAddress = IPAddress.Parse("10.201.1.2");
        routes.UpsertFromAnnouncement(
            new PeerAnnouncement
            {
                ProtocolVersion = PeerDiscoveryService.ProtocolVersion,
                IdentityId = peerId,
                PlayerName = "Visible Host"
            },
            observedAddress,
            localEndpoint);
        var peer = new PeerViewModel();
        peer.Apply(
            new PeerAnnouncement
            {
                ProtocolVersion = PeerDiscoveryService.ProtocolVersion,
                IdentityId = peerId,
                PlayerName = "Visible Host",
                NetworkAddress = observedAddress.ToString(),
                LocalAddress = localEndpoint.NetworkAddress,
                LocalInterfaceId = localEndpoint.InterfaceId,
                IsHost = true,
                ServerPort = 41000,
                LanSessionId = sessionId,
                LanWorldName = "Route gap world",
                LanRelayProtocolVersion =
                    LanRelayService.ResumableProtocolVersion
            },
            "");

        using var discoveryReceiver = new UdpClient(
            new IPEndPoint(IPAddress.Loopback, 0));
        var discoveryPort =
            ((IPEndPoint)discoveryReceiver.Client.LocalEndPoint!).Port;
        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"minecraft-relay-route-retention-{Guid.NewGuid():N}.log");
        await using var relay = new LanRelayService(
            new Logger(logPath),
            routes,
            new AlwaysFailConnector(),
            clock);
        relay.SetLocalIdentity(Guid.NewGuid().ToString("D"));
        await using var advertisements = new LanAdvertisementService(
            new Logger(logPath),
            relay,
            routes,
            discoveryPort,
            clock);

        try
        {
            advertisements.Update(
                null,
                "",
                "",
                "",
                [localEndpoint],
                [peer]);
            await advertisements.PublishOnceAsync(token);
            var initialAdvertisement =
                await discoveryReceiver.ReceiveAsync(token);
            var relayPort = ReadLanAdvertisementPort(
                initialAdvertisement.Buffer);

            advertisements.Update(
                null,
                "",
                "",
                "",
                [],
                [peer]);
            await advertisements.PublishOnceAsync(token);
            var retainedAdvertisement =
                await discoveryReceiver.ReceiveAsync(token);

            Assert.Equal(
                relayPort,
                ReadLanAdvertisementPort(retainedAdvertisement.Buffer));
            Assert.Equal(
                1,
                relay.GetDiagnosticSnapshot().ClientRelayCount);

            clock.UtcNow += TimeSpan.FromMinutes(2) +
                            TimeSpan.FromMilliseconds(1);
            await advertisements.PublishOnceAsync(token);

            Assert.Equal(
                0,
                relay.GetDiagnosticSnapshot().ClientRelayCount);
        }
        finally
        {
            await advertisements.DisposeAsync();
            await relay.DisposeAsync();
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Fact]
    public async Task V23Peers_MidstreamLostAck_ResumeWithoutMinecraftReconnect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var token = timeout.Token;
        var clientIdentity = Guid.NewGuid().ToString("D");
        var hostIdentity = Guid.NewGuid().ToString("D");
        var sessionId = Guid.NewGuid().ToString("N");
        var clientAddress = IPAddress.Parse("10.202.0.10");
        var hostAddress = IPAddress.Parse("10.202.0.20");
        var failoverHostAddress = IPAddress.Parse("10.202.0.21");
        const string interfaceId = "selected-interface";
        var clientEndpoint = NetworkTestData.Endpoint(
            interfaceId,
            clientAddress.ToString(),
            202);
        var hostEndpoint = NetworkTestData.Endpoint(
            interfaceId,
            hostAddress.ToString(),
            202);
        var clientRoutes = new PeerRouteResolver();
        clientRoutes.UpsertFromAnnouncement(
            new PeerAnnouncement { IdentityId = hostIdentity },
            hostAddress,
            clientEndpoint);
        clientRoutes.UpsertFromAnnouncement(
            new PeerAnnouncement { IdentityId = hostIdentity },
            failoverHostAddress,
            clientEndpoint);
        var hostRoutes = new PeerRouteResolver();
        hostRoutes.UpsertFromAnnouncement(
            new PeerAnnouncement { IdentityId = clientIdentity },
            clientAddress,
            hostEndpoint);

        var worldListener = new TcpListener(IPAddress.Loopback, 0);
        var controlListener = new TcpListener(IPAddress.Loopback, 0);
        worldListener.Start();
        controlListener.Start();
        var worldPort = ((IPEndPoint)worldListener.LocalEndpoint).Port;
        var controlEndpoint = (IPEndPoint)controlListener.LocalEndpoint;
        var connector = new CountingLoopbackConnector(controlEndpoint);
        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"minecraft-relay-v2-integration-{Guid.NewGuid():N}.log");
        var logger = new Logger(logPath);
        var first = Encoding.UTF8.GetBytes(
            "minecraft request delivered before the transport reset|");
        var second = Encoding.UTF8.GetBytes(
            "request continues after transparent resume");
        var expected = first.Concat(second).ToArray();
        var response = Encoding.UTF8.GetBytes(
            "host response over the resumed relay");
        var worldAcceptCount = 0;
        var handlerTasks = new ConcurrentBag<Task>();

        try
        {
            await using var hostRelay = new LanRelayService(
                logger,
                new NetworkTestData.FakeSelectedNetworkTransport(),
                hostRoutes);
            await using var clientRelay = new LanRelayService(
                logger,
                clientRoutes,
                connector);
            hostRelay.SetLocalIdentity(hostIdentity);
            clientRelay.SetLocalIdentity(clientIdentity);
            hostRelay.SetHostSession(worldPort, sessionId);

            var lostAck = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var routeUpdated = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var dropAck = 1;
            hostRelay.V2BeforeWriteFrameForTesting = async (frame, _) =>
            {
                if (frame.Type == LanRelayV2FrameType.Ack &&
                    Interlocked.Exchange(ref dropAck, 0) == 1)
                {
                    await clientRelay.GetOrCreateClientRelayAsync(
                        hostIdentity,
                        sessionId,
                        clientRoutes.GetSendCandidates(hostIdentity)
                            .Where(candidate =>
                                string.Equals(
                                    candidate.Address,
                                    failoverHostAddress.ToString(),
                                    StringComparison.OrdinalIgnoreCase))
                            .ToArray(),
                        worldPort,
                        LanRelayService.ResumableProtocolVersion);
                    routeUpdated.TrySetResult();
                    lostAck.TrySetResult();
                    throw new IOException(
                        "Injected transport reset before ACK delivery.");
                }
            };
            var resumed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            clientRelay.DiagnosticEvent += value =>
            {
                if (string.Equals(
                        value.Phase,
                        "resumed",
                        StringComparison.Ordinal))
                {
                    resumed.TrySetResult();
                }
            };

            var controlLoop = RunControlLoopAsync(
                controlListener,
                hostRelay,
                connectionNumber => new PortableConnectionContext(
                    clientAddress,
                    50000,
                    connectionNumber == 1
                        ? hostAddress
                        : failoverHostAddress,
                    controlEndpoint.Port,
                    interfaceId,
                    202,
                    DateTimeOffset.UtcNow),
                handlerTasks,
                token);
            var worldAccept = worldListener
                .AcceptTcpClientAsync(token)
                .AsTask();
            var initialRoute = Assert.Single(
                clientRoutes.GetSendCandidates(hostIdentity),
                candidate => string.Equals(
                    candidate.Address,
                    hostAddress.ToString(),
                    StringComparison.OrdinalIgnoreCase));
            var relayInfo = await clientRelay.GetOrCreateClientRelayAsync(
                hostIdentity,
                sessionId,
                [initialRoute],
                worldPort,
                LanRelayService.ResumableProtocolVersion);

            using var minecraft = new TcpClient(AddressFamily.InterNetwork);
            await minecraft.ConnectAsync(
                IPAddress.Loopback,
                relayInfo.LocalPort,
                token);
            using var world = await worldAccept;
            Interlocked.Increment(ref worldAcceptCount);

            await minecraft.GetStream().WriteAsync(first, token);
            await lostAck.Task.WaitAsync(token);
            await routeUpdated.Task.WaitAsync(token);
            await connector.SecondConnection.WaitAsync(token);
            await resumed.Task.WaitAsync(token);

            await minecraft.GetStream().WriteAsync(second, token);
            var received = new byte[expected.Length];
            await ReadExactlyAsync(world.GetStream(), received, token);
            Assert.Equal(expected, received);

            await world.GetStream().WriteAsync(response, token);
            var receivedResponse = new byte[response.Length];
            await ReadExactlyAsync(
                minecraft.GetStream(),
                receivedResponse,
                token);
            Assert.Equal(response, receivedResponse);
            Assert.Equal(1, Volatile.Read(ref worldAcceptCount));
            Assert.True(connector.ConnectionCount >= 2);
            Assert.Equal(hostAddress, connector.AttemptedTargets[0].Address);
            Assert.Equal(
                failoverHostAddress,
                connector.AttemptedTargets[^1].Address);
            Assert.All(
                connector.AttemptedTargets,
                target => Assert.Equal(interfaceId, target.LocalInterfaceId));
            Assert.Equal(relayInfo.LocalPort, ((IPEndPoint)
                minecraft.Client.RemoteEndPoint!).Port);

            minecraft.Client.Shutdown(SocketShutdown.Both);
            timeout.Cancel();
            await IgnoreConnectionEndAsync(
                [controlLoop, .. handlerTasks.ToArray()]);
        }
        finally
        {
            timeout.Cancel();
            worldListener.Stop();
            controlListener.Stop();
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.203.0.99", false)]
    public async Task V2Host_RejectsLoopbackAndUnknownObservedRoutes(
        string remoteAddressText,
        bool isLoopback)
    {
        var hostIdentity = Guid.NewGuid().ToString("D");
        var clientIdentity = Guid.NewGuid().ToString("D");
        var hostAddress = IPAddress.Parse("10.203.0.20");
        var knownClientAddress = IPAddress.Parse("10.203.0.10");
        const string interfaceId = "selected-interface";
        var routes = new PeerRouteResolver();
        routes.UpsertFromAnnouncement(
            new PeerAnnouncement { IdentityId = clientIdentity },
            knownClientAddress,
            NetworkTestData.Endpoint(
                interfaceId,
                hostAddress.ToString(),
                203));
        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"minecraft-relay-v2-security-{Guid.NewGuid():N}.log");
        await using var relay = new LanRelayService(
            new Logger(logPath),
            new NetworkTestData.FakeSelectedNetworkTransport(),
            routes);
        relay.SetLocalIdentity(hostIdentity);
        relay.SetHostSession(41000, "current-session");
        await using var response = new MemoryStream();
        var request = BuildV2RelayRequestFrame(
            41000,
            "current-session",
            clientIdentity,
            hostIdentity);

        try
        {
            await relay.HandleIncomingAsync(
                response,
                request,
                new PortableConnectionContext(
                    IPAddress.Parse(remoteAddressText),
                    50000,
                    hostAddress,
                    WorldTransferService.TransferPort,
                    interfaceId,
                    203,
                    DateTimeOffset.UtcNow),
                CancellationToken.None);
            response.Position = 0;
            var reply = await PortableProtocol.ReadFrameAsync(
                response,
                CancellationToken.None);

            Assert.False(ReadReplyOk(reply));
            Assert.Equal(
                0,
                relay.GetDiagnosticSnapshot().ActiveResumableTunnels);
            Assert.Equal(
                isLoopback
                    ? "The LAN relay did not arrive through a usable selected route."
                    : "The LAN relay peer route is unknown.",
                ReadReplyMessage(reply));
        }
        finally
        {
            await relay.DisposeAsync();
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Fact]
    public async Task V2Host_OverlappingResumeRequests_GrantOneAttachmentLease()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(7));
        var token = timeout.Token;
        var hostIdentity = Guid.NewGuid().ToString("D");
        var clientIdentity = Guid.NewGuid().ToString("D");
        var sessionId = Guid.NewGuid().ToString("N");
        var tunnelId = Guid.NewGuid();
        var resumeToken = Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(32));
        var hostAddress = IPAddress.Parse("10.203.1.20");
        var clientAddress = IPAddress.Parse("10.203.1.10");
        const string interfaceId = "selected-interface";
        var routes = new PeerRouteResolver();
        routes.UpsertFromAnnouncement(
            new PeerAnnouncement { IdentityId = clientIdentity },
            clientAddress,
            NetworkTestData.Endpoint(
                interfaceId,
                hostAddress.ToString(),
                213));
        var worldListener = new TcpListener(IPAddress.Loopback, 0);
        worldListener.Start();
        var worldPort =
            ((IPEndPoint)worldListener.LocalEndpoint).Port;
        var context = new PortableConnectionContext(
            clientAddress,
            50000,
            hostAddress,
            WorldTransferService.TransferPort,
            interfaceId,
            213,
            DateTimeOffset.UtcNow);
        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"minecraft-relay-v2-overlap-{Guid.NewGuid():N}.log");

        try
        {
            await using var relay = new LanRelayService(
                new Logger(logPath),
                new NetworkTestData.FakeSelectedNetworkTransport(),
                routes);
            relay.SetLocalIdentity(hostIdentity);
            relay.SetHostSession(worldPort, sessionId);

            var (openClient, openServer) =
                await CreateConnectedPairAsync(token);
            using (openClient)
            using (openServer)
            using (var openAttachment =
                   CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var worldAccept = worldListener
                    .AcceptTcpClientAsync(token)
                    .AsTask();
                var openHandler = relay.HandleIncomingAsync(
                    openServer.GetStream(),
                    BuildV2RelayRequestFrame(
                        worldPort,
                        sessionId,
                        clientIdentity,
                        hostIdentity,
                        tunnelId: tunnelId,
                        resumeToken: resumeToken),
                    context,
                    openAttachment.Token);
                using var world = await worldAccept;
                var openReply = await PortableProtocol.ReadFrameAsync(
                    openClient.GetStream(),
                    token,
                    LanRelayService.MaxHandshakeBytes);
                Assert.True(ReadReplyOk(openReply));

                openAttachment.Cancel();
                await openHandler.WaitAsync(token);

                var (resumeClient1, resumeServer1) =
                    await CreateConnectedPairAsync(token);
                var (resumeClient2, resumeServer2) =
                    await CreateConnectedPairAsync(token);
                using (resumeClient1)
                using (resumeServer1)
                using (resumeClient2)
                using (resumeServer2)
                using (var resumeAttachments =
                       CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    var resumeRequest = BuildV2RelayRequestFrame(
                        worldPort,
                        sessionId,
                        clientIdentity,
                        hostIdentity,
                        mode: "resume",
                        tunnelId: tunnelId,
                        resumeToken: resumeToken);
                    var handler1 = relay.HandleIncomingAsync(
                        resumeServer1.GetStream(),
                        resumeRequest,
                        context with { RemotePort = 50001 },
                        resumeAttachments.Token);
                    var handler2 = relay.HandleIncomingAsync(
                        resumeServer2.GetStream(),
                        resumeRequest,
                        context with { RemotePort = 50002 },
                        resumeAttachments.Token);
                    var replies = await Task.WhenAll(
                        PortableProtocol.ReadFrameAsync(
                            resumeClient1.GetStream(),
                            token,
                            LanRelayService.MaxHandshakeBytes),
                        PortableProtocol.ReadFrameAsync(
                            resumeClient2.GetStream(),
                            token,
                            LanRelayService.MaxHandshakeBytes));

                    Assert.Equal(
                        1,
                        replies.Count(ReadReplyOk));
                    var winner = ReadReplyOk(replies[0])
                        ? resumeClient1
                        : resumeClient2;
                    var payload = Encoding.UTF8.GetBytes(
                        "single owner attachment");
                    await LanRelayV2Protocol.WriteFrameAsync(
                        winner.GetStream(),
                        LanRelayV2Frame.Data(0, payload),
                        token);
                    var delivered = new byte[payload.Length];
                    await ReadExactlyAsync(
                        world.GetStream(),
                        delivered,
                        token);
                    Assert.Equal(payload, delivered);
                    var acknowledgement =
                        await LanRelayV2Protocol.ReadFrameAsync(
                            winner.GetStream(),
                            token);
                    Assert.Equal(
                        LanRelayV2FrameType.Ack,
                        acknowledgement.Type);
                    Assert.Equal(
                        payload.Length,
                        acknowledgement.Offset);

                    resumeAttachments.Cancel();
                    await Task.WhenAll(handler1, handler2).WaitAsync(token);
                }
            }
        }
        finally
        {
            worldListener.Stop();
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Fact]
    public async Task HostSessionChange_ClosesV2TunnelAndRejectsStaleResume()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        var token = timeout.Token;
        var hostIdentity = Guid.NewGuid().ToString("D");
        var clientIdentity = Guid.NewGuid().ToString("D");
        var oldSession = Guid.NewGuid().ToString("N");
        var newSession = Guid.NewGuid().ToString("N");
        var tunnelId = Guid.NewGuid();
        var resumeToken = Convert.ToBase64String(
            Enumerable.Range(0, 32).Select(index => (byte)index).ToArray());
        var hostAddress = IPAddress.Parse("10.204.0.20");
        var clientAddress = IPAddress.Parse("10.204.0.10");
        const string interfaceId = "selected-interface";
        var routes = new PeerRouteResolver();
        routes.UpsertFromAnnouncement(
            new PeerAnnouncement { IdentityId = clientIdentity },
            clientAddress,
            NetworkTestData.Endpoint(
                interfaceId,
                hostAddress.ToString(),
                204));
        var worldListener = new TcpListener(IPAddress.Loopback, 0);
        worldListener.Start();
        var worldPort = ((IPEndPoint)worldListener.LocalEndpoint).Port;
        var context = new PortableConnectionContext(
            clientAddress,
            50000,
            hostAddress,
            WorldTransferService.TransferPort,
            interfaceId,
            204,
            DateTimeOffset.UtcNow);
        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"minecraft-relay-v2-session-{Guid.NewGuid():N}.log");

        try
        {
            await using var relay = new LanRelayService(
                new Logger(logPath),
                new NetworkTestData.FakeSelectedNetworkTransport(),
                routes);
            relay.SetLocalIdentity(hostIdentity);
            relay.SetHostSession(worldPort, oldSession);
            var (requestClient, requestServer) =
                await CreateConnectedPairAsync(token);
            using (requestClient)
            using (requestServer)
            {
                var worldAccept = worldListener
                    .AcceptTcpClientAsync(token)
                    .AsTask();
                var handler = relay.HandleIncomingAsync(
                    requestServer.GetStream(),
                    BuildV2RelayRequestFrame(
                        worldPort,
                        oldSession,
                        clientIdentity,
                        hostIdentity,
                        tunnelId: tunnelId,
                        resumeToken: resumeToken),
                    context,
                    token);
                using var world = await worldAccept;
                var reply = await PortableProtocol.ReadFrameAsync(
                    requestClient.GetStream(),
                    token);
                Assert.True(ReadReplyOk(reply));

                relay.SetHostSession(worldPort, newSession);
                await handler.WaitAsync(token);
                var closed = new byte[1];
                Assert.Equal(
                    0,
                    await world.GetStream().ReadAsync(closed, token));
            }

            var (resumeClient, resumeServer) =
                await CreateConnectedPairAsync(token);
            using (resumeClient)
            using (resumeServer)
            {
                await relay.HandleIncomingAsync(
                    resumeServer.GetStream(),
                    BuildV2RelayRequestFrame(
                        worldPort,
                        oldSession,
                        clientIdentity,
                        hostIdentity,
                        mode: "resume",
                        tunnelId: tunnelId,
                        resumeToken: resumeToken),
                    context,
                    token);
                var reply = await PortableProtocol.ReadFrameAsync(
                    resumeClient.GetStream(),
                    token);
                Assert.False(ReadReplyOk(reply));
                Assert.Equal(
                    "LAN session is not available.",
                    ReadReplyMessage(reply));
            }
        }
        finally
        {
            worldListener.Stop();
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Fact]
    public async Task V2ReconnectGraceExpiry_ClosesTheMinecraftConnection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = timeout.Token;
        var clock = new NetworkTestData.FakeNetworkClock(
            new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero));
        var connector = new AlwaysFailConnector();
        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"minecraft-relay-v2-expiry-{Guid.NewGuid():N}.log");
        var terminal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var relay = new LanRelayService(
            new Logger(logPath),
            new PeerRouteResolver(clock),
            connector,
            clock);
        relay.SetLocalIdentity(Guid.NewGuid().ToString("D"));
        relay.DiagnosticEvent += value =>
        {
            if (string.Equals(
                    value.TerminalReason,
                    "reconnect_grace_expired",
                    StringComparison.Ordinal))
            {
                terminal.TrySetResult();
            }
        };
        var info = await relay.GetOrCreateClientRelayAsync(
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("N"),
            [
                new PeerCandidateEndpoint
                {
                    Address = "10.205.0.20",
                    LocalAddress = "10.205.0.10",
                    LocalInterfaceId = "selected-interface",
                    IsConfirmed = true
                }
            ],
            41000,
            LanRelayService.ResumableProtocolVersion);

        try
        {
            using var minecraft = new TcpClient(AddressFamily.InterNetwork);
            await minecraft.ConnectAsync(
                IPAddress.Loopback,
                info.LocalPort,
                token);
            await terminal.Task.WaitAsync(token);
            var buffer = new byte[1];
            var read = await Record.ExceptionAsync(async () =>
            {
                var count = await minecraft.GetStream().ReadAsync(
                    buffer,
                    token);
                Assert.Equal(0, count);
            });
            Assert.True(read is null or IOException or SocketException);
            Assert.True(connector.ConnectionCount >= 1);
            Assert.True(clock.UtcNow >=
                        new DateTimeOffset(
                            2026,
                            7,
                            28,
                            12,
                            0,
                            15,
                            TimeSpan.Zero));
        }
        finally
        {
            await relay.DisposeAsync();
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    private static async Task<(TcpClient Client, TcpClient Server)>
        CreateConnectedPairAsync(CancellationToken token)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var client = new TcpClient(AddressFamily.InterNetwork);
            var acceptTask = listener.AcceptTcpClientAsync(token).AsTask();
            await client.ConnectAsync(endpoint.Address, endpoint.Port, token);
            return (client, await acceptTask);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task RunControlLoopAsync(
        TcpListener listener,
        LanRelayService hostRelay,
        Func<int, PortableConnectionContext> connectionFactory,
        ConcurrentBag<Task> handlerTasks,
        CancellationToken token)
    {
        var connectionNumber = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var accepted = await listener
                    .AcceptTcpClientAsync(token)
                    .ConfigureAwait(false);
                var handler = HandleControlConnectionAsync(
                    accepted,
                    hostRelay,
                    connectionFactory(++connectionNumber),
                    token);
                handlerTasks.Add(handler);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (SocketException) when (token.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (token.IsCancellationRequested)
        {
        }
    }

    private static async Task HandleControlConnectionAsync(
        TcpClient accepted,
        LanRelayService hostRelay,
        PortableConnectionContext connection,
        CancellationToken token)
    {
        using (accepted)
        {
            try
            {
                var stream = accepted.GetStream();
                var initialFrame = await PortableProtocol.ReadFrameAsync(
                    stream,
                    token);
                await hostRelay.HandleIncomingAsync(
                    stream,
                    initialFrame,
                    connection with
                    {
                        RemotePort = ((IPEndPoint)
                            accepted.Client.RemoteEndPoint!).Port,
                        LocalPort = ((IPEndPoint)
                            accepted.Client.LocalEndPoint!).Port,
                        AcceptedAtUtc = DateTimeOffset.UtcNow
                    },
                    token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (
                ex is IOException or
                SocketException or
                EndOfStreamException or
                ObjectDisposedException)
            {
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

    private static async Task IgnoreConnectionEndAsync(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task;
            }
            catch (Exception ex) when (
                ex is IOException or
                SocketException or
                EndOfStreamException or
                OperationCanceledException or
                ObjectDisposedException)
            {
            }
        }
    }

    private static int ReadLanAdvertisementPort(byte[] payload)
    {
        var text = Encoding.UTF8.GetString(payload);
        const string startMarker = "[AD]";
        const string endMarker = "[/AD]";
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        var end = text.IndexOf(endMarker, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return int.Parse(
            text.AsSpan(
                start + startMarker.Length,
                end - start - startMarker.Length),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] BuildV2RelayRequestFrame(
        int serverPort,
        string sessionId,
        string senderIdentity,
        string recipientIdentity,
        string mode = "open",
        Guid? tunnelId = null,
        string? resumeToken = null,
        long receivedOffset = 0) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                Protocol = LanRelayService.ProtocolName,
                ProtocolVersion = LanRelayService.ResumableProtocolVersion,
                ServerPort = serverPort,
                LanSessionId = sessionId,
                Mode = mode,
                TunnelId = tunnelId ?? Guid.NewGuid(),
                ResumeToken = resumeToken ??
                              Convert.ToBase64String(new byte[32]),
                SenderIdentityId = senderIdentity,
                RecipientIdentityId = recipientIdentity,
                ReceivedOffset = receivedOffset
            },
            WebJson);

    private static bool ReadReplyOk(byte[] frame)
    {
        using var document = JsonDocument.Parse(frame);
        return document.RootElement.GetProperty("ok").GetBoolean();
    }

    private static string ReadReplyMessage(byte[] frame)
    {
        using var document = JsonDocument.Parse(frame);
        return document.RootElement.GetProperty("message").GetString() ?? "";
    }

    private sealed class LoopbackConnector(IPEndPoint endpoint)
        : ILanRelayPeerConnector
    {
        public async Task<TcpClient> ConnectAsync(
            LanRelayTarget target,
            int remotePort,
            CancellationToken token)
        {
            var client = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                await client.ConnectAsync(
                    endpoint.Address,
                    endpoint.Port,
                    token);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    private sealed class AlwaysFailConnector : ILanRelayPeerConnector
    {
        private int _connectionCount;

        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        public Task<TcpClient> ConnectAsync(
            LanRelayTarget target,
            int remotePort,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _connectionCount);
            return Task.FromException<TcpClient>(
                new SocketException((int)SocketError.NetworkUnreachable));
        }
    }

    private sealed class CountingLoopbackConnector(IPEndPoint endpoint)
        : ILanRelayPeerConnector
    {
        private readonly TaskCompletionSource _secondConnection =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<LanRelayTarget> _attemptedTargets =
            new();
        private int _connectionCount;

        public int ConnectionCount => Volatile.Read(ref _connectionCount);
        public Task SecondConnection => _secondConnection.Task;
        public IReadOnlyList<LanRelayTarget> AttemptedTargets =>
            _attemptedTargets.ToArray();

        public async Task<TcpClient> ConnectAsync(
            LanRelayTarget target,
            int remotePort,
            CancellationToken token)
        {
            var client = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                _attemptedTargets.Enqueue(target);
                await client.ConnectAsync(
                    endpoint.Address,
                    endpoint.Port,
                    token);
                if (Interlocked.Increment(ref _connectionCount) >= 2)
                {
                    _secondConnection.TrySetResult();
                }
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }
}
