using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace Minecraft;

/// <summary>
/// Makes "who is online" symmetric.
///
/// Steam serves a friend's rich presence on its own terms: one player's client
/// gets the other's keys at once, the other's is told there are none, and the
/// pair sit there with one seeing a friend and the other seeing nobody. The
/// side that can see is the side that can act, so it introduces itself: a
/// short connection carrying the same keys it publishes to Steam. The receiver
/// then knows exactly what presence would have told it, and lists the friend
/// with a version and a state rather than as a nameless live connection.
///
/// One greeting per friend per session, repeated only when they reappear.
/// </summary>
public sealed class PeerGreetingService : IPortableProtocolHandler
{
    public const string ProtocolName = "MinecraftPortableHello";
    public const int ProtocolVersion = PortableFormat.ProtocolVersion;

    private static readonly TimeSpan GreetTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RegreetAfter = TimeSpan.FromMinutes(2);

    private readonly IPeerTransport _transport;
    private readonly Logger _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _greeted = new();
    private volatile SteamPeerPresence? _local;

    public PeerGreetingService(IPeerTransport transport, Logger logger)
    {
        _transport = transport;
        _logger = logger;
    }

    string IPortableProtocolHandler.ProtocolName => ProtocolName;

    /// <summary>Raised on the receiving side with the friend's own account of themselves.</summary>
    public event Action<SteamPeerPresence>? Greeted;

    /// <summary>The presence to introduce ourselves with; the same one Steam gets.</summary>
    public void SetLocalPresence(SteamPeerPresence presence) => _local = presence;

    /// <summary>
    /// Greets every friend whose presence we can read but who may not be able
    /// to read ours. Cheap to call from the UI timer: a friend already greeted
    /// this session is skipped until they have been gone a while.
    /// </summary>
    public void GreetNew(IReadOnlyList<SteamPeerPresence> visible, CancellationToken token)
    {
        var local = _local;
        if (local is null) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var peer in visible)
        {
            if (peer.SteamId == local.SteamId || !peer.SteamId.IsValid) continue;
            // Zero is our own marker for "listed from a live connection": they
            // reached us, so they already know we are here.
            if (peer.ProtocolVersion == 0) continue;
            if (_greeted.TryGetValue(peer.SteamId.Value, out var last) && now - last < RegreetAfter) continue;
            _greeted[peer.SteamId.Value] = now;
            _ = Task.Run(() => GreetAsync(peer.SteamId, local, token), token);
        }
    }

    /// <summary>Forgets a friend so their next appearance is greeted again.</summary>
    public void Forget(SteamId64 peer) => _greeted.TryRemove(peer.Value, out _);

    private async Task GreetAsync(SteamId64 peer, SteamPeerPresence local, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(GreetTimeout);
            await using var connection = await _transport
                .ConnectAsync(peer, ProtocolName, timeout.Token)
                .ConfigureAwait(false);
            await PortableProtocol.WriteJsonAsync(connection.Stream, new Hello
            {
                Protocol = ProtocolName,
                ProtocolVersion = ProtocolVersion,
                Presence = SteamPresenceCodec.Encode(local).ToDictionary(pair => pair.Key, pair => pair.Value)
            }, _jsonOptions, timeout.Token).ConfigureAwait(false);
            // The peer answers with a single frame so the connection is not
            // torn down under a message still in flight.
            await PortableProtocol.ReadFrameAsync(connection.Stream, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException
                                       or JsonException or ObjectDisposedException)
        {
            // A greeting is a courtesy; a friend who cannot be reached right now
            // is greeted again when they next appear.
            _greeted.TryRemove(peer.Value, out _);
            _logger.Info($"Could not greet {peer}: {ex.Message}");
        }
    }

    async Task IPortableProtocolHandler.HandleAsync(
        Stream stream,
        byte[] initialFrame,
        PeerConnectionContext context,
        CancellationToken token)
    {
        var hello = PortableProtocol.Deserialize<Hello>(initialFrame, _jsonOptions);
        if (hello is null || hello.Protocol != ProtocolName || hello.Presence is null) return;

        var presence = SteamPresenceCodec.TryDecode(
            context.PeerId,
            context.PersonaName,
            key => hello.Presence.TryGetValue(key, out var value) ? value : "");
        // Their greeting proves they can see us, so there is nothing to answer
        // with but an acknowledgement.
        await PortableProtocol.WriteJsonAsync(stream, new Hello
        {
            Protocol = ProtocolName,
            ProtocolVersion = ProtocolVersion
        }, _jsonOptions, token).ConfigureAwait(false);
        if (presence is not null) Greeted?.Invoke(presence);
    }

    private sealed class Hello
    {
        public string Protocol { get; set; } = "";
        public int ProtocolVersion { get; set; }
        public Dictionary<string, string>? Presence { get; set; }
    }
}
