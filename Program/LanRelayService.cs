using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Minecraft;

public sealed class LanRelayService : IAsyncDisposable
{
    public const string ProtocolName = "MinecraftPortableLanRelay";
    public const int ProtocolVersion = 1;
    private readonly Logger _logger;
    private readonly ILanRelayPeerConnector _peerConnector;
    private readonly PeerRouteResolver _routes;
    private readonly SemaphoreSlim _clientRelayGate = new(1, 1);
    private readonly Dictionary<string, ClientRelay> _clientRelays = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _hostSessionGate = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private HostSession _hostSession = HostSession.Empty;
    private CancellationTokenSource _hostSessionCts = CreateCanceledTokenSource();
    private volatile bool _disposed;

    public LanRelayService(Logger logger, ISelectedNetworkTransport network, PeerRouteResolver routes)
        : this(logger, routes, new SelectedNetworkLanRelayPeerConnector(network))
    {
    }

    internal LanRelayService(
        Logger logger,
        PeerRouteResolver routes,
        ILanRelayPeerConnector peerConnector)
    {
        _logger = logger;
        _routes = routes;
        _peerConnector = peerConnector;
    }

    public void SetHostSession(int? port, string? sessionId)
    {
        var normalizedSessionId = sessionId?.Trim() ?? "";
        var next = port is > 0 and <= 65535 &&
                   !string.IsNullOrWhiteSpace(normalizedSessionId)
            ? new HostSession(port.Value, normalizedSessionId)
            : HostSession.Empty;
        CancellationTokenSource previousCts;
        lock (_hostSessionGate)
        {
            if (_disposed) return;
            if (_hostSession == next) return;
            previousCts = _hostSessionCts;
            _hostSession = next;
            _hostSessionCts = next == HostSession.Empty
                ? CreateCanceledTokenSource()
                : new CancellationTokenSource();
        }
        previousCts.Cancel();
        previousCts.Dispose();
    }

    public async Task<ClientLanRelayInfo> GetOrCreateClientRelayAsync(
        string peerId,
        string lanSessionId,
        IReadOnlyList<PeerCandidateEndpoint> endpoints,
        int remoteLanPort)
    {
        if (remoteLanPort is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(remoteLanPort));
        var targets = endpoints
            .Select(CreateTarget)
            .Where(target => target is not null)
            .Cast<LanRelayTarget>()
            .Distinct()
            .ToArray();
        if (targets.Length == 0) throw new ArgumentException("LAN relay has no valid peer endpoint.", nameof(endpoints));
        var key = BuildKey(peerId, lanSessionId);
        await _clientRelayGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_clientRelays.TryGetValue(key, out var existing))
            {
                existing.UpdateTargets(targets, remoteLanPort, lanSessionId);
                return new ClientLanRelayInfo(key, existing.LocalPort);
            }

            var previousSessions = _clientRelays
                .Where(pair => string.Equals(
                    pair.Value.PeerId,
                    peerId.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (var previous in previousSessions)
            {
                _clientRelays.Remove(previous.Key);
            }

            var preferredLocalPort = previousSessions
                .Select(pair => pair.Value.LocalPort)
                .FirstOrDefault();
            foreach (var previous in previousSessions)
            {
                await previous.Value.DisposeAsync().ConfigureAwait(false);
            }

            var relay = new ClientRelay(
                peerId,
                targets,
                remoteLanPort,
                lanSessionId,
                preferredLocalPort,
                _logger,
                _jsonOptions,
                _peerConnector,
                _routes);
            _clientRelays.Add(key, relay);
            return new ClientLanRelayInfo(key, relay.LocalPort);
        }
        finally
        {
            _clientRelayGate.Release();
        }
    }

    public async Task RetainClientRelaysAsync(IReadOnlySet<string> activeKeys)
    {
        var removed = new List<ClientRelay>();
        await _clientRelayGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            foreach (var pair in _clientRelays.ToArray())
            {
                if (activeKeys.Contains(pair.Key)) continue;
                _clientRelays.Remove(pair.Key);
                removed.Add(pair.Value);
            }
        }
        finally
        {
            _clientRelayGate.Release();
        }
        foreach (var relay in removed) await relay.DisposeAsync().ConfigureAwait(false);
    }

    public async Task HandleIncomingAsync(Stream stream, byte[] initialFrame, CancellationToken token)
    {
        var request = PortableProtocol.Deserialize<LanRelayRequest>(initialFrame, _jsonOptions);
        using var hostSession = CaptureHostSession(token);
        if (request is null || request.Protocol != ProtocolName || request.ProtocolVersion != ProtocolVersion ||
            request.ServerPort is <= 0 or > 65535 || request.ServerPort != hostSession.Session.Port ||
            string.IsNullOrWhiteSpace(request.LanSessionId) ||
            !string.Equals(
                request.LanSessionId,
                hostSession.Session.SessionId,
                StringComparison.Ordinal))
        {
            await PortableProtocol.WriteJsonAsync(stream, new LanRelayReply
            {
                Ok = false,
                Message = "LAN session is not available."
            }, _jsonOptions, token).ConfigureAwait(false);
            return;
        }

        TcpClient? minecraft = null;
        var readySent = false;
        var sessionToken = hostSession.Token;
        try
        {
            minecraft = await ConnectLocalMinecraftAsync(
                hostSession.Session.Port,
                sessionToken).ConfigureAwait(false);
            if (!IsCurrentHostSession(hostSession))
            {
                throw new OperationCanceledException(sessionToken);
            }
            await PortableProtocol.WriteJsonAsync(
                stream,
                new LanRelayReply { Ok = true },
                _jsonOptions,
                sessionToken).ConfigureAwait(false);
            readySent = true;
            await RelayBidirectionalAsync(
                stream,
                minecraft.GetStream(),
                sessionToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            _logger.Warn($"Incoming Minecraft LAN relay failed: {ex.Message}");
            try
            {
                if (!readySent)
                {
                    await PortableProtocol.WriteJsonAsync(stream, new LanRelayReply
                    {
                        Ok = false,
                        Message = "Could not reach the local LAN world."
                    }, _jsonOptions, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
            }
        }
        catch (OperationCanceledException) when (
            sessionToken.IsCancellationRequested ||
            !IsCurrentHostSession(hostSession))
        {
        }
        finally
        {
            minecraft?.Dispose();
        }
    }

    private static async Task<TcpClient> ConnectLocalMinecraftAsync(int port, CancellationToken token)
    {
        Exception? lastError = null;
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            var client = new TcpClient(address.AddressFamily);
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                attempt.CancelAfter(TimeSpan.FromSeconds(3));
                await client.ConnectAsync(address, port, attempt.Token).ConfigureAwait(false);
                return client;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                client.Dispose();
                if (token.IsCancellationRequested) throw;
                lastError = ex;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        throw new IOException("Could not reach the local Minecraft listener.", lastError);
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource hostSessionCts;
        lock (_hostSessionGate)
        {
            if (_disposed) return;
            _disposed = true;
            _hostSession = HostSession.Empty;
            hostSessionCts = _hostSessionCts;
        }
        hostSessionCts.Cancel();
        ClientRelay[] relays;
        await _clientRelayGate.WaitAsync().ConfigureAwait(false);
        try
        {
            relays = _clientRelays.Values.ToArray();
            _clientRelays.Clear();
        }
        finally
        {
            _clientRelayGate.Release();
        }
        foreach (var relay in relays) await relay.DisposeAsync().ConfigureAwait(false);
        hostSessionCts.Dispose();
    }

    private HostSessionContext CaptureHostSession(CancellationToken externalToken)
    {
        lock (_hostSessionGate)
        {
            if (_disposed)
            {
                return new HostSessionContext(
                    HostSession.Empty,
                    CreateCanceledTokenSource());
            }
            return new HostSessionContext(
                _hostSession,
                CancellationTokenSource.CreateLinkedTokenSource(
                    externalToken,
                    _hostSessionCts.Token));
        }
    }

    private bool IsCurrentHostSession(HostSessionContext expected)
    {
        lock (_hostSessionGate)
        {
            return !expected.Token.IsCancellationRequested &&
                   _hostSession == expected.Session;
        }
    }

    private static CancellationTokenSource CreateCanceledTokenSource()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        return cts;
    }

    private static string BuildKey(
        string peerId,
        string lanSessionId) =>
        string.IsNullOrWhiteSpace(peerId)
            ? throw new ArgumentException("LAN relay requires a peer identity.", nameof(peerId))
            : string.IsNullOrWhiteSpace(lanSessionId)
                ? throw new ArgumentException("LAN relay requires a session id.", nameof(lanSessionId))
                : $"{peerId.Trim()}|{lanSessionId.Trim()}";

    private static LanRelayTarget? CreateTarget(PeerCandidateEndpoint endpoint)
    {
        if (!IPAddress.TryParse(endpoint.Address, out var remoteAddress) ||
            !VirtualNetworkService.IsUsableAddress(remoteAddress) ||
            !IPAddress.TryParse(endpoint.LocalAddress, out var localAddress) ||
            !VirtualNetworkService.IsUsableAddress(localAddress) ||
            localAddress.AddressFamily != remoteAddress.AddressFamily ||
            string.IsNullOrWhiteSpace(endpoint.LocalInterfaceId))
        {
            return null;
        }

        return new LanRelayTarget(
            remoteAddress,
            localAddress.ToString(),
            endpoint.LocalInterfaceId.Trim());
    }

    private static async Task RelayBidirectionalAsync(Stream first, Stream second, CancellationToken token)
    {
        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var firstToSecond = first.CopyToAsync(second, relayCts.Token);
        var secondToFirst = second.CopyToAsync(first, relayCts.Token);
        await Task.WhenAny(firstToSecond, secondToFirst).ConfigureAwait(false);
        relayCts.Cancel();
        try
        {
            await Task.WhenAll(firstToSecond, secondToFirst).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    private sealed class ClientRelay : IAsyncDisposable
    {
        private readonly string _peerId;
        private readonly object _targetGate = new();
        private IReadOnlyList<LanRelayTarget> _targets;
        private int _remoteLanPort;
        private string _lanSessionId;
        private readonly Logger _logger;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly ILanRelayPeerConnector _peerConnector;
        private readonly PeerRouteResolver _routes;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptTask;
        private readonly ConcurrentDictionary<long, Task> _clientTasks = new();
        private long _nextClientTaskId;
        private int _disposeStarted;

        public ClientRelay(
            string peerId,
            IReadOnlyList<LanRelayTarget> targets,
            int remoteLanPort,
            string lanSessionId,
            int preferredLocalPort,
            Logger logger,
            JsonSerializerOptions jsonOptions,
            ILanRelayPeerConnector peerConnector,
            PeerRouteResolver routes)
        {
            _peerId = peerId.Trim();
            _targets = targets;
            _remoteLanPort = remoteLanPort;
            _lanSessionId = lanSessionId;
            _logger = logger;
            _jsonOptions = jsonOptions;
            _peerConnector = peerConnector;
            _routes = routes;
            _listener = StartLocalListener(preferredLocalPort);
            LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptTask = AcceptLoopAsync(_cts.Token);
        }

        public int LocalPort { get; }
        public string PeerId => _peerId;

        private static TcpListener StartLocalListener(int preferredPort)
        {
            if (preferredPort is > 0 and <= 65535)
            {
                var preferred = new TcpListener(IPAddress.Loopback, preferredPort);
                try
                {
                    preferred.Start();
                    return preferred;
                }
                catch (SocketException)
                {
                    preferred.Stop();
                }
                catch
                {
                    preferred.Stop();
                    throw;
                }
            }

            var fallback = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                fallback.Start();
                return fallback;
            }
            catch
            {
                fallback.Stop();
                throw;
            }
        }

        public void UpdateTargets(
            IReadOnlyList<LanRelayTarget> targets,
            int remoteLanPort,
            string lanSessionId)
        {
            lock (_targetGate)
            {
                _targets = targets.ToArray();
                _remoteLanPort = remoteLanPort;
                _lanSessionId = lanSessionId?.Trim() ?? "";
            }
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    var taskId = Interlocked.Increment(ref _nextClientTaskId);
                    var task = HandleClientAsync(client, token);
                    _clientTasks[taskId] = task;
                    _ = ObserveClientTaskAsync(taskId, task);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    if (!token.IsCancellationRequested) _logger.Warn($"Local Minecraft LAN relay listener failed: {ex.Message}");
                    break;
                }
            }
        }

        private async Task ObserveClientTaskAsync(long taskId, Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
            {
            }
            catch (Exception ex)
            {
                _logger.Warn($"Local Minecraft LAN relay task failed: {ex.Message}");
            }
            finally
            {
                _clientTasks.TryRemove(taskId, out _);
            }
        }

        private async Task HandleClientAsync(TcpClient localClient, CancellationToken token)
        {
            using (localClient)
            {
                var failures = new List<string>();
                IReadOnlyList<LanRelayTarget> targets;
                int remoteLanPort;
                string lanSessionId;
                lock (_targetGate)
                {
                    targets = _targets.ToArray();
                    remoteLanPort = _remoteLanPort;
                    lanSessionId = _lanSessionId;
                }

                foreach (var target in targets)
                {
                    try
                    {
                        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        connectCts.CancelAfter(TimeSpan.FromSeconds(4));
                        using var remoteClient = await _peerConnector.ConnectAsync(
                            target,
                            WorldTransferService.TransferPort,
                            connectCts.Token).ConfigureAwait(false);
                        await using var remoteStream = remoteClient.GetStream();
                        await PortableProtocol.WriteJsonAsync(remoteStream, new LanRelayRequest
                        {
                            ServerPort = remoteLanPort,
                            LanSessionId = lanSessionId
                        }, _jsonOptions, connectCts.Token).ConfigureAwait(false);
                        var replyFrame = await PortableProtocol.ReadFrameAsync(
                            remoteStream,
                            connectCts.Token).ConfigureAwait(false);
                        var reply = PortableProtocol.Deserialize<LanRelayReply>(replyFrame, _jsonOptions);
                        if (reply is null || reply.Protocol != ProtocolName ||
                            reply.ProtocolVersion != ProtocolVersion || !reply.Ok)
                        {
                            throw new IOException(reply?.Message ?? "Remote LAN relay was rejected.");
                        }
                        _routes.MarkEndpointHealthy(_peerId, target.ToCandidate());
                        await RelayBidirectionalAsync(
                            localClient.GetStream(),
                            remoteStream,
                            token).ConfigureAwait(false);
                        return;
                    }
                    catch (Exception ex) when (
                        ex is SocketException or
                        IOException or
                        OperationCanceledException or
                        InvalidDataException or
                        JsonException)
                    {
                        if (token.IsCancellationRequested) return;
                        _routes.MarkEndpointUnhealthy(_peerId, target.ToCandidate());
                        failures.Add($"{target.Address}: {ex.Message}");
                    }
                }
                _logger.Warn("Minecraft LAN relay failed: " + string.Join("; ", failures.Take(3)));
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
            try
            {
                _cts.Cancel();
                _listener.Stop();
                try
                {
                    await _acceptTask.ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    ex is OperationCanceledException or
                    ObjectDisposedException or
                    SocketException)
                {
                }

                var clientTasks = _clientTasks.Values.ToArray();
                if (clientTasks.Length == 0) return;
                try
                {
                    await Task.WhenAll(clientTasks).ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    ex is OperationCanceledException or
                    IOException or
                    SocketException)
                {
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Minecraft LAN relay shutdown observed a failed client task: {ex.Message}");
                }
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }

    private sealed class LanRelayRequest
    {
        public string Protocol { get; set; } = ProtocolName;
        public int ProtocolVersion { get; set; } = LanRelayService.ProtocolVersion;
        public int ServerPort { get; set; }
        public string LanSessionId { get; set; } = "";
    }

    private sealed class LanRelayReply
    {
        public string Protocol { get; set; } = ProtocolName;
        public int ProtocolVersion { get; set; } = LanRelayService.ProtocolVersion;
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
    }

    private sealed record HostSession(int Port, string SessionId)
    {
        public static HostSession Empty { get; } = new(0, "");
    }

    private sealed class HostSessionContext(
        HostSession session,
        CancellationTokenSource cancellation) : IDisposable
    {
        public HostSession Session { get; } = session;
        public CancellationToken Token => cancellation.Token;
        public void Dispose() => cancellation.Dispose();
    }
}

public sealed record ClientLanRelayInfo(string Key, int LocalPort);

internal sealed record LanRelayTarget(
    IPAddress Address,
    string LocalAddress,
    string LocalInterfaceId)
{
    public PeerCandidateEndpoint ToCandidate() => new()
    {
        Address = Address.ToString(),
        LocalAddress = LocalAddress,
        LocalInterfaceId = LocalInterfaceId
    };
}
