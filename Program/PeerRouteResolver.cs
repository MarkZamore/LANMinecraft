using System.Net;
using System.Net.Sockets;

namespace Minecraft;

public interface IPeerRouteResolver
{
    IReadOnlyList<PeerCandidateEndpoint> GetSendCandidates(
        string? identityId,
        AddressFamily? addressFamily = null);

    bool IsKnownEndpoint(
        string? identityId,
        IPAddress address,
        string? localInterfaceId);

    void MarkEndpointHealthy(string identityId, PeerCandidateEndpoint endpoint);
    void MarkEndpointUnhealthy(string identityId, PeerCandidateEndpoint endpoint);
}

public sealed class PeerCandidateEndpoint : IEquatable<PeerCandidateEndpoint>
{
    public required string Address { get; init; }
    public string LocalAddress { get; set; } = "";
    public string LocalInterfaceId { get; set; } = "";
    public string AddressFamily { get; set; } = "";
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSuccessUtc { get; set; }
    public bool IsObserved { get; set; }
    public bool IsConfirmed { get; set; }
    public int FailureScore { get; set; }

    public PeerCandidateEndpoint Copy() => new()
    {
        Address = Address,
        LocalAddress = LocalAddress,
        LocalInterfaceId = LocalInterfaceId,
        AddressFamily = AddressFamily,
        LastSeenUtc = LastSeenUtc,
        LastSuccessUtc = LastSuccessUtc,
        IsObserved = IsObserved,
        IsConfirmed = IsConfirmed,
        FailureScore = FailureScore
    };

    public bool Equals(PeerCandidateEndpoint? other) =>
        other is not null &&
        string.Equals(Address, other.Address, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(LocalAddress, other.LocalAddress, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(LocalInterfaceId, other.LocalInterfaceId, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as PeerCandidateEndpoint);

    public override int GetHashCode() => HashCode.Combine(
        Address.Trim().ToLowerInvariant(),
        LocalAddress.Trim().ToLowerInvariant(),
        LocalInterfaceId.Trim().ToLowerInvariant());
}

public sealed record PeerDiscoveryCandidate(string IdentityId, PeerCandidateEndpoint Endpoint);

public sealed record PeerDiscoveryBatch(
    IReadOnlyList<PeerDiscoveryCandidate> Candidates,
    int NextCursor);

public sealed class PeerRouteResolver : IPeerRouteResolver
{
    private const int CurrentCacheSchemaVersion = KnownPeerCache.CurrentSchemaVersion;
    private static readonly TimeSpan ConfirmedEndpointTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan ObservedEndpointTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan CandidateEndpointTtl = TimeSpan.FromMinutes(15);
    private readonly Dictionary<string, PeerRouteState> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly INetworkClock _clock;
    private PeerDiscoveryCandidate[] _discoverySnapshot = [];
    private bool _discoverySnapshotDirty = true;

    public PeerRouteResolver(INetworkClock? clock = null)
    {
        _clock = clock ?? SystemNetworkClock.Instance;
    }

    public void UpsertFromAnnouncement(
        PeerAnnouncement announcement,
        IPAddress observedAddress,
        NetworkEndpointInfo receivingEndpoint)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        ArgumentNullException.ThrowIfNull(observedAddress);
        ArgumentNullException.ThrowIfNull(receivingEndpoint);
        if (!Guid.TryParse(announcement.IdentityId, out var identity) ||
            !IsUsableAddress(observedAddress) ||
            observedAddress.AddressFamily != receivingEndpoint.AddressFamily ||
            !IPAddress.TryParse(receivingEndpoint.NetworkAddress, out var localAddress))
        {
            return;
        }

        var identityId = identity.ToString("D");
        var now = _clock.UtcNow;
        var key = BuildEndpointKey(observedAddress, receivingEndpoint.InterfaceId);
        lock (_gate)
        {
            if (!_peers.TryGetValue(identityId, out var peer))
            {
                peer = new PeerRouteState(identityId, now);
                _peers.Add(identityId, peer);
            }

            peer.PlayerName = announcement.PlayerName?.Trim() ?? "";
            peer.LastSeenUtc = now;
            if (!peer.Endpoints.TryGetValue(key, out var endpoint))
            {
                endpoint = new PeerCandidateEndpoint { Address = observedAddress.ToString() };
                peer.Endpoints.Add(key, endpoint);
            }

            endpoint.LocalAddress = localAddress.ToString();
            endpoint.LocalInterfaceId = receivingEndpoint.InterfaceId;
            endpoint.AddressFamily = GetAddressFamilyName(observedAddress.AddressFamily);
            endpoint.LastSeenUtc = now;
            endpoint.IsObserved = true;
            endpoint.IsConfirmed = true;
            endpoint.FailureScore = 0;
            _discoverySnapshotDirty = true;
            PruneLocked(now);
        }
    }

    public IReadOnlyList<PeerCandidateEndpoint> GetSendCandidates(
        string? identityId,
        AddressFamily? addressFamily = null)
    {
        identityId = NormalizeIdentity(identityId);
        if (string.IsNullOrWhiteSpace(identityId)) return Array.Empty<PeerCandidateEndpoint>();

        lock (_gate)
        {
            if (!_peers.TryGetValue(identityId, out var peer)) return Array.Empty<PeerCandidateEndpoint>();
            var now = _clock.UtcNow;
            return peer.Endpoints.Values
                .Where(endpoint => !IsExpired(endpoint, now))
                .Where(endpoint =>
                    endpoint.IsConfirmed &&
                    !string.IsNullOrWhiteSpace(endpoint.LocalAddress) &&
                    !string.IsNullOrWhiteSpace(endpoint.LocalInterfaceId))
                .Where(endpoint => addressFamily is null || GetAddressFamily(endpoint) == addressFamily)
                .OrderByDescending(endpoint => endpoint.IsConfirmed)
                .ThenBy(endpoint => endpoint.FailureScore)
                .ThenByDescending(endpoint => endpoint.LastSuccessUtc)
                .ThenByDescending(endpoint => endpoint.IsObserved)
                .ThenByDescending(endpoint => endpoint.LastSeenUtc)
                .ThenBy(endpoint => GetAddressFamily(endpoint) == AddressFamily.InterNetwork ? 0 : 1)
                .ThenBy(endpoint => endpoint.Address, StringComparer.OrdinalIgnoreCase)
                .Select(endpoint => endpoint.Copy())
                .ToArray();
        }
    }

    public PeerDiscoveryBatch GetDiscoveryBatch(
        NetworkEndpointInfo localEndpoint,
        int cursor,
        int maxCount)
    {
        ArgumentNullException.ThrowIfNull(localEndpoint);
        maxCount = Math.Max(0, maxCount);
        if (maxCount == 0) return new PeerDiscoveryBatch([], Math.Max(0, cursor));

        lock (_gate)
        {
            PruneLocked(_clock.UtcNow);
            EnsureDiscoverySnapshotLocked();
            if (_discoverySnapshot.Length == 0) return new PeerDiscoveryBatch([], 0);

            var normalizedCursor = (cursor & int.MaxValue) % _discoverySnapshot.Length;
            var result = new List<PeerDiscoveryCandidate>(Math.Min(maxCount, _discoverySnapshot.Length));
            var visited = 0;
            while (visited < _discoverySnapshot.Length && result.Count < maxCount)
            {
                var candidate = _discoverySnapshot[(normalizedCursor + visited) % _discoverySnapshot.Length];
                if (MatchesLocalEndpoint(candidate.Endpoint, localEndpoint))
                {
                    result.Add(new PeerDiscoveryCandidate(candidate.IdentityId, candidate.Endpoint.Copy()));
                }
                visited++;
            }

            return new PeerDiscoveryBatch(
                result,
                (normalizedCursor + Math.Max(1, visited)) % _discoverySnapshot.Length);
        }
    }

    public bool IsKnownEndpoint(
        string? identityId,
        IPAddress address,
        string? localInterfaceId)
    {
        identityId = NormalizeIdentity(identityId);
        if (string.IsNullOrWhiteSpace(identityId) || !IsUsableAddress(address)) return false;
        lock (_gate)
        {
            if (!_peers.TryGetValue(identityId, out var peer)) return false;
            return peer.Endpoints.Values.Any(endpoint =>
                endpoint.IsConfirmed &&
                string.Equals(endpoint.Address, address.ToString(), StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(endpoint.LocalInterfaceId) ||
                 string.IsNullOrWhiteSpace(localInterfaceId) ||
                 string.Equals(
                     endpoint.LocalInterfaceId,
                     localInterfaceId,
                     StringComparison.OrdinalIgnoreCase)));
        }
    }

    public void MarkEndpointHealthy(string identityId, PeerCandidateEndpoint endpoint)
    {
        identityId = NormalizeIdentity(identityId);
        if (string.IsNullOrWhiteSpace(identityId)) return;
        lock (_gate)
        {
            if (!TryGetEndpointLocked(identityId, endpoint, out var existing)) return;
            existing.FailureScore = 0;
            existing.IsConfirmed = true;
            existing.LastSeenUtc = _clock.UtcNow;
            existing.LastSuccessUtc = existing.LastSeenUtc;
            _discoverySnapshotDirty = true;
        }
    }

    public void MarkEndpointUnhealthy(string identityId, PeerCandidateEndpoint endpoint)
    {
        identityId = NormalizeIdentity(identityId);
        if (string.IsNullOrWhiteSpace(identityId)) return;
        lock (_gate)
        {
            if (!TryGetEndpointLocked(identityId, endpoint, out var existing)) return;
            existing.FailureScore = Math.Min(9, existing.FailureScore + 1);
            _discoverySnapshotDirty = true;
        }
    }

    public KnownPeerCache Export()
    {
        lock (_gate)
        {
            PruneLocked(_clock.UtcNow);
            return new KnownPeerCache
            {
                SchemaVersion = CurrentCacheSchemaVersion,
                Peers = _peers.Values
                    .OrderByDescending(peer => peer.LastSeenUtc)
                    .Select(peer => new KnownPeerIdentityRecord
                    {
                        IdentityId = peer.IdentityId,
                        PlayerName = peer.PlayerName,
                        Endpoints = peer.Endpoints.Values.Select(endpoint => new KnownPeerEndpointRecord
                        {
                            Address = endpoint.Address,
                            LocalAddress = endpoint.LocalAddress,
                            LocalInterfaceId = endpoint.LocalInterfaceId,
                            LastSeenUtc = endpoint.LastSeenUtc,
                            LastSuccessUtc = endpoint.LastSuccessUtc,
                            IsObserved = endpoint.IsObserved,
                            IsConfirmed = endpoint.IsConfirmed,
                            FailureScore = endpoint.FailureScore
                        }).ToList()
                    }).ToList()
            };
        }
    }

    public void Load(KnownPeerCache? cache)
    {
        if (cache is null || cache.SchemaVersion is not (4 or CurrentCacheSchemaVersion)) return;
        var isLegacy = cache.SchemaVersion < CurrentCacheSchemaVersion;
        var now = _clock.UtcNow;
        lock (_gate)
        {
            _peers.Clear();
            foreach (var record in cache.Peers ?? [])
            {
                if (!Guid.TryParse(record.IdentityId, out var identity)) continue;
                var identityId = identity.ToString("D");
                var peer = new PeerRouteState(identityId, now)
                {
                    PlayerName = record.PlayerName?.Trim() ?? ""
                };

                foreach (var stored in record.Endpoints ?? [])
                {
                    var address = ParseAddress(stored.Address);
                    if (address is null) continue;
                    var localAddress = isLegacy ? "" : NormalizeLocalAddress(stored.LocalAddress, address.AddressFamily);
                    var localInterfaceId = isLegacy ? "" : stored.LocalInterfaceId?.Trim() ?? "";
                    var endpoint = new PeerCandidateEndpoint
                    {
                        Address = address.ToString(),
                        LocalAddress = localAddress,
                        LocalInterfaceId = localInterfaceId,
                        AddressFamily = GetAddressFamilyName(address.AddressFamily),
                        LastSeenUtc = isLegacy ? now : stored.LastSeenUtc,
                        LastSuccessUtc = isLegacy ? default : stored.LastSuccessUtc,
                        IsObserved = !isLegacy && stored.IsObserved,
                        IsConfirmed = !isLegacy && stored.IsConfirmed,
                        FailureScore = isLegacy ? 0 : Math.Clamp(stored.FailureScore, 0, 9)
                    };
                    peer.Endpoints[BuildEndpointKey(address, localInterfaceId)] = endpoint;
                }

                if (peer.Endpoints.Count == 0) continue;
                peer.LastSeenUtc = peer.Endpoints.Values.Max(endpoint => endpoint.LastSeenUtc);
                _peers[identityId] = peer;
            }
            _discoverySnapshotDirty = true;
            PruneLocked(now);
        }
    }

    public void Prune(DateTimeOffset now)
    {
        lock (_gate) PruneLocked(now);
    }

    private void PruneLocked(DateTimeOffset now)
    {
        var changed = false;
        foreach (var peer in _peers.Values.ToArray())
        {
            foreach (var pair in peer.Endpoints
                         .Where(pair => IsExpired(pair.Value, now))
                         .ToArray())
            {
                peer.Endpoints.Remove(pair.Key);
                changed = true;
            }
            if (peer.Endpoints.Count == 0)
            {
                _peers.Remove(peer.IdentityId);
                changed = true;
            }
        }
        if (changed) _discoverySnapshotDirty = true;
    }

    private void EnsureDiscoverySnapshotLocked()
    {
        if (!_discoverySnapshotDirty) return;
        var now = _clock.UtcNow;
        _discoverySnapshot = _peers.Values
            .SelectMany(peer => peer.Endpoints.Values
                .Where(endpoint => !IsExpired(endpoint, now))
                .Select(endpoint => new PeerDiscoveryCandidate(peer.IdentityId, endpoint.Copy())))
            .OrderByDescending(candidate => candidate.Endpoint.IsConfirmed)
            .ThenByDescending(candidate => candidate.Endpoint.LastSuccessUtc)
            .ThenByDescending(candidate => candidate.Endpoint.LastSeenUtc)
            .ThenBy(candidate => candidate.IdentityId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Endpoint.Address, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _discoverySnapshotDirty = false;
    }

    private static bool MatchesLocalEndpoint(
        PeerCandidateEndpoint candidate,
        NetworkEndpointInfo localEndpoint)
    {
        if (GetAddressFamily(candidate) != localEndpoint.AddressFamily) return false;
        if (!string.IsNullOrWhiteSpace(candidate.LocalInterfaceId) &&
            !string.Equals(
                candidate.LocalInterfaceId,
                localEndpoint.InterfaceId,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(candidate.LocalAddress) ||
               string.Equals(
                   candidate.LocalAddress,
                   localEndpoint.NetworkAddress,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExpired(PeerCandidateEndpoint endpoint, DateTimeOffset now)
    {
        var ttl = endpoint.IsConfirmed || endpoint.LastSuccessUtc != default
            ? ConfirmedEndpointTtl
            : endpoint.IsObserved
                ? ObservedEndpointTtl
                : CandidateEndpointTtl;
        return now - endpoint.LastSeenUtc > ttl;
    }

    private bool TryGetEndpointLocked(
        string identityId,
        PeerCandidateEndpoint candidate,
        out PeerCandidateEndpoint endpoint)
    {
        endpoint = null!;
        if (!_peers.TryGetValue(identityId, out var peer) ||
            !IPAddress.TryParse(candidate.Address, out var address))
        {
            return false;
        }

        var key = BuildEndpointKey(address, candidate.LocalInterfaceId);
        return peer.Endpoints.TryGetValue(key, out endpoint!);
    }

    private static string BuildEndpointKey(IPAddress address, string? localInterfaceId) =>
        string.Join("|",
            localInterfaceId?.Trim().ToLowerInvariant() ?? "",
            address.ToString().ToLowerInvariant());

    private static AddressFamily GetAddressFamily(PeerCandidateEndpoint endpoint)
    {
        if (IPAddress.TryParse(endpoint.Address, out var address)) return address.AddressFamily;
        return endpoint.AddressFamily.Equals("IPv6", StringComparison.OrdinalIgnoreCase)
            ? AddressFamily.InterNetworkV6
            : AddressFamily.InterNetwork;
    }

    private static string GetAddressFamilyName(AddressFamily family) =>
        family == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";

    private static string NormalizeIdentity(string? identityId) =>
        Guid.TryParse(identityId, out var identity) ? identity.ToString("D") : "";

    private static IPAddress? ParseAddress(string? value) =>
        TryGetUsableAddress(value, out var address) ? address : null;

    private static string NormalizeLocalAddress(string? value, AddressFamily family) =>
        IPAddress.TryParse(value, out var address) && address.AddressFamily == family &&
        VirtualNetworkService.IsUsableAddress(address)
            ? address.ToString()
            : "";

    private static bool TryGetUsableAddress(string? value, out IPAddress address)
    {
        address = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim().Trim('[', ']', ',', ';', '"');
        var slash = normalized.IndexOf('/');
        if (slash > 0) normalized = normalized[..slash];
        return IPAddress.TryParse(normalized, out address!) && IsUsableAddress(address);
    }

    private static bool IsUsableAddress(IPAddress address) =>
        VirtualNetworkService.IsUsableAddress(address);

    private sealed class PeerRouteState
    {
        public PeerRouteState(string identityId, DateTimeOffset now)
        {
            IdentityId = identityId;
            LastSeenUtc = now;
        }

        public string IdentityId { get; }
        public string PlayerName { get; set; } = "";
        public DateTimeOffset LastSeenUtc { get; set; }
        public Dictionary<string, PeerCandidateEndpoint> Endpoints { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
