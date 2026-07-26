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
            LocalInterfaceId = "private-interface-guid",
            IsHost = true,
            ServerPort = 35656,
            LanSessionId = Guid.NewGuid().ToString("N"),
            LanWorldName = "World"
        };

        var json = JsonSerializer.Serialize(announcement, new JsonSerializerOptions(
            JsonSerializerDefaults.Web));

        Assert.DoesNotContain("127.0.0.1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("10.60.0.1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-interface-guid", json, StringComparison.Ordinal);
        Assert.DoesNotContain("vpnIp", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("providerId", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(announcement.IdentityId, json, StringComparison.Ordinal);
        Assert.Contains(announcement.LanSessionId, json, StringComparison.Ordinal);
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
    public void MinecraftLanPayload_AdvertisesOnlyTheLocalRelayPort()
    {
        var payload = LanAdvertisementService.BuildPayload(
            "Relay World",
            "Remote Host",
            32123);
        var text = Encoding.UTF8.GetString(payload);

        Assert.Contains("[MOTD]", text, StringComparison.Ordinal);
        Assert.Contains("[AD]32123[/AD]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("10.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Remote Host", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MinecraftLanDiscovery_HasNoRemoteUdpTarget()
    {
        var targets = LanAdvertisementService.GetLocalAdvertisementTargets();

        Assert.Equal(2, targets.Count);
        Assert.All(targets, target =>
        {
            Assert.Equal(LanAdvertisementService.MinecraftLanDiscoveryPort, target.Port);
            var bytes = target.Address.GetAddressBytes();
            Assert.True(
                IPAddress.IsLoopback(target.Address) ||
                target.Address.AddressFamily == AddressFamily.InterNetwork &&
                bytes[0] is >= 224 and <= 239);
        });
    }

    [Fact]
    public void MinecraftLanRelayRoute_MustMatchTheCurrentlySelectedLocalScope()
    {
        var route = new PeerCandidateEndpoint
        {
            Address = "10.82.0.25",
            LocalAddress = "10.82.0.1",
            LocalInterfaceId = "selected-interface"
        };

        Assert.True(LanAdvertisementService.IsRouteOnSelectedEndpoint(
            route,
            "10.82.0.1",
            "selected-interface"));
        Assert.False(LanAdvertisementService.IsRouteOnSelectedEndpoint(
            route,
            "10.83.0.1",
            "other-interface"));
        Assert.False(LanAdvertisementService.IsRouteOnSelectedEndpoint(
            route,
            "",
            ""));
    }

    [Fact]
    public async Task OneRelayPerIdentityAndSession_ReplacesThePreviousSession()
    {
        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"minecraft-network-test-{Guid.NewGuid():N}.log");
        var routes = new PeerRouteResolver();
        await using var relay = new LanRelayService(
            new Logger(logPath),
            new FakeSelectedNetworkTransport(),
            routes);
        var endpoint = new PeerCandidateEndpoint
        {
            Address = "10.80.0.25",
            LocalAddress = "10.80.0.1",
            LocalInterfaceId = "selected-interface",
            AddressFamily = "IPv4",
            IsConfirmed = true
        };
        var identityId = Guid.NewGuid().ToString("D");

        var first = await relay.GetOrCreateClientRelayAsync(
            identityId,
            "session-a",
            [endpoint],
            41000);
        var duplicate = await relay.GetOrCreateClientRelayAsync(
            identityId,
            "session-a",
            [endpoint],
            41000);
        var reopened = await relay.GetOrCreateClientRelayAsync(
            identityId,
            "session-b",
            [endpoint],
            41001);

        Assert.Equal(first, duplicate);
        Assert.NotEqual(first.Key, reopened.Key);
        Assert.Equal(first.LocalPort, reopened.LocalPort);

        await relay.RetainClientRelaysAsync(new HashSet<string>(
            [reopened.Key],
            StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RemovingTheSession_ClosesItsLocalRelayListener()
    {
        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"minecraft-relay-close-{Guid.NewGuid():N}.log");
        await using var relay = new LanRelayService(
            new Logger(logPath),
            new FakeSelectedNetworkTransport(),
            new PeerRouteResolver());
        var info = await relay.GetOrCreateClientRelayAsync(
            Guid.NewGuid().ToString("D"),
            "session-to-close",
            [
                new PeerCandidateEndpoint
                {
                    Address = "10.83.0.25",
                    LocalAddress = "10.83.0.1",
                    LocalInterfaceId = "selected-interface",
                    IsConfirmed = true
                }
            ],
            41000);

        await relay.RetainClientRelaysAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        var error = await Record.ExceptionAsync(async () =>
            await client.ConnectAsync(
                IPAddress.Loopback,
                info.LocalPort,
                timeout.Token));
        Assert.True(error is SocketException or OperationCanceledException);
        Assert.False(client.Connected);
    }

    [Fact]
    public async Task HostSessionChange_CancelsAnAlreadyActiveRelay()
    {
        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"minecraft-host-session-{Guid.NewGuid():N}.log");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = timeout.Token;
        var sessionId = Guid.NewGuid().ToString("N");
        var worldListener = new TcpListener(IPAddress.Loopback, 0);
        var controlListener = new TcpListener(IPAddress.Loopback, 0);
        worldListener.Start();
        controlListener.Start();
        var worldPort = ((IPEndPoint)worldListener.LocalEndpoint).Port;
        var controlPort = ((IPEndPoint)controlListener.LocalEndpoint).Port;
        await using var relay = new LanRelayService(
            new Logger(logPath),
            new FakeSelectedNetworkTransport(),
            new PeerRouteResolver());
        relay.SetHostSession(worldPort, sessionId);

        using var controlClient = new TcpClient(AddressFamily.InterNetwork);
        var acceptControlTask = controlListener.AcceptTcpClientAsync(token).AsTask();
        await controlClient.ConnectAsync(
            IPAddress.Loopback,
            controlPort,
            token);
        using var controlServer = await acceptControlTask;
        var acceptWorldTask = worldListener.AcceptTcpClientAsync(token).AsTask();
        var handleTask = relay.HandleIncomingAsync(
            controlServer.GetStream(),
            BuildRelayRequestFrame(worldPort, sessionId),
            token);
        using var worldServer = await acceptWorldTask;

        var reply = await PortableProtocol.ReadFrameAsync(
            controlClient.GetStream(),
            token);
        Assert.True(ReadReplyOk(reply));

        relay.SetHostSession(worldPort, Guid.NewGuid().ToString("N"));

        await handleTask.WaitAsync(token);
        var buffer = new byte[1];
        var read = await worldServer.GetStream().ReadAsync(buffer, token);
        Assert.Equal(0, read);

        worldListener.Stop();
        controlListener.Stop();
    }

    [Fact]
    public async Task HostRejectsARequestFromThePreviousSession()
    {
        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"minecraft-host-reject-{Guid.NewGuid():N}.log");
        await using var relay = new LanRelayService(
            new Logger(logPath),
            new FakeSelectedNetworkTransport(),
            new PeerRouteResolver());
        var oldSessionId = Guid.NewGuid().ToString("N");
        var newSessionId = Guid.NewGuid().ToString("N");
        relay.SetHostSession(41000, oldSessionId);
        relay.SetHostSession(41001, newSessionId);
        await using var response = new MemoryStream();

        await relay.HandleIncomingAsync(
            response,
            BuildRelayRequestFrame(41000, oldSessionId),
            CancellationToken.None);
        response.Position = 0;
        var reply = await PortableProtocol.ReadFrameAsync(
            response,
            CancellationToken.None);

        Assert.False(ReadReplyOk(reply));
    }

    [Fact]
    public async Task V6ObservedWorld_AppearsLocallyAndOpensThroughRelay_WithoutRemoteUdp4445()
    {
        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"minecraft-relay-integration-{Guid.NewGuid():N}.log");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var sessionId = Guid.NewGuid().ToString("N");
        var requestBytes = Encoding.UTF8.GetBytes("minecraft-client-request");
        var responseBytes = Encoding.UTF8.GetBytes("minecraft-host-response");

        var worldListener = new TcpListener(IPAddress.Loopback, 0);
        var controlListener = new TcpListener(IPAddress.Loopback, 0);
        using var minecraftDiscovery = new UdpClient(
            new IPEndPoint(IPAddress.Loopback, 0));
        worldListener.Start();
        controlListener.Start();
        var worldPort = ((IPEndPoint)worldListener.LocalEndpoint).Port;
        var controlEndpoint = (IPEndPoint)controlListener.LocalEndpoint;
        var minecraftDiscoveryPort =
            ((IPEndPoint)minecraftDiscovery.Client.LocalEndPoint!).Port;
        var logger = new Logger(logPath);
        var clock = new FakeNetworkClock(
            new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));
        var routes = new PeerRouteResolver(clock);
        var peerId = Guid.NewGuid().ToString("D");
        var localEndpoint = Endpoint(
            "selected-interface",
            "10.90.0.1",
            90);
        var failedAddress = IPAddress.Parse("10.90.0.24");
        var workingAddress = IPAddress.Parse("10.90.0.25");
        var announcement = new PeerAnnouncement { IdentityId = peerId };
        routes.UpsertFromAnnouncement(
            announcement,
            failedAddress,
            localEndpoint);
        routes.UpsertFromAnnouncement(
            announcement,
            workingAddress,
            localEndpoint);
        var peer = new PeerViewModel();
        peer.Apply(new PeerAnnouncement
        {
            ProtocolVersion = PeerDiscoveryService.ProtocolVersion,
            IdentityId = peerId,
            PlayerName = "Remote Host",
            NetworkAddress = workingAddress.ToString(),
            LocalAddress = localEndpoint.NetworkAddress,
            LocalInterfaceId = localEndpoint.InterfaceId,
            IsHost = true,
            ServerPort = worldPort,
            LanSessionId = sessionId,
            LanWorldName = "Relay World"
        }, "");
        var connector = new LoopbackLanRelayPeerConnector(controlEndpoint)
        {
            AddressToFail = failedAddress
        };

        await using var hostRelay = new LanRelayService(
            logger,
            new FakeSelectedNetworkTransport(),
            routes);
        await using var clientRelay = new LanRelayService(
            logger,
            routes,
            connector);
        await using var lanAdvertisement = new LanAdvertisementService(
            logger,
            clientRelay,
            routes,
            minecraftDiscoveryPort,
            clock);
        hostRelay.SetHostSession(worldPort, sessionId);

        var worldTask = Task.Run(async () =>
        {
            using var worldClient = await worldListener.AcceptTcpClientAsync(token);
            var stream = worldClient.GetStream();
            var received = new byte[requestBytes.Length];
            await ReadExactlyAsync(stream, received, token);
            Assert.Equal(requestBytes, received);
            await stream.WriteAsync(responseBytes, token);
            await stream.FlushAsync(token);
            worldClient.Client.Shutdown(SocketShutdown.Send);
        }, token);

        var controlTask = Task.Run(async () =>
        {
            using var controlClient = await controlListener.AcceptTcpClientAsync(token);
            var stream = controlClient.GetStream();
            var initialFrame = await PortableProtocol.ReadFrameAsync(stream, token);
            await hostRelay.HandleIncomingAsync(stream, initialFrame, token);
        }, token);

        try
        {
            lanAdvertisement.Update(
                null,
                "",
                "",
                "",
                [localEndpoint],
                [peer]);
            await lanAdvertisement.PublishOnceAsync(token);
            var localAnnouncement = await minecraftDiscovery.ReceiveAsync(token);
            Assert.True(IPAddress.IsLoopback(
                localAnnouncement.RemoteEndPoint.Address));
            var relayPort = ReadLanAdvertisementPort(
                localAnnouncement.Buffer);

            using var minecraftClient = new TcpClient(AddressFamily.InterNetwork);
            await minecraftClient.ConnectAsync(
                IPAddress.Loopback,
                relayPort,
                token);
            var minecraftStream = minecraftClient.GetStream();
            await minecraftStream.WriteAsync(requestBytes, token);
            await minecraftStream.FlushAsync(token);
            var receivedResponse = new byte[responseBytes.Length];
            await ReadExactlyAsync(
                minecraftStream,
                receivedResponse,
                token);

            Assert.Equal(responseBytes, receivedResponse);
            Assert.NotNull(connector.LastTarget);
            Assert.Equal("10.90.0.25", connector.LastTarget.Address.ToString());
            Assert.Equal("10.90.0.1", connector.LastTarget.LocalAddress);
            Assert.Equal(
                "selected-interface",
                connector.LastTarget.LocalInterfaceId);
            Assert.Equal(WorldTransferService.TransferPort, connector.LastPort);
            Assert.Equal(
                [failedAddress, workingAddress],
                connector.AttemptedAddresses);
            var routeHealth = routes.GetSendCandidates(peerId);
            Assert.Equal(
                1,
                routeHealth.Single(route =>
                    route.Address == failedAddress.ToString()).FailureScore);
            Assert.NotEqual(
                default,
                routeHealth.Single(route =>
                    route.Address == workingAddress.ToString()).LastSuccessUtc);

            minecraftClient.Client.Shutdown(SocketShutdown.Both);
            await Task.WhenAll(worldTask, controlTask);
        }
        finally
        {
            worldListener.Stop();
            controlListener.Stop();
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Fact]
    public async Task ProductionRelayConnector_PassesTheSelectedRouteAndRequestedControlPort()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var transport = new RecordingSelectedNetworkTransport();
        var connector = new SelectedNetworkLanRelayPeerConnector(transport);
        var target = new LanRelayTarget(
            IPAddress.Loopback,
            "10.91.0.1",
            "selected-interface");
        var acceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();

        try
        {
            using var client = await connector.ConnectAsync(
                target,
                endpoint.Port,
                timeout.Token);
            using var accepted = await acceptTask;

            Assert.Equal(target.Address, transport.LastRemoteAddress);
            Assert.Equal(target.LocalAddress, transport.LastLocalAddress);
            Assert.Equal(
                target.LocalInterfaceId,
                transport.LastLocalInterfaceId);
            Assert.True(client.Connected);
            Assert.True(accepted.Connected);
        }
        finally
        {
            listener.Stop();
        }
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

    private static byte[] BuildRelayRequestFrame(
        int serverPort,
        string sessionId) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                Protocol = LanRelayService.ProtocolName,
                ProtocolVersion = LanRelayService.ProtocolVersion,
                ServerPort = serverPort,
                LanSessionId = sessionId
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static bool ReadReplyOk(byte[] frame)
    {
        using var document = JsonDocument.Parse(frame);
        return document.RootElement.GetProperty("ok").GetBoolean();
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
            text.AsSpan(start + startMarker.Length, end - start - startMarker.Length),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
