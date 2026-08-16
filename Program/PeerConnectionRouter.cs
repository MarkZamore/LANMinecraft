using System.Collections.Concurrent;
using System.IO;

namespace Minecraft;

/// <summary>
/// Reads the first frame of every accepted peer connection and hands it to the
/// protocol that claims it - the same multiplexing the shared TCP port used to
/// do inside the world-transfer listener, minus the sockets.
///
/// World transfer is the fallback handler because its own frames do not carry a
/// "protocol" field; that quirk predates the multiplexer and is kept so old and
/// new builds still recognise each other's transfer handshake.
/// </summary>
public sealed class PeerConnectionRouter : IAsyncDisposable
{
    /// <summary>The first frame is a small handshake; anything larger is a mistake or an attack.</summary>
    internal const int MaxInitialFrameBytes = 64 * 1024;
    internal static readonly TimeSpan InitialFrameTimeout = TimeSpan.FromSeconds(10);
    private const int MaxConcurrentConnections = 32;

    private readonly IPeerTransport _transport;
    private readonly Logger? _logger;
    private readonly ConcurrentDictionary<string, IPortableProtocolHandler> _handlers =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connectionGate = new(MaxConcurrentConnections, MaxConcurrentConnections);
    private readonly CancellationTokenSource _shutdownCts = new();
    private IPortableProtocolHandler? _fallback;
    private bool _started;
    private bool _disposed;

    public PeerConnectionRouter(IPeerTransport transport, Logger? logger = null)
    {
        _transport = transport;
        _logger = logger;
        _transport.ConnectionAccepted += OnConnectionAccepted;
    }

    /// <summary>Registers a protocol; the last registration for a name wins.</summary>
    public void Register(IPortableProtocolHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[handler.ProtocolName] = handler;
    }

    /// <summary>Handles connections whose first frame names no known protocol.</summary>
    public void RegisterFallback(IPortableProtocolHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _fallback = handler;
    }

    public async Task StartAsync(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        _started = true;
        await _transport.StartListeningAsync(token).ConfigureAwait(false);
    }

    private void OnConnectionAccepted(object? sender, PeerConnection connection)
    {
        _ = Task.Run(() => DispatchAsync(connection));
    }

    private async Task DispatchAsync(PeerConnection connection)
    {
        await using var scope = connection;
        var token = _shutdownCts.Token;
        if (!await _connectionGate.WaitAsync(TimeSpan.Zero, token).ConfigureAwait(false))
        {
            _logger?.Warn(
                $"Rejected a connection from {connection.Context.PeerId}: too many concurrent peer connections.");
            return;
        }

        try
        {
            // Only friends can reach us; the transport authenticates the
            // account, so this is the whole authorisation check.
            if (!connection.Context.IsFriend)
            {
                _logger?.Warn(
                    $"Rejected a connection from {connection.Context.PeerId}: not a Steam friend.");
                return;
            }

            using var initialTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            initialTimeout.CancelAfter(InitialFrameTimeout);
            var initialFrame = await PortableProtocol.ReadFrameAsync(
                connection.Stream,
                initialTimeout.Token,
                MaxInitialFrameBytes).ConfigureAwait(false);

            var protocol = PortableProtocol.ReadProtocol(initialFrame);
            var handler = !string.IsNullOrEmpty(protocol) && _handlers.TryGetValue(protocol, out var registered)
                ? registered
                : _fallback;
            if (handler is null)
            {
                _logger?.Warn(
                    $"No handler for protocol '{protocol}' from {connection.Context.PeerId}.");
                return;
            }

            await handler.HandleAsync(
                connection.Stream,
                initialFrame,
                connection.Context,
                token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
        {
            _logger?.Warn($"Peer connection from {connection.Context.PeerId} failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Peer connection from {connection.Context.PeerId} faulted: {ex}");
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.ConnectionAccepted -= OnConnectionAccepted;
        await _shutdownCts.CancelAsync().ConfigureAwait(false);
        await _transport.StopListeningAsync().ConfigureAwait(false);
        _shutdownCts.Dispose();
        _connectionGate.Dispose();
    }
}
