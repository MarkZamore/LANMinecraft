using System.Text.Json;

namespace Minecraft.Tests;

/// <summary>
/// The router is the successor of the shared-port multiplexer: one accept path,
/// several protocols, and a fallback for the world transfer whose handshake
/// carries no protocol name.
/// </summary>
public sealed class PeerConnectionRouterTests
{
    private const ulong HostId = 76561198000000001;
    private const ulong GuestId = 76561198000000002;

    [Fact]
    public async Task FirstFrame_PicksTheRegisteredProtocol()
    {
        var network = new InMemoryPeerNetwork();
        network.MakeFriends(HostId, GuestId);
        var host = network.CreateTransport(HostId, "MarkZamore");
        var guest = network.CreateTransport(GuestId, "anuvenn");

        var waypoints = new RecordingHandler("MinecraftPortableWaypoints");
        var skins = new RecordingHandler("MinecraftPortableSkin");
        var transfer = new RecordingHandler("fallback");
        await using var router = new PeerConnectionRouter(host);
        router.Register(waypoints);
        router.Register(skins);
        router.RegisterFallback(transfer);
        await router.StartAsync(CancellationToken.None);

        await using (var connection = await guest.ConnectAsync(
            SteamId64.Parse(HostId.ToString()), "MinecraftPortableSkin", CancellationToken.None))
        {
            await WriteFrameAsync(connection.Stream, new { protocol = "MinecraftPortableSkin", version = 1 });
            await skins.Handled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(skins.Handled.Task.IsCompletedSuccessfully);
        Assert.False(waypoints.Handled.Task.IsCompleted);
        Assert.False(transfer.Handled.Task.IsCompleted);
        Assert.Equal(GuestId, skins.LastContext?.PeerId.Value);
        Assert.Equal("anuvenn", skins.LastContext?.PersonaName);
    }

    [Fact]
    public async Task AFrameWithoutAProtocol_GoesToTheFallback()
    {
        var network = new InMemoryPeerNetwork();
        network.MakeFriends(HostId, GuestId);
        var host = network.CreateTransport(HostId);
        var guest = network.CreateTransport(GuestId);

        var waypoints = new RecordingHandler("MinecraftPortableWaypoints");
        var transfer = new RecordingHandler("fallback");
        await using var router = new PeerConnectionRouter(host);
        router.Register(waypoints);
        router.RegisterFallback(transfer);
        await router.StartAsync(CancellationToken.None);

        await using (var connection = await guest.ConnectAsync(
            SteamId64.Parse(HostId.ToString()), "world", CancellationToken.None))
        {
            // The world transfer opens with its own header, which has a
            // messageType but no protocol field.
            await WriteFrameAsync(connection.Stream, new { messageType = "Prepare", size = 0 });
            await transfer.Handled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(transfer.Handled.Task.IsCompletedSuccessfully);
        Assert.False(waypoints.Handled.Task.IsCompleted);
    }

    [Fact]
    public async Task AnUnknownProtocolWithoutAFallback_IsDropped()
    {
        var network = new InMemoryPeerNetwork();
        network.MakeFriends(HostId, GuestId);
        var host = network.CreateTransport(HostId);
        var guest = network.CreateTransport(GuestId);

        var waypoints = new RecordingHandler("MinecraftPortableWaypoints");
        await using var router = new PeerConnectionRouter(host);
        router.Register(waypoints);
        await router.StartAsync(CancellationToken.None);

        await using (var connection = await guest.ConnectAsync(
            SteamId64.Parse(HostId.ToString()), "unknown", CancellationToken.None))
        {
            await WriteFrameAsync(connection.Stream, new { protocol = "SomethingElse" });
            await Task.Delay(200);
        }

        Assert.False(waypoints.Handled.Task.IsCompleted);
    }

    [Fact]
    public async Task AConnectionFromANonFriend_IsRefusedBeforeTheFirstFrame()
    {
        var network = new InMemoryPeerNetwork();
        // Deliberately no friendship between the two accounts.
        var host = network.CreateTransport(HostId);
        var guest = network.CreateTransport(GuestId);

        var skins = new RecordingHandler("MinecraftPortableSkin");
        await using var router = new PeerConnectionRouter(host);
        router.Register(skins);
        await router.StartAsync(CancellationToken.None);

        await using (var connection = await guest.ConnectAsync(
            SteamId64.Parse(HostId.ToString()), "MinecraftPortableSkin", CancellationToken.None))
        {
            await WriteFrameAsync(connection.Stream, new { protocol = "MinecraftPortableSkin" });
            await Task.Delay(200);
        }

        Assert.False(skins.Handled.Task.IsCompleted);
    }

    [Fact]
    public async Task NullTransport_ExplainsItselfInsteadOfThrowingSocketErrors()
    {
        await using var transport = new NullPeerTransport("Steam-транспорт ещё не подключён.");
        Assert.False(transport.IsAvailable);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => transport.ConnectAsync(SteamId64.Parse(HostId.ToString()), "world", CancellationToken.None));
        Assert.Equal("Steam-транспорт ещё не подключён.", failure.Message);
    }

    private static Task WriteFrameAsync<T>(Stream stream, T value) =>
        PortableProtocol.WriteJsonAsync(
            stream,
            value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            CancellationToken.None);

    private sealed class RecordingHandler(string protocolName) : IPortableProtocolHandler
    {
        public string ProtocolName { get; } = protocolName;
        public TaskCompletionSource Handled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PeerConnectionContext? LastContext { get; private set; }
        public byte[]? LastFrame { get; private set; }

        public Task HandleAsync(
            Stream stream,
            byte[] initialFrame,
            PeerConnectionContext context,
            CancellationToken token)
        {
            LastContext = context;
            LastFrame = initialFrame;
            Handled.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
