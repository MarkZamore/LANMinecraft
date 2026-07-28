using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Minecraft;

public sealed class LanAdvertisementService : IAsyncDisposable
{
    public const int MinecraftLanDiscoveryPort = 4445;
    private static readonly IPAddress MinecraftLanMulticast = IPAddress.Parse("224.0.2.60");
    private static readonly TimeSpan AdvertisementInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan MissingSessionRetention = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ActiveEndpointWindow = TimeSpan.FromSeconds(40);
    private readonly Logger _logger;
    private readonly LanRelayService _relay;
    private readonly PeerRouteResolver _routes;
    private readonly INetworkClock _clock;
    private readonly int _minecraftLanDiscoveryPort;
    private readonly object _stateGate = new();
    private readonly object _lifecycleGate = new();
    private readonly Dictionary<string, UdpClient> _senders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RemoteLanSessionState> _remoteSessions = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Task? _disposeTask;
    private bool _disposed;
    private LanAdvertisementSnapshot _snapshot = LanAdvertisementSnapshot.Empty;
    private DateTimeOffset _lastWarningAt = DateTimeOffset.MinValue;

    public LanAdvertisementService(
        Logger logger,
        LanRelayService relay,
        PeerRouteResolver routes,
        INetworkClock? clock = null)
        : this(
            logger,
            relay,
            routes,
            MinecraftLanDiscoveryPort,
            clock)
    {
    }

    internal LanAdvertisementService(
        Logger logger,
        LanRelayService relay,
        PeerRouteResolver routes,
        int minecraftLanDiscoveryPort,
        INetworkClock? clock = null)
    {
        if (minecraftLanDiscoveryPort is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minecraftLanDiscoveryPort));
        }
        _logger = logger;
        _relay = relay;
        _routes = routes;
        _minecraftLanDiscoveryPort = minecraftLanDiscoveryPort;
        _clock = clock ?? SystemNetworkClock.Instance;
    }

    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_disposed || _loopTask is not null) return;
            _cts = new CancellationTokenSource();
            _loopTask = RunAsync(_cts.Token);
        }
    }

    public LanAdvertisementDiagnosticSnapshot GetDiagnosticSnapshot()
    {
        lock (_stateGate)
        {
            return new LanAdvertisementDiagnosticSnapshot(
                _loopTask is not null && _cts is { IsCancellationRequested: false },
                _snapshot.Peers.Count(peer => peer.IsHost),
                _remoteSessions.Count,
                _senders.Count);
        }
    }

    public void Update(
        int? localPort,
        string localSessionId,
        string worldName,
        string playerName,
        IEnumerable<NetworkEndpointInfo> endpoints,
        IEnumerable<PeerViewModel> peers)
    {
        var peerSnapshots = peers.Select(peer =>
        {
            var peerEndpoints = _routes.GetSendCandidates(peer.IdentityId);
            return new RemoteLanPeer(
                peer.IdentityId,
                peer.PlayerName,
                peer.IsHost,
                peer.ServerPort,
                peer.LanSessionId,
                peer.LanWorldName,
                peer.LanRelayProtocolVersion,
                peerEndpoints);
        }).ToArray();
        var selectedRoutes = endpoints
            .Select(endpoint => new LocalRouteScope(
                endpoint.NetworkAddress,
                endpoint.InterfaceId))
            .Distinct()
            .ToArray();
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _relay.SetHostSession(localPort, localSessionId);
            lock (_stateGate)
            {
                _snapshot = new LanAdvertisementSnapshot(
                    peerSnapshots,
                    selectedRoutes);
            }
        }
        _ = worldName;
        _ = playerName;
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await SendCurrentAdvertisementsAsync(token).ConfigureAwait(false);
                await _clock.DelayAsync(AdvertisementInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Local Minecraft LAN advertisement loop failed: {ex.Message}");
        }
    }

    private async Task SendCurrentAdvertisementsAsync(CancellationToken token)
    {
        LanAdvertisementSnapshot snapshot;
        lock (_stateGate) snapshot = _snapshot;
        var now = _clock.UtcNow;

        var retainedRelays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retainedSessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visibleIdentities = snapshot.Peers
            .Where(peer => !string.IsNullOrWhiteSpace(peer.IdentityId))
            .Select(peer => peer.IdentityId.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remoteHosts = snapshot.Peers
            .Where(peer =>
                peer.IsHost &&
                peer.ServerPort is > 0 and <= 65535 &&
                !string.IsNullOrWhiteSpace(peer.IdentityId) &&
                !string.IsNullOrWhiteSpace(peer.LanSessionId))
            .GroupBy(
                peer => BuildSessionKey(peer.IdentityId, peer.LanSessionId),
                StringComparer.OrdinalIgnoreCase);
        foreach (var session in remoteHosts)
        {
            var peer = session
                .OrderByDescending(candidate => candidate.Endpoints.Count)
                .First();
            var sessionKey = session.Key;
            retainedSessions.Add(sessionKey);
            var endpoints = session
                .SelectMany(candidate => candidate.Endpoints)
                .Where(endpoint =>
                    now - endpoint.LastSeenUtc <= ActiveEndpointWindow &&
                    IsRemoteAddress(endpoint.Address) &&
                    snapshot.SelectedRoutes.Any(selected =>
                        IsRouteOnSelectedEndpoint(
                            endpoint,
                            selected.LocalAddress,
                            selected.LocalInterfaceId)))
                .GroupBy(
                    endpoint => $"{endpoint.Address}|{endpoint.LocalAddress}|{endpoint.LocalInterfaceId}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();

            RemoteLanSessionState sessionState;
            lock (_stateGate)
            {
                if (!_remoteSessions.TryGetValue(sessionKey, out sessionState!))
                {
                    sessionState = new RemoteLanSessionState(peer.IdentityId.Trim(), now);
                    _remoteSessions.Add(sessionKey, sessionState);
                }
                sessionState.LastSeenUtc = now;
            }

            if (endpoints.Length > 0)
            {
                sessionState.LastRoutableUtc = now;
                try
                {
                    var relay = await _relay.GetOrCreateClientRelayAsync(
                        peer.IdentityId,
                        peer.LanSessionId,
                        endpoints,
                        peer.ServerPort,
                        peer.RelayProtocolVersion).ConfigureAwait(false);
                    sessionState.RelayKey = relay.Key;
                    sessionState.LocalPort = relay.LocalPort;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    WarnThrottled($"Minecraft LAN relay setup failed: {ex.Message}");
                }
            }
            else
            {
                if (sessionState.LastRoutableUtc == default ||
                    now - sessionState.LastRoutableUtc >
                    MissingSessionRetention)
                {
                    sessionState.RelayKey = "";
                    sessionState.LocalPort = 0;
                }
            }

            if (string.IsNullOrWhiteSpace(sessionState.RelayKey) ||
                sessionState.LocalPort is <= 0 or > 65535)
            {
                if (endpoints.Length == 0)
                {
                    WarnThrottled($"Minecraft LAN relay has no active route to {peer.PlayerName}.");
                }
                continue;
            }

            retainedRelays.Add(sessionState.RelayKey);
            var payload = BuildPayload(
                SanitizeMotd(peer.WorldName),
                SanitizeMotd(peer.PlayerName),
                sessionState.LocalPort);
            await SendLocalAdvertisementAsync(payload, token).ConfigureAwait(false);
        }
        lock (_stateGate)
        {
            foreach (var pair in _remoteSessions.ToArray())
            {
                if (retainedSessions.Contains(pair.Key)) continue;
                var explicitlyClosed = visibleIdentities.Contains(pair.Value.IdentityId);
                if (explicitlyClosed || now - pair.Value.LastSeenUtc > MissingSessionRetention)
                {
                    _remoteSessions.Remove(pair.Key);
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(pair.Value.RelayKey))
                {
                    retainedRelays.Add(pair.Value.RelayKey);
                }
            }
        }
        await _relay.RetainClientRelaysAsync(retainedRelays).ConfigureAwait(false);
    }

    private async Task SendLocalAdvertisementAsync(byte[] payload, CancellationToken token)
    {
        UdpClient sender;
        try
        {
            sender = GetOrCreateSender(IPAddress.Loopback);
        }
        catch (Exception ex)
        {
            WarnThrottled($"Local Minecraft LAN relay sender failed: {ex.Message}");
            return;
        }

        Exception? lastFailure = null;
        var delivered = false;
        foreach (var target in GetLocalAdvertisementTargets(
                     _minecraftLanDiscoveryPort))
        {
            try
            {
                await sender.SendAsync(payload, target, token).ConfigureAwait(false);
                delivered = true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
            }
        }

        if (!delivered && lastFailure is not null)
        {
            WarnThrottled($"Local Minecraft LAN relay advertisement failed: {lastFailure.Message}");
        }
    }

    private static string BuildSessionKey(string identityId, string sessionId) =>
        $"{identityId.Trim()}|{sessionId.Trim()}";

    private UdpClient GetOrCreateSender(IPAddress localAddress)
    {
        var key = localAddress.ToString();
        lock (_stateGate)
        {
            if (_senders.TryGetValue(key, out var existing)) return existing;
            UdpClient? sender = null;
            try
            {
                sender = new UdpClient(new IPEndPoint(localAddress, 0));
                if (localAddress.Equals(IPAddress.Loopback))
                {
                    sender.Client.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.MulticastInterface,
                        localAddress.GetAddressBytes());
                    sender.Client.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.MulticastTimeToLive,
                        0);
                    sender.MulticastLoopback = true;
                }
                _senders[key] = sender;
                return sender;
            }
            catch
            {
                sender?.Dispose();
                throw;
            }
        }
    }

    private void WarnThrottled(string message)
    {
        var now = _clock.UtcNow;
        if (now - _lastWarningAt < TimeSpan.FromSeconds(30)) return;
        _lastWarningAt = now;
        _logger.Warn(message);
    }

    internal static byte[] BuildPayload(string worldName, string playerName, int port)
    {
        var normalizedWorldName = string.Equals(
            worldName.Trim(),
            "Minecraft LAN",
            StringComparison.OrdinalIgnoreCase)
            ? ""
            : SanitizeMotd(worldName);
        var metadata = string.Join(":",
            "MinecraftPortable",
            EncodeMetadata(normalizedWorldName),
            EncodeMetadata(SanitizeMotd(playerName)));
        return Encoding.UTF8.GetBytes($"[MOTD]{metadata}[/MOTD][AD]{port}[/AD]");
    }

    internal Task PublishOnceAsync(CancellationToken token = default) =>
        SendCurrentAdvertisementsAsync(token);

    internal static IReadOnlyList<IPEndPoint> GetLocalAdvertisementTargets(
        int port = MinecraftLanDiscoveryPort) =>
    [
        new(IPAddress.Loopback, port),
        new(MinecraftLanMulticast, port)
    ];

    internal static bool IsRouteOnSelectedEndpoint(
        PeerCandidateEndpoint route,
        string selectedLocalAddress,
        string selectedInterfaceId) =>
        string.Equals(
            route.LocalAddress,
            selectedLocalAddress,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            route.LocalInterfaceId,
            selectedInterfaceId,
            StringComparison.OrdinalIgnoreCase);

    private static string EncodeMetadata(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool IsRemoteAddress(string? value) =>
        IPAddress.TryParse(value, out var address) &&
        VirtualNetworkService.IsUsableAddress(address);

    private static string SanitizeMotd(string value)
    {
        var result = string.IsNullOrWhiteSpace(value) ? "Minecraft LAN" : value.Trim();
        return result.Replace("[", "(", StringComparison.Ordinal).Replace("]", ")", StringComparison.Ordinal);
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposed = true;
            var cts = _cts;
            var task = _loopTask;
            _cts = null;
            _loopTask = null;
            cts?.Cancel();
            _disposeTask = DisposeCoreAsync(cts, task);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(CancellationTokenSource? cts, Task? task)
    {
        await Task.Yield();
        try
        {
            if (task is not null)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Local Minecraft LAN advertisement stopped after an error: {ex.Message}");
                }
            }
        }
        finally
        {
            cts?.Dispose();
            lock (_stateGate)
            {
                foreach (var sender in _senders.Values) sender.Dispose();
                _senders.Clear();
                _remoteSessions.Clear();
                _snapshot = LanAdvertisementSnapshot.Empty;
            }
        }
    }

    private sealed record RemoteLanPeer(
        string IdentityId,
        string PlayerName,
        bool IsHost,
        int ServerPort,
        string LanSessionId,
        string WorldName,
        int RelayProtocolVersion,
        IReadOnlyList<PeerCandidateEndpoint> Endpoints);

    private sealed record LanAdvertisementSnapshot(
        IReadOnlyList<RemoteLanPeer> Peers,
        IReadOnlyList<LocalRouteScope> SelectedRoutes)
    {
        public static LanAdvertisementSnapshot Empty { get; } =
            new([], []);
    }

    private sealed record LocalRouteScope(
        string LocalAddress,
        string LocalInterfaceId);

    private sealed class RemoteLanSessionState(string identityId, DateTimeOffset lastSeenUtc)
    {
        public string IdentityId { get; } = identityId;
        public DateTimeOffset LastSeenUtc { get; set; } = lastSeenUtc;
        public DateTimeOffset LastRoutableUtc { get; set; }
        public string RelayKey { get; set; } = "";
        public int LocalPort { get; set; }
    }
}

public sealed record LanAdvertisementDiagnosticSnapshot(
    bool IsRunning,
    int AdvertisedHostCount,
    int RemoteSessionCount,
    int LocalSenderCount);
