using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Minecraft;

public sealed class PeerDiscoveryService : IAsyncDisposable
{
    public const int ProtocolVersion = 6;
    private const int DiscoveryPort = 35655;
    private const int MaxDatagramSize = 64 * 1024;
    private const int MaxFullSubnetProbeSize = 512;
    private const int SubnetProbeBatchSize = 48;
    private const int KnownPeerBatchSize = 64;
    private const int SioUdpConnectionReset = -1744830452;
    private static readonly IPAddress IPv4MulticastGroup = IPAddress.Parse("239.255.77.67");
    private static readonly IPAddress IPv6MulticastGroup = IPAddress.Parse("ff12::4d43:5054");
    private static readonly TimeSpan BaseSendInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxSendInterval = TimeSpan.FromSeconds(4);

    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly ISelectedNetworkTransport _network;
    private readonly PeerRouteResolver _routes;
    private readonly INetworkClock _clock;
    private readonly NetworkNeighborService _neighborService = new();
    private readonly List<SenderEntry> _senders = [];
    private readonly List<UdpClient> _listeners = [];
    private readonly HashSet<string> _localAddresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, IReadOnlyList<IPAddress>> _neighborTargets = [];
    private readonly Dictionary<string, DateTimeOffset> _lastDirectedReplies = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _peerGate = new();
    private readonly object _senderGate = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Random _random = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task[] _receiveLoopTasks = [];
    private Task? _sendLoopTask;
    private bool _knownPeersDirty;
    private bool _knownPeersLoaded;
    private DateTimeOffset _lastKnownPeerSave = DateTimeOffset.MinValue;
    private DateTimeOffset _lastNeighborWarning = DateTimeOffset.MinValue;
    private Func<NetworkEndpointInfo, PeerAnnouncement>? _createAnnouncement;
    private NetworkEnvironmentSnapshot _snapshot = new();
    private IReadOnlyList<IPAddress> _dynamicTargets = Array.Empty<IPAddress>();
    private string _localIdentityId = "";
    private bool _disposed;

    public PeerDiscoveryService(
        AppPaths paths,
        Logger logger,
        ISelectedNetworkTransport network,
        PeerRouteResolver routes,
        INetworkClock? clock = null)
    {
        _paths = paths;
        _logger = logger;
        _network = network;
        _routes = routes;
        _clock = clock ?? SystemNetworkClock.Instance;
    }

    public event Action<PeerAnnouncement>? PeerUpdated;

    public async Task StartAsync(
        NetworkEnvironmentSnapshot snapshot,
        Func<NetworkEndpointInfo, PeerAnnouncement> createAnnouncement)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopCoreAsync().ConfigureAwait(false);
            LoadKnownPeers();
            PruneKnownPeers();
            _snapshot = snapshot;
            _createAnnouncement = createAnnouncement;
            _localIdentityId = snapshot.PrimaryEndpoint is null
                ? ""
                : NormalizeIdentity(createAnnouncement(snapshot.PrimaryEndpoint).IdentityId);
            _cts = new CancellationTokenSource();

            ConfigureListeners(snapshot.Endpoints);
            foreach (var endpoint in snapshot.Endpoints)
            {
                if (!IPAddress.TryParse(endpoint.NetworkAddress, out var localAddress)) continue;
                UdpClient? sender = null;
                try
                {
                    sender = _network.CreateBoundUdpClient(endpoint, 0, reuseAddress: false);
                    DisableUdpConnectionReset(sender.Client);
                    EnablePacketInformation(sender.Client, localAddress.AddressFamily);
                    if (localAddress.AddressFamily == AddressFamily.InterNetwork)
                    {
                        sender.EnableBroadcast = true;
                        sender.Ttl = 1;
                        sender.Client.SetSocketOption(
                            SocketOptionLevel.IP,
                            SocketOptionName.MulticastTimeToLive,
                            1);
                        sender.Client.SetSocketOption(
                            SocketOptionLevel.IP,
                            SocketOptionName.MulticastInterface,
                            localAddress.GetAddressBytes());
                    }
                    else
                    {
                        sender.Client.SetSocketOption(
                            SocketOptionLevel.IPv6,
                            SocketOptionName.MulticastInterface,
                            endpoint.InterfaceIndex);
                        sender.Client.SetSocketOption(
                            SocketOptionLevel.IPv6,
                            SocketOptionName.MulticastTimeToLive,
                            1);
                    }
                    var targets = BuildTargets(endpoint);
                    lock (_senderGate)
                    {
                        _senders.Add(new SenderEntry(sender, endpoint, targets));
                        _localAddresses.Add(endpoint.NetworkAddress);
                    }
                    sender = null;
                    _logger.Info(
                        $"Peer discovery sender configured for {endpoint.InterfaceName} " +
                        $"({endpoint.NetworkAddress}/{endpoint.PrefixLength}) with {targets.Count} target(s).");
                }
                catch (Exception ex)
                {
                    sender?.Dispose();
                    _logger.Warn($"Peer discovery sender init failed for '{endpoint.InterfaceName}': {ex.Message}");
                }
            }

            await RefreshDynamicTargetsAsync(_cts.Token).ConfigureAwait(false);
            _receiveLoopTasks = _listeners
                .Select(listener => ReceiveLoopAsync(listener, null, _cts.Token))
                .Concat(_senders.Select(sender =>
                    ReceiveLoopAsync(sender.Sender, sender.Endpoint, _cts.Token)))
                .ToArray();
            _sendLoopTask = SendLoopAsync(createAnnouncement, _cts.Token);
            _logger.Info($"Peer discovery v{ProtocolVersion} started on {_senders.Count} selected endpoint(s): " +
                string.Join(", ", _senders.Select(sender =>
                    $"{sender.Endpoint.InterfaceName} [{sender.Endpoint.NetworkAddress}]").Take(4)));
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
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

    private void ConfigureListeners(IReadOnlyList<NetworkEndpointInfo> endpoints)
    {
        var ipv4Endpoint = endpoints.FirstOrDefault(endpoint =>
            endpoint.AddressFamily == AddressFamily.InterNetwork);
        if (ipv4Endpoint is not null &&
            IPAddress.TryParse(ipv4Endpoint.NetworkAddress, out var ipv4Address))
        {
            UdpClient? listener = null;
            try
            {
                listener = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
                DisableUdpConnectionReset(listener.Client);
                listener.Client.ExclusiveAddressUse = false;
                listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                EnablePacketInformation(listener.Client, AddressFamily.InterNetwork);
                listener.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                try
                {
                    listener.JoinMulticastGroup(IPv4MulticastGroup, ipv4Address);
                }
                catch (SocketException ex)
                {
                    _logger.Warn($"IPv4 discovery multicast is unavailable: {ex.Message}");
                }
                _listeners.Add(listener);
                listener = null;
            }
            catch (Exception ex)
            {
                listener?.Dispose();
                if (ex is not SocketException) throw;
                _logger.Warn($"IPv4 discovery listener is unavailable: {ex.Message}");
            }
        }

        var ipv6Endpoint = endpoints.FirstOrDefault(endpoint =>
            endpoint.AddressFamily == AddressFamily.InterNetworkV6);
        if (ipv6Endpoint is null) return;

        UdpClient? ipv6Listener = null;
        try
        {
            ipv6Listener = new UdpClient(AddressFamily.InterNetworkV6);
            DisableUdpConnectionReset(ipv6Listener.Client);
            ipv6Listener.Client.DualMode = false;
            ipv6Listener.Client.ExclusiveAddressUse = false;
            ipv6Listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            EnablePacketInformation(ipv6Listener.Client, AddressFamily.InterNetworkV6);
            ipv6Listener.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, DiscoveryPort));
            try
            {
                ipv6Listener.JoinMulticastGroup(ipv6Endpoint.InterfaceIndex, IPv6MulticastGroup);
            }
            catch (SocketException ex)
            {
                _logger.Warn(
                    $"IPv6 discovery multicast is unavailable on interface " +
                    $"{ipv6Endpoint.InterfaceIndex}: {ex.Message}");
            }
            _listeners.Add(ipv6Listener);
            ipv6Listener = null;
        }
        catch (Exception ex)
        {
            ipv6Listener?.Dispose();
            if (ex is not SocketException) throw;
            _logger.Warn($"IPv6 discovery listener is unavailable: {ex.Message}");
        }
    }

    private async Task StopCoreAsync()
    {
        var cts = _cts;
        SenderEntry[] senders;
        UdpClient[] listeners;
        lock (_senderGate)
        {
            senders = _senders.ToArray();
            listeners = _listeners.ToArray();
            _senders.Clear();
            _listeners.Clear();
            _localAddresses.Clear();
        }
        var tasks = _receiveLoopTasks.Concat(_sendLoopTask is null ? [] : [_sendLoopTask]).ToArray();
        _cts = null;
        _receiveLoopTasks = [];
        _sendLoopTask = null;
        _createAnnouncement = null;
        _snapshot = new NetworkEnvironmentSnapshot();
        _dynamicTargets = Array.Empty<IPAddress>();
        _localIdentityId = "";
        lock (_peerGate)
        {
            _neighborTargets.Clear();
            _lastDirectedReplies.Clear();
        }

        cts?.Cancel();
        foreach (var listener in listeners) listener.Dispose();
        foreach (var sender in senders) sender.Sender.Dispose();
        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
            catch (Exception ex)
            {
                _logger.Warn($"Peer discovery stopped after an unexpected task error: {ex.Message}");
            }
        }
        cts?.Dispose();
        if (_knownPeersLoaded)
        {
            PersistKnownPeers(force: true);
        }
    }

    private List<DiscoveryTarget> BuildTargets(NetworkEndpointInfo endpoint)
    {
        var result = new List<DiscoveryTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (endpoint.AddressFamily == AddressFamily.InterNetwork)
        {
            Add(new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), false);
            Add(new IPEndPoint(IPv4MulticastGroup, DiscoveryPort), false);
            if (VirtualNetworkService.SupportsDirectedBroadcast(endpoint) &&
                IPAddress.TryParse(endpoint.BroadcastAddress, out var broadcast))
            {
                Add(new IPEndPoint(broadcast, DiscoveryPort), false);
            }
            foreach (var address in VirtualNetworkService.EnumerateProbeAddresses(
                         endpoint,
                         MaxFullSubnetProbeSize))
            {
                if (!IsLocalAddress(address.ToString()))
                {
                    Add(new IPEndPoint(address, DiscoveryPort), true);
                }
            }
        }
        else if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var scopedGroup = new IPAddress(IPv6MulticastGroup.GetAddressBytes(), endpoint.InterfaceIndex);
            Add(new IPEndPoint(scopedGroup, DiscoveryPort), false);
        }
        return result;

        void Add(IPEndPoint target, bool probe)
        {
            if (seen.Add(target.ToString())) result.Add(new DiscoveryTarget(target, probe));
        }
    }

    private async Task ReceiveLoopAsync(
        UdpClient listener,
        NetworkEndpointInfo? boundEndpoint,
        CancellationToken token)
    {
        var buffer = new byte[MaxDatagramSize];
        EndPoint remoteTemplate = listener.Client.AddressFamily == AddressFamily.InterNetwork
            ? new IPEndPoint(IPAddress.Any, 0)
            : new IPEndPoint(IPAddress.IPv6Any, 0);
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await listener.Client.ReceiveMessageFromAsync(
                    buffer.AsMemory(),
                    SocketFlags.None,
                    remoteTemplate,
                    token).ConfigureAwait(false);
                if (result.RemoteEndPoint is not IPEndPoint remoteEndpoint) continue;
                var receivingEndpoint = ResolveReceivingEndpoint(
                    listener.Client.AddressFamily,
                    result.PacketInformation,
                    boundEndpoint);
                if (receivingEndpoint is null) continue;

                var announcement = JsonSerializer.Deserialize<PeerAnnouncement>(
                    buffer.AsSpan(0, result.ReceivedBytes),
                    _jsonOptions);
                if (announcement?.App != "MinecraftPortable" ||
                    announcement.ProtocolVersion != ProtocolVersion ||
                    !Guid.TryParse(announcement.IdentityId, out var identity) ||
                    string.Equals(
                        identity.ToString("D"),
                        _localIdentityId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var peerAddress = ResolvePeerAddress(remoteEndpoint);
                if (peerAddress is null ||
                    IsLocalAddress(peerAddress.ToString()) ||
                    peerAddress.AddressFamily != receivingEndpoint.AddressFamily)
                {
                    continue;
                }

                announcement.NetworkAddress = peerAddress.ToString();
                announcement.LocalAddress = receivingEndpoint.NetworkAddress;
                announcement.LocalInterfaceId = receivingEndpoint.InterfaceId;
                RememberPeer(announcement, peerAddress, receivingEndpoint);
                PeerUpdated?.Invoke(announcement);
                if (!announcement.IsDirectedReply)
                {
                    await SendDirectedReplyAsync(
                        remoteEndpoint,
                        receivingEndpoint,
                        token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException && token.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Peer discovery receive failed: {ex.Message}");
            }
        }
    }

    private NetworkEndpointInfo? ResolveReceivingEndpoint(
        AddressFamily family,
        IPPacketInformation packetInformation,
        NetworkEndpointInfo? boundEndpoint)
    {
        if (boundEndpoint is not null)
        {
            return IsPacketOnSelectedEndpoint(
                    family,
                    packetInformation.Interface,
                    packetInformation.Address,
                    boundEndpoint)
                ? boundEndpoint
                : null;
        }

        if (packetInformation.Interface <= 0) return null;
        return _snapshot.Endpoints.FirstOrDefault(endpoint =>
            IsPacketOnSelectedEndpoint(
                family,
                packetInformation.Interface,
                packetInformation.Address,
                endpoint));
    }

    internal static bool IsPacketOnSelectedEndpoint(
        AddressFamily family,
        int incomingInterfaceIndex,
        IPAddress destination,
        NetworkEndpointInfo endpoint) =>
        incomingInterfaceIndex > 0 &&
        endpoint.AddressFamily == family &&
        incomingInterfaceIndex == endpoint.InterfaceIndex &&
        IsAllowedLocalDestination(destination, endpoint);

    internal static bool IsAllowedLocalDestination(
        IPAddress destination,
        NetworkEndpointInfo endpoint)
    {
        if (IPAddress.TryParse(endpoint.NetworkAddress, out var selectedAddress) &&
            destination.Equals(selectedAddress))
        {
            return true;
        }

        if (endpoint.AddressFamily == AddressFamily.InterNetwork)
        {
            return destination.Equals(IPAddress.Broadcast) ||
                   destination.Equals(IPv4MulticastGroup) ||
                   IPAddress.TryParse(endpoint.BroadcastAddress, out var directedBroadcast) &&
                   destination.Equals(directedBroadcast);
        }

        return endpoint.AddressFamily == AddressFamily.InterNetworkV6 &&
               destination.AddressFamily == AddressFamily.InterNetworkV6 &&
               destination.GetAddressBytes().AsSpan().SequenceEqual(
                   IPv6MulticastGroup.GetAddressBytes());
    }

    private async Task SendLoopAsync(
        Func<NetworkEndpointInfo, PeerAnnouncement> createAnnouncement,
        CancellationToken token)
    {
        SenderEntry[] senderSnapshot;
        lock (_senderGate) senderSnapshot = _senders.ToArray();
        if (senderSnapshot.Length == 0)
        {
            _logger.Warn("Peer discovery has no selected network endpoint.");
            return;
        }

        var tick = 0;
        while (!token.IsCancellationRequested)
        {
            tick++;
            var refreshDynamicTargets = tick % 5 == 0;
            if (refreshDynamicTargets)
            {
                await RefreshDynamicTargetsAsync(token).ConfigureAwait(false);
            }
            foreach (var entry in senderSnapshot)
            {
                if (_clock.UtcNow < entry.NextAttemptUtc) continue;
                try
                {
                    var announcement = createAnnouncement(entry.Endpoint);
                    announcement.IsDirectedReply = false;
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(announcement, _jsonOptions));
                    Exception? lastFailure = null;
                    var delivered = false;
                    foreach (var target in BuildCurrentTargets(entry, includeProbes: true))
                    {
                        try
                        {
                            await entry.Sender.SendAsync(
                                bytes,
                                target.Endpoint,
                                token).ConfigureAwait(false);
                            delivered = true;
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
                        {
                            lastFailure = ex;
                        }
                    }
                    if (!delivered && lastFailure is not null)
                    {
                        throw lastFailure;
                    }
                    entry.FailureDelay = BaseSendInterval;
                    entry.NextAttemptUtc = DateTimeOffset.MinValue;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException && token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    entry.FailureDelay = TimeSpan.FromMilliseconds(Math.Min(
                        MaxSendInterval.TotalMilliseconds,
                        Math.Max(
                            BaseSendInterval.TotalMilliseconds,
                            entry.FailureDelay.TotalMilliseconds + 700)));
                    entry.NextAttemptUtc = _clock.UtcNow + entry.FailureDelay +
                                           TimeSpan.FromMilliseconds(_random.Next(150, 450));
                    if (_clock.UtcNow - entry.LastWarningUtc >= TimeSpan.FromSeconds(15))
                    {
                        entry.LastWarningUtc = _clock.UtcNow;
                        _logger.Warn(
                            $"Peer discovery send failed for '{entry.Endpoint.InterfaceName}': {ex.Message}");
                    }
                }
            }

            var delay = BaseSendInterval + TimeSpan.FromMilliseconds(_random.Next(-120, 180));
            if (delay < TimeSpan.FromMilliseconds(600)) delay = TimeSpan.FromMilliseconds(600);
            await _clock.DelayAsync(delay, token).ConfigureAwait(false);
        }
    }

    private List<DiscoveryTarget> BuildCurrentTargets(SenderEntry entry, bool includeProbes)
    {
        var targets = new List<DiscoveryTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in entry.Targets.Where(target => !target.IsProbe))
        {
            Add(target.Endpoint, false);
        }

        if (includeProbes)
        {
            var probes = entry.Targets.Where(target => target.IsProbe).ToArray();
            var count = Math.Min(SubnetProbeBatchSize, probes.Length);
            for (var index = 0; index < count; index++)
            {
                Add(probes[(entry.ProbeCursor + index) % probes.Length].Endpoint, true);
            }
            if (probes.Length > 0) entry.ProbeCursor = (entry.ProbeCursor + count) % probes.Length;
        }

        IReadOnlyList<IPAddress> neighbors;
        lock (_peerGate)
        {
            neighbors = _neighborTargets.TryGetValue(entry.Endpoint.InterfaceIndex, out var values)
                ? values
                : Array.Empty<IPAddress>();
        }

        var knownBatch = _routes.GetDiscoveryBatch(
            entry.Endpoint,
            entry.KnownPeerCursor,
            KnownPeerBatchSize);
        entry.KnownPeerCursor = knownBatch.NextCursor;
        foreach (var item in knownBatch.Candidates)
        {
            var address = ParseAddress(item.Endpoint.Address);
            if (address is null || IsLocalAddress(address.ToString())) continue;
            Add(new IPEndPoint(address, DiscoveryPort), false);
        }

        foreach (var address in neighbors.Concat(_dynamicTargets).Distinct())
        {
            if (address.AddressFamily != entry.Endpoint.AddressFamily ||
                IsLocalAddress(address.ToString()) ||
                !VirtualNetworkService.IsUsableAddress(address))
            {
                continue;
            }
            Add(new IPEndPoint(address, DiscoveryPort), false);
        }
        return targets;

        void Add(IPEndPoint target, bool probe)
        {
            if (target.AddressFamily == entry.Endpoint.AddressFamily && seen.Add(target.ToString()))
            {
                targets.Add(new DiscoveryTarget(target, probe));
            }
        }
    }

    private async Task RefreshDynamicTargetsAsync(CancellationToken token)
    {
        Exception? lastFailure = null;
        try
        {
            var neighbors = _neighborService.GetNeighbors();
            lock (_peerGate)
            {
                _neighborTargets.Clear();
                foreach (var pair in neighbors) _neighborTargets[pair.Key] = pair.Value;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lastFailure = ex;
        }

        try
        {
            _dynamicTargets = await _network.GetDynamicPeerTargetsAsync(
                _snapshot,
                token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lastFailure = ex;
        }

        if (lastFailure is not null &&
            _clock.UtcNow - _lastNeighborWarning >= TimeSpan.FromMinutes(1))
        {
            _lastNeighborWarning = _clock.UtcNow;
            _logger.Warn($"Could not refresh network discovery targets: {lastFailure.Message}");
        }
    }

    private async Task SendDirectedReplyAsync(
        IPEndPoint peerEndpoint,
        NetworkEndpointInfo receivingEndpoint,
        CancellationToken token)
    {
        var peerAddress = peerEndpoint.Address;
        if (IsLocalAddress(peerAddress.ToString())) return;
        var factory = _createAnnouncement;
        if (factory is null) return;
        var key = $"{receivingEndpoint.InterfaceId}|{peerEndpoint}";
        var now = _clock.UtcNow;
        lock (_peerGate)
        {
            if (_lastDirectedReplies.TryGetValue(key, out var previous) &&
                now - previous < TimeSpan.FromSeconds(3))
            {
                return;
            }
            _lastDirectedReplies[key] = now;
        }

        SenderEntry? entry;
        lock (_senderGate)
        {
            entry = _senders.FirstOrDefault(sender =>
                IsSameEndpoint(sender.Endpoint, receivingEndpoint) &&
                sender.Endpoint.AddressFamily == peerAddress.AddressFamily);
        }
        if (entry is null) return;

        try
        {
            var announcement = factory(entry.Endpoint);
            announcement.IsDirectedReply = true;
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(announcement, _jsonOptions));
            await entry.Sender.SendAsync(bytes, peerEndpoint, token).ConfigureAwait(false);
            if (peerEndpoint.Port != DiscoveryPort)
            {
                await entry.Sender.SendAsync(
                    bytes,
                    new IPEndPoint(peerAddress, DiscoveryPort),
                    token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            _logger.Warn($"Peer discovery directed reply to {peerAddress} failed: {ex.Message}");
        }
    }

    private static bool IsSameEndpoint(
        NetworkEndpointInfo endpoint,
        NetworkEndpointInfo selectedEndpoint) =>
        string.Equals(endpoint.InterfaceId, selectedEndpoint.InterfaceId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            endpoint.NetworkAddress,
            selectedEndpoint.NetworkAddress,
            StringComparison.OrdinalIgnoreCase);

    private static IPAddress? ResolvePeerAddress(IPEndPoint remote) =>
        remote.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 &&
        VirtualNetworkService.IsUsableAddress(remote.Address)
            ? remote.Address
            : null;

    private void RememberPeer(
        PeerAnnouncement announcement,
        IPAddress observedAddress,
        NetworkEndpointInfo receivingEndpoint)
    {
        var now = _clock.UtcNow;
        _routes.UpsertFromAnnouncement(announcement, observedAddress, receivingEndpoint);
        var persist = now - _lastKnownPeerSave > TimeSpan.FromMinutes(5);
        lock (_peerGate) _knownPeersDirty = true;
        if (persist) PersistKnownPeers(force: true);
    }

    private void LoadKnownPeers()
    {
        try
        {
            if (!File.Exists(_paths.NetworkPeersFile))
            {
                lock (_peerGate)
                {
                    _knownPeersLoaded = true;
                    _knownPeersDirty = false;
                }
                return;
            }
            var cache = JsonSerializer.Deserialize<KnownPeerCache>(
                File.ReadAllText(_paths.NetworkPeersFile),
                _jsonOptions);
            _routes.Load(cache);
            lock (_peerGate)
            {
                _knownPeersLoaded = true;
                _knownPeersDirty = cache?.SchemaVersion == 4;
            }
        }
        catch (Exception ex)
        {
            lock (_peerGate)
            {
                _knownPeersLoaded = false;
                _knownPeersDirty = false;
            }
            _logger.Warn($"Could not read known network peers: {ex.Message}");
        }
    }

    private void PruneKnownPeers()
    {
        _routes.Prune(_clock.UtcNow);
    }

    private void PersistKnownPeers(bool force)
    {
        try
        {
            lock (_peerGate)
            {
                if (!force && !_knownPeersDirty) return;
                var cache = _routes.Export();
                AtomicFile.WriteAllText(
                    _paths.NetworkPeersFile,
                    JsonSerializer.Serialize(cache, _jsonOptions));
                _knownPeersDirty = false;
                _lastKnownPeerSave = _clock.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not write known network peers: {ex.Message}");
        }
    }

    public bool IsLocalAddress(string address)
    {
        lock (_senderGate) return _localAddresses.Contains(address);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private static string NormalizeIdentity(string? value) =>
        Guid.TryParse(value, out var identity) ? identity.ToString("D") : "";

    private static IPAddress? ParseAddress(string? value) =>
        IPAddress.TryParse(value, out var address) &&
        VirtualNetworkService.IsUsableAddress(address)
            ? address
            : null;

    private static void EnablePacketInformation(Socket socket, AddressFamily family)
    {
        socket.SetSocketOption(
            family == AddressFamily.InterNetwork ? SocketOptionLevel.IP : SocketOptionLevel.IPv6,
            SocketOptionName.PacketInformation,
            true);
    }

    private static void DisableUdpConnectionReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            socket.IOControl(
                (IOControlCode)SioUdpConnectionReset,
                [0, 0, 0, 0],
                null);
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or NotSupportedException)
        {
            // Discovery remains functional; Windows may only report ICMP errors on this socket.
        }
    }

    private sealed class SenderEntry
    {
        public SenderEntry(
            UdpClient sender,
            NetworkEndpointInfo endpoint,
            IReadOnlyList<DiscoveryTarget> targets)
        {
            Sender = sender;
            Endpoint = endpoint;
            Targets = targets;
        }

        public UdpClient Sender { get; }
        public NetworkEndpointInfo Endpoint { get; }
        public IReadOnlyList<DiscoveryTarget> Targets { get; }
        public TimeSpan FailureDelay { get; set; } = BaseSendInterval;
        public DateTimeOffset NextAttemptUtc { get; set; }
        public DateTimeOffset LastWarningUtc { get; set; } = DateTimeOffset.MinValue;
        public int ProbeCursor { get; set; }
        public int KnownPeerCursor { get; set; }
    }

    private sealed record DiscoveryTarget(IPEndPoint Endpoint, bool IsProbe);
}
