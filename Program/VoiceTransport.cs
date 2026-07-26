using System.Net;
using System.Net.Sockets;

namespace Minecraft;

public sealed class VoiceTransport : IAsyncDisposable, IDisposable
{
    public const int VoicePort = 35657;
    private const int SioUdpConnectionReset = -1744830452;
    private readonly Logger _logger;
    private readonly ISelectedNetworkTransport _network;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly List<TransportSocket> _sockets = [];
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private DateTimeOffset _lastWarningAt = DateTimeOffset.MinValue;
    private long _packetsSent;
    private long _packetsReceived;
    private long _bytesSent;
    private long _bytesReceived;
    private long _errors;
    private long _listenerStarts;

    public VoiceTransport(Logger logger, ISelectedNetworkTransport network)
    {
        _logger = logger;
        _network = network;
    }

    public VoiceTransportDiagnosticSnapshot GetDiagnosticSnapshot() => new(
        Interlocked.Read(ref _packetsSent),
        Interlocked.Read(ref _packetsReceived),
        Interlocked.Read(ref _bytesSent),
        Interlocked.Read(ref _bytesReceived),
        Interlocked.Read(ref _errors),
        Math.Max(0, Interlocked.Read(ref _listenerStarts) - 1));

    public void StartListening(
        IPAddress listenAddress,
        int port,
        Func<IPEndPoint, byte[], string, string, Task> onPacketReceived)
    {
        ArgumentNullException.ThrowIfNull(listenAddress);
        ArgumentNullException.ThrowIfNull(onPacketReceived);

        _lifecycleGate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopCoreAsync().AsTask().GetAwaiter().GetResult();
            StartListeningCore(listenAddress, port, onPacketReceived);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void StartListeningCore(
        IPAddress listenAddress,
        int port,
        Func<IPEndPoint, byte[], string, string, Task> onPacketReceived)
    {
        Interlocked.Increment(ref _listenerStarts);
        var cts = new CancellationTokenSource();
        var useNetworkEndpoints = listenAddress.Equals(IPAddress.Any) ||
                                  listenAddress.Equals(IPAddress.IPv6Any);
        var endpoints = _network.GetSnapshot().Endpoints
            .Where(endpoint =>
                useNetworkEndpoints ||
                string.Equals(
                    endpoint.NetworkAddress,
                    listenAddress.ToString(),
                    StringComparison.OrdinalIgnoreCase))
            .GroupBy(endpoint => endpoint.NetworkAddress, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var configured = new List<TransportSocket>();
        try
        {
            foreach (var endpoint in endpoints)
            {
                TryConfigureSocket(endpoint, port, onPacketReceived, cts, configured);
            }
            if (configured.Count == 0)
            {
                throw new SocketException((int)SocketError.AddressNotAvailable);
            }
            lock (_gate)
            {
                _cts = cts;
                _sockets.AddRange(configured);
            }
        }
        catch
        {
            cts.Cancel();
            foreach (var socket in configured)
            {
                DisposeTransportSocket(socket);
            }
            WaitForReceiveTasks(configured);
            cts.Dispose();
            throw;
        }
    }

    private void TryConfigureSocket(
        NetworkEndpointInfo endpoint,
        int port,
        Func<IPEndPoint, byte[], string, string, Task> onPacketReceived,
        CancellationTokenSource cts,
        ICollection<TransportSocket> configured)
    {
        UdpClient? udp = null;
        VoiceQosSession? qos = null;
        try
        {
            udp = _network.CreateBoundUdpClient(endpoint, port, reuseAddress: true);
            DisableUdpConnectionReset(udp.Client);
            ConfigureBuffers(udp);
            qos = VoiceQosSession.AttachBestEffort(udp.Client, _logger);
            var receiveTask = ReceiveLoopAsync(
                udp,
                endpoint.NetworkAddress,
                endpoint.InterfaceId,
                onPacketReceived,
                cts.Token);
            configured.Add(new TransportSocket(
                udp,
                qos,
                receiveTask,
                endpoint.AddressFamily,
                endpoint.NetworkAddress,
                endpoint.InterfaceId));
            udp = null;
            qos = null;
        }
        catch (Exception ex)
        {
            qos?.Dispose();
            udp?.Dispose();
            if (ex is not SocketException) throw;
            _logger.Warn($"Voice listener on {endpoint.NetworkAddress} is unavailable: {ex.Message}");
        }
    }

    private static void ConfigureBuffers(UdpClient udp)
    {
        udp.Client.SendBufferSize = 512 * 1024;
        udp.Client.ReceiveBufferSize = 512 * 1024;
    }

    private static void DisableUdpConnectionReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            socket.IOControl((IOControlCode)SioUdpConnectionReset, [0, 0, 0, 0], null);
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or NotSupportedException)
        {
            // Voice remains usable; Windows may surface ICMP errors on this socket.
        }
    }

    private async Task ReceiveLoopAsync(
        UdpClient udp,
        string localAddress,
        string localInterfaceId,
        Func<IPEndPoint, byte[], string, string, Task> onPacketReceived,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(token).ConfigureAwait(false);
                Interlocked.Increment(ref _packetsReceived);
                Interlocked.Add(ref _bytesReceived, result.Buffer.Length);
                await onPacketReceived(
                    result.RemoteEndPoint,
                    result.Buffer,
                    localAddress,
                    localInterfaceId).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (
                token.IsCancellationRequested ||
                IsClosedSocketError(ex.SocketErrorCode))
            {
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errors);
                WarnThrottled("Voice receive failed: " + ex.Message);
                try
                {
                    await Task.Delay(100, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public Task SendAsync(IEnumerable<IPEndPoint> targets, byte[] payload, CancellationToken token) =>
        SendAsync(targets.Select(target => new VoiceRouteTarget(target, "", "")), payload, token);

    public async Task SendAsync(
        IEnumerable<VoiceRouteTarget> targets,
        byte[] payload,
        CancellationToken token)
    {
        TransportSocket[] sockets;
        lock (_gate) sockets = _sockets.ToArray();
        if (payload.Length == 0 || sockets.Length == 0) return;

        foreach (var target in targets.Distinct())
        {
            var transport = SelectTransport(sockets, target);
            if (transport is null) continue;
            try
            {
                transport.Qos.AddDestination(target.EndPoint);
                await transport.Udp.SendAsync(payload, target.EndPoint, token).ConfigureAwait(false);
                Interlocked.Increment(ref _packetsSent);
                Interlocked.Add(ref _bytesSent, payload.Length);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errors);
                WarnThrottled($"Voice send to {target.EndPoint.Address} failed: {ex.Message}");
            }
        }
    }

    private static TransportSocket? SelectTransport(
        IReadOnlyList<TransportSocket> sockets,
        VoiceRouteTarget target)
    {
        var family = target.EndPoint.AddressFamily;
        var familySockets = sockets
            .Where(socket => socket.AddressFamily == family)
            .ToArray();
        if (familySockets.Length == 0) return null;

        if (!string.IsNullOrWhiteSpace(target.LocalAddress))
        {
            return familySockets.FirstOrDefault(socket =>
                string.Equals(socket.LocalAddress, target.LocalAddress, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(target.LocalInterfaceId) ||
                 string.IsNullOrWhiteSpace(socket.LocalInterfaceId) ||
                 string.Equals(socket.LocalInterfaceId, target.LocalInterfaceId, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(target.LocalInterfaceId))
        {
            return familySockets.FirstOrDefault(socket =>
                string.Equals(socket.LocalInterfaceId, target.LocalInterfaceId, StringComparison.OrdinalIgnoreCase));
        }

        return familySockets.Length == 1 ? familySockets[0] : null;
    }

    public async ValueTask StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async ValueTask StopCoreAsync()
    {
        CancellationTokenSource? cts;
        TransportSocket[] sockets;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            sockets = _sockets.ToArray();
            _sockets.Clear();
        }
        cts?.Cancel();
        foreach (var socket in sockets)
        {
            DisposeTransportSocket(socket);
        }
        try
        {
            await Task.WhenAll(sockets.Select(socket => socket.ReceiveTask)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
        finally
        {
            cts?.Dispose();
        }
    }

    private static void DisposeTransportSocket(TransportSocket socket)
    {
        try
        {
            socket.Qos.Dispose();
        }
        finally
        {
            socket.Udp.Dispose();
        }
    }

    internal Task GetReceiveCompletionTask()
    {
        lock (_gate)
        {
            return Task.WhenAll(_sockets.Select(socket => socket.ReceiveTask));
        }
    }

    private static void WaitForReceiveTasks(IEnumerable<TransportSocket> sockets)
    {
        try
        {
            Task.WhenAll(sockets.Select(socket => socket.ReceiveTask))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private void WarnThrottled(string message)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastWarningAt < TimeSpan.FromSeconds(15)) return;
        _lastWarningAt = now;
        _logger.Warn(message);
    }

    private static bool IsClosedSocketError(SocketError error) =>
        error is SocketError.OperationAborted or
            SocketError.Interrupted or
            SocketError.NotSocket or
            SocketError.Shutdown;

    public void Dispose()
    {
        _lifecycleGate.Wait();
        try
        {
            if (!_disposed)
            {
                _disposed = true;
                StopCoreAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_disposed)
            {
                _disposed = true;
                await StopCoreAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
        GC.SuppressFinalize(this);
    }

    private sealed record TransportSocket(
        UdpClient Udp,
        VoiceQosSession Qos,
        Task ReceiveTask,
        AddressFamily AddressFamily,
        string LocalAddress,
        string LocalInterfaceId);
}

public sealed record VoiceTransportDiagnosticSnapshot(
    long PacketsSent,
    long PacketsReceived,
    long BytesSent,
    long BytesReceived,
    long Errors,
    long ListenerReconnects);

public sealed record VoiceRouteTarget(
    IPEndPoint EndPoint,
    string LocalAddress,
    string LocalInterfaceId);

public sealed record VoicePeerCandidate(
    string PeerId,
    string Address,
    string LocalAddress = "",
    string LocalInterfaceId = "");

public sealed class VoiceRuntimeOptions
{
    public int Port { get; init; } = VoiceTransport.VoicePort;
    public IPAddress ListenAddress { get; init; } = IPAddress.IPv6Any;

    public void Validate()
    {
        if (Port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
        if (ListenAddress.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            throw new ArgumentException("Voice listen address must be IPv4 or IPv6.", nameof(ListenAddress));
        }
    }
}
