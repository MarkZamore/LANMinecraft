using System.IO;

namespace Minecraft;

/// <summary>
/// Who is on the other end of a peer connection. Steam authenticates the
/// account for us, so this replaces the IP-derived context the VPN transport
/// had to assemble (and half-trust) itself.
/// </summary>
public sealed record PeerConnectionContext(
    SteamId64 PeerId,
    string PersonaName,
    bool IsFriend,
    DateTimeOffset AcceptedAtUtc)
{
    public static PeerConnectionContext ForPeer(SteamId64 peer, string personaName = "", bool isFriend = true) =>
        new(peer, personaName, isFriend, DateTimeOffset.UtcNow);
}

/// <summary>
/// A stream that hands bytes to a transport which delivers them later. Writing
/// to one returns as soon as the bytes are queued, so a caller measuring
/// progress by what it has written measures its own speed, not the wire's.
/// </summary>
public interface IQueuedByteSink
{
    /// <summary>Bytes accepted from the writer but not yet delivered to the peer.</summary>
    long QueuedBytes { get; }
}

/// <summary>One duplex conversation with a peer, framed by PortableProtocol.</summary>
public sealed class PeerConnection(PeerConnectionContext context, Stream stream) : IAsyncDisposable
{
    public PeerConnectionContext Context { get; } = context;
    public Stream Stream { get; } = stream;

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// How the launcher reaches another launcher. The VPN era spread this across
/// three pieces - sockets bound to a chosen adapter, a table mapping identities
/// to addresses, and a per-protocol accept loop; a peer is now just a Steam
/// account, and everything above the stream stays the same.
/// </summary>
public interface IPeerTransport : IAsyncDisposable
{
    /// <summary>False while the underlying network is not usable yet.</summary>
    bool IsAvailable { get; }

    /// <summary>Russian explanation for the UI when <see cref="IsAvailable"/> is false.</summary>
    string UnavailableReason { get; }

    /// <summary>Opens a connection and announces which protocol will speak over it.</summary>
    Task<PeerConnection> ConnectAsync(SteamId64 peer, string protocolName, CancellationToken token);

    Task StartListeningAsync(CancellationToken token);

    Task StopListeningAsync();

    /// <summary>
    /// The peers this launcher currently holds a connection to. Being mid-
    /// conversation with somebody is the strongest evidence they are there -
    /// stronger than rich presence, which Steam serves on its own schedule.
    /// </summary>
    IReadOnlyCollection<SteamId64> ConnectedPeers { get; }

    /// <summary>Raised for every accepted connection; the router owns the rest.</summary>
    event EventHandler<PeerConnection>? ConnectionAccepted;
}

/// <summary>One of the protocols multiplexed over a peer connection.</summary>
public interface IPortableProtocolHandler
{
    /// <summary>The value in the first frame's "protocol" field.</summary>
    string ProtocolName { get; }

    Task HandleAsync(
        Stream stream,
        byte[] initialFrame,
        PeerConnectionContext context,
        CancellationToken token);
}

/// <summary>
/// Stand-in transport for builds where the Steam transport is not wired up yet:
/// it never accepts anything and explains itself when asked to connect, so the
/// UI can stay honest instead of throwing socket errors.
/// </summary>
public sealed class NullPeerTransport(string? unavailableReason = null) : IPeerTransport
{
    public bool IsAvailable => false;

    public string UnavailableReason { get; } =
        unavailableReason ?? "Соединение с другими игроками ещё не подключено.";

    public IReadOnlyCollection<SteamId64> ConnectedPeers => [];

    public event EventHandler<PeerConnection>? ConnectionAccepted
    {
        add { }
        remove { }
    }

    public Task<PeerConnection> ConnectAsync(SteamId64 peer, string protocolName, CancellationToken token) =>
        Task.FromException<PeerConnection>(new InvalidOperationException(UnavailableReason));

    public Task StartListeningAsync(CancellationToken token) => Task.CompletedTask;

    public Task StopListeningAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
