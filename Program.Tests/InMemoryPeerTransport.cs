using System.IO.Pipelines;

namespace Minecraft.Tests;

/// <summary>
/// A whole peer network in one process: every launcher under test gets a
/// transport bound to its Steam account, and connecting between two of them
/// creates a real duplex byte stream. It replaces the loopback-TCP fakes the
/// VPN-era tests used, without any sockets or ports.
/// </summary>
internal sealed class InMemoryPeerNetwork
{
    private readonly Dictionary<ulong, InMemoryPeerTransport> _transports = [];
    private readonly HashSet<(ulong, ulong)> _friendships = [];

    public InMemoryPeerTransport CreateTransport(ulong steamId64, string personaName = "Player")
    {
        Assert.True(SteamId64.TryFrom(steamId64, out var peer));
        var transport = new InMemoryPeerTransport(this, peer, personaName);
        _transports[steamId64] = transport;
        return transport;
    }

    public void MakeFriends(ulong first, ulong second)
    {
        _friendships.Add((first, second));
        _friendships.Add((second, first));
    }

    internal bool AreFriends(ulong first, ulong second) =>
        _friendships.Contains((first, second));

    internal InMemoryPeerTransport? Find(ulong steamId64) =>
        _transports.TryGetValue(steamId64, out var transport) ? transport : null;
}

internal sealed class InMemoryPeerTransport(
    InMemoryPeerNetwork network,
    SteamId64 localPeer,
    string personaName) : IPeerTransport
{
    private bool _listening;

    public SteamId64 LocalPeer { get; } = localPeer;
    public string PersonaName { get; } = personaName;
    public List<(SteamId64 Peer, string Protocol)> OutgoingConnections { get; } = [];

    public bool IsAvailable { get; set; } = true;
    public string UnavailableReason { get; set; } = "";

    /// <summary>Peers this transport has open connections to.</summary>
    public List<SteamId64> Connected { get; } = [];

    public IReadOnlyCollection<SteamId64> ConnectedPeers => Connected;

    public event EventHandler<PeerConnection>? ConnectionAccepted;

    public Task<PeerConnection> ConnectAsync(SteamId64 peer, string protocolName, CancellationToken token)
    {
        if (!IsAvailable) throw new InvalidOperationException(UnavailableReason);

        var remote = network.Find(peer.Value)
            ?? throw new IOException($"Peer {peer} is not online.");
        if (!remote._listening) throw new IOException($"Peer {peer} is not listening.");

        OutgoingConnections.Add((peer, protocolName));

        // Two pipes, crossed: what one side writes the other side reads.
        var toRemote = new Pipe();
        var toLocal = new Pipe();
        var localStream = new DuplexStream(toLocal.Reader, toRemote.Writer);
        var remoteStream = new DuplexStream(toRemote.Reader, toLocal.Writer);

        var accepted = new PeerConnection(
            new PeerConnectionContext(
                LocalPeer,
                PersonaName,
                network.AreFriends(LocalPeer.Value, peer.Value),
                DateTimeOffset.UtcNow),
            remoteStream);
        remote.ConnectionAccepted?.Invoke(remote, accepted);

        return Task.FromResult(new PeerConnection(
            new PeerConnectionContext(
                peer,
                remote.PersonaName,
                network.AreFriends(LocalPeer.Value, peer.Value),
                DateTimeOffset.UtcNow),
            localStream));
    }

    public Task StartListeningAsync(CancellationToken token)
    {
        _listening = true;
        return Task.CompletedTask;
    }

    public Task StopListeningAsync()
    {
        _listening = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _listening = false;
        return ValueTask.CompletedTask;
    }

    /// <summary>Reads from one pipe and writes to another, so both ends see a normal stream.</summary>
    private sealed class DuplexStream(PipeReader reader, PipeWriter writer) : Stream
    {
        private readonly Stream _input = reader.AsStream();
        private readonly Stream _output = writer.AsStream();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _input.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) =>
            _output.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _output.WriteAsync(buffer, cancellationToken);

        public override void Flush() => _output.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _output.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _output.Dispose();
                _input.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _output.DisposeAsync().ConfigureAwait(false);
            await _input.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
