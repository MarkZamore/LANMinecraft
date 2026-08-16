using System;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Minecraft;

public sealed class AppSettings
{
    /// <summary>
    /// 1 = the VPN/voice era (no version field on disk); 2 = Steam era. The
    /// launcher backs a file up once before it first writes the new shape, so
    /// an older build can be restored by hand.
    /// </summary>
    public int SchemaVersion { get; set; } = SettingsService.CurrentSchemaVersion;

    public string PlayerName { get; set; } = "";
    public string PreviousPlayerName { get; set; } = "";

    [JsonIgnore]
    public string LocalIdentityId { get; set; } = "";

    [JsonIgnore]
    public string LocalIdentityName { get; set; } = "";

    public int MaxMemoryGb { get; set; } = 16;

    public string ClientRelativePath { get; set; } = "";
    public string SkinPath { get; set; } = "";
    public string SelectedWorldRelativePath { get; set; } = "";
    public string SelectedNetworkInterfaceId { get; set; } = "";
    public string SelectedNetworkAddress { get; set; } = "";
}

public sealed class NetworkEndpointInfo
{
    public required string InterfaceId { get; init; }
    public int InterfaceIndex { get; init; }
    public required string InterfaceName { get; init; }
    public string Description { get; init; } = "";
    public required string NetworkAddress { get; init; }
    public int PrefixLength { get; init; }
    public string BroadcastAddress { get; init; } = "";
    public bool IsHardware { get; init; }
    public bool IsFilterInterface { get; init; }
    public bool IsEndpointInterface { get; init; }
    public bool HasDefaultRoute { get; init; }
    public int SortPriority { get; init; } = 50;

    [JsonIgnore]
    public AddressFamily AddressFamily => IPAddress.TryParse(NetworkAddress, out var address)
        ? address.AddressFamily
        : AddressFamily.Unspecified;

    [JsonIgnore]
    public string DisplayName => $"{InterfaceName} - {NetworkAddress}";
}

public sealed class NetworkEnvironmentSnapshot
{
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<NetworkEndpointInfo> Endpoints { get; init; } = Array.Empty<NetworkEndpointInfo>();
    public IReadOnlyList<NetworkEndpointInfo> AvailableEndpoints { get; init; } =
        Array.Empty<NetworkEndpointInfo>();
    public NetworkEndpointInfo? PrimaryEndpoint { get; init; }

    public string Fingerprint =>
        $"primary={FormatEndpoint(PrimaryEndpoint)}";

    public string TopologyFingerprint => string.Join(
        "|",
        (AvailableEndpoints.Count == 0 ? Endpoints : AvailableEndpoints)
        .OrderBy(endpoint => endpoint.InterfaceId, StringComparer.OrdinalIgnoreCase)
        .ThenBy(endpoint => endpoint.NetworkAddress, StringComparer.OrdinalIgnoreCase)
        .Select(FormatEndpoint)) + $"|{Fingerprint}";

    private static string FormatEndpoint(NetworkEndpointInfo? endpoint) =>
        endpoint is null
            ? string.Empty
            : string.Join(
                "@",
                endpoint.InterfaceId,
                endpoint.NetworkAddress,
                endpoint.PrefixLength,
                endpoint.InterfaceIndex,
                endpoint.IsHardware,
                endpoint.IsFilterInterface,
                endpoint.IsEndpointInterface,
                endpoint.HasDefaultRoute);
}

public sealed record DiagnosticLogTargetOption(
    string IdentityId,
    string DisplayName,
    string NetworkAddress,
    string TlsFingerprint)
{
    public static DiagnosticLogTargetOption Nobody { get; } =
        new(string.Empty, "Никому", string.Empty, string.Empty);

    public bool IsNobody => string.IsNullOrWhiteSpace(IdentityId);
}

public sealed class PeerEndpointInfo
{
    public required string Address { get; init; }
    public string LocalAddress { get; set; } = "";
    public string LocalInterfaceId { get; set; } = "";
    public string AddressFamily { get; set; } = "";
    public bool IsHost { get; set; }
    public int ServerPort { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

public sealed class KnownPeerCache
{
    public const int CurrentSchemaVersion = 5;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<KnownPeerIdentityRecord> Peers { get; set; } = [];
}

public sealed class KnownPeerIdentityRecord
{
    public string IdentityId { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public List<KnownPeerEndpointRecord> Endpoints { get; set; } = [];
}

public sealed class KnownPeerEndpointRecord
{
    public string Address { get; set; } = "";
    public string LocalAddress { get; set; } = "";
    public string LocalInterfaceId { get; set; } = "";
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSuccessUtc { get; set; }
    public bool IsObserved { get; set; }
    public bool IsConfirmed { get; set; }
    public int FailureScore { get; set; }
}

public sealed class PeerAnnouncement
{
    public string App { get; set; } = "MinecraftPortable";
    public int ProtocolVersion { get; set; }
    public string PlayerName { get; set; } = "";
    public string IdentityId { get; set; } = "";
    public string IdentityName { get; set; } = "";

    // These fields are derived from packet information after reception. They are
    // deliberately never accepted from, or sent to, a remote peer.
    [JsonIgnore]
    public string NetworkAddress { get; set; } = "";
    [JsonIgnore]
    public string LocalAddress { get; set; } = "";
    [JsonIgnore]
    public string LocalInterfaceId { get; set; } = "";

    public bool IsDirectedReply { get; set; }
    public bool IsHost { get; set; }
    public string PackHash { get; set; } = "";
    public int ServerPort { get; set; }
    public string LanSessionId { get; set; } = "";
    public string LanWorldName { get; set; } = "";
    public int LanRelayProtocolVersion { get; set; }
    public bool IsMinecraftRunning { get; set; }
    public bool IsMinecraftPreparing { get; set; }
    public bool IsSkinAvailable { get; set; }
    public string SkinSha256 { get; set; } = "";
    public string SkinModel { get; set; } = "classic";
    public string HostedWorldId { get; set; } = "";
    public int WaypointProtocolVersion { get; set; }
    public List<WaypointProviderAnnouncement> WaypointProviders { get; set; } = [];
    public int DiagnosticLogProtocolVersion { get; set; }
    public string DiagnosticTlsFingerprint { get; set; } = "";
}

public sealed class WaypointProviderAnnouncement
{
    public string ProviderId { get; set; } = "";
    public string ModVersion { get; set; } = "";
    public string WorldContextId { get; set; } = "";
}

public sealed class PeerViewModel : INotifyPropertyChanged
{
    private readonly Dictionary<string, PeerEndpointInfo> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private string _playerName = "";
    private string _networkAddress = "";
    private string _primaryEndpointKey = "";
    private string _identityId = "";
    private string _identityName = "";
    private bool _isHost;
    private string _packHash = "";
    private int _serverPort;
    private string _lanSessionId = "";
    private string _lanWorldName = "";
    private int _lanRelayProtocolVersion;
    private bool _isMinecraftRunning;
    private bool _isMinecraftPreparing;
    private bool _isSkinAvailable;
    private string _skinSha256 = "";
    private string _skinModel = "classic";
    private DateTimeOffset _lastSeen;
    private string _localPackHash = "";
    private int? _lastRttMs;
    private DateTimeOffset _lastRttAt;
    private string _hostedWorldId = "";
    private int _waypointProtocolVersion;
    private IReadOnlyList<WaypointProviderAnnouncement> _waypointProviders = Array.Empty<WaypointProviderAnnouncement>();
    private int _diagnosticLogProtocolVersion;
    private string _diagnosticTlsFingerprint = "";

    public string PlayerName
    {
        get => _playerName;
        set
        {
            if (Set(ref _playerName, value))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(HostDisplayName));
            }
        }
    }

    public string NetworkAddress
    {
        get => _networkAddress;
        set
        {
            if (Set(ref _networkAddress, value))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(HostDisplayName));
            }
        }
    }

    public string IdentityId
    {
        get => _identityId;
        set => Set(ref _identityId, value);
    }

    public string IdentityName
    {
        get => _identityName;
        set => Set(ref _identityName, value);
    }

    public bool IsHost
    {
        get => _isHost;
        set
        {
            if (Set(ref _isHost, value))
            {
                OnPropertyChanged(nameof(HostDisplayName));
            }
        }
    }

    public string PackHash { get => _packHash; set { if (Set(ref _packHash, value)) OnPropertyChanged(nameof(PackStatus)); } }

    public bool IsMinecraftRunning
    {
        get => _isMinecraftRunning;
        set => Set(ref _isMinecraftRunning, value);
    }

    public bool IsMinecraftPreparing
    {
        get => _isMinecraftPreparing;
        set => Set(ref _isMinecraftPreparing, value);
    }

    public bool IsSkinAvailable
    {
        get => _isSkinAvailable;
        set => Set(ref _isSkinAvailable, value);
    }

    public string SkinSha256
    {
        get => _skinSha256;
        set => Set(ref _skinSha256, value ?? "");
    }

    public string SkinModel
    {
        get => _skinModel;
        set => Set(ref _skinModel, string.Equals(value, "slim", StringComparison.OrdinalIgnoreCase) ? "slim" : "classic");
    }

    public int ServerPort
    {
        get => _serverPort;
        set
        {
            if (Set(ref _serverPort, value))
            {
                OnPropertyChanged(nameof(HostDisplayName));
            }
        }
    }
    public string LanSessionId { get => _lanSessionId; set => Set(ref _lanSessionId, value ?? ""); }
    public string LanWorldName { get => _lanWorldName; set => Set(ref _lanWorldName, value ?? ""); }
    public int LanRelayProtocolVersion
    {
        get => _lanRelayProtocolVersion;
        set => Set(ref _lanRelayProtocolVersion, value);
    }
    public bool SupportsResumableLanRelay =>
        LanRelayProtocolVersion >= LanRelayService.ResumableProtocolVersion;
    public DateTimeOffset LastSeen { get => _lastSeen; set { if (Set(ref _lastSeen, value)) OnPropertyChanged(nameof(LastSeenText)); } }
    public string LocalPackHash { get => _localPackHash; set { if (Set(ref _localPackHash, value)) OnPropertyChanged(nameof(PackStatus)); } }
    public int? LastRttMs
    {
        get => _lastRttMs;
        set
        {
            if (Set(ref _lastRttMs, value))
            {
                OnPropertyChanged(nameof(HostDisplayName));
                OnPropertyChanged(nameof(RttDisplay));
            }
        }
    }
    public DateTimeOffset LastRttAt { get => _lastRttAt; set => Set(ref _lastRttAt, value); }
    public string HostedWorldId { get => _hostedWorldId; set => Set(ref _hostedWorldId, value); }
    public int WaypointProtocolVersion { get => _waypointProtocolVersion; set => Set(ref _waypointProtocolVersion, value); }
    public IReadOnlyList<WaypointProviderAnnouncement> WaypointProviders
    {
        get => _waypointProviders;
        set => Set(ref _waypointProviders, value ?? Array.Empty<WaypointProviderAnnouncement>());
    }
    public int DiagnosticLogProtocolVersion
    {
        get => _diagnosticLogProtocolVersion;
        set => Set(ref _diagnosticLogProtocolVersion, value);
    }
    public string DiagnosticTlsFingerprint
    {
        get => _diagnosticTlsFingerprint;
        set => Set(ref _diagnosticTlsFingerprint, value?.Trim() ?? "");
    }
    public bool SupportsDiagnosticLogs =>
        DiagnosticLogProtocolVersion == PeerSupportProtocol.ProtocolVersion &&
        DiagnosticTlsFingerprint.Length == 64;

    public string DisplayName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(PlayerName) ? "Неизвестный игрок" : PlayerName;
            var address = AddressDisplay;
            return string.IsNullOrWhiteSpace(address) ? name : $"{name} - {address}";
        }
    }

    public string AddressDisplay
    {
        get
        {
            _endpoints.TryGetValue(_primaryEndpointKey, out var primary);
            if (primary is null) return NetworkAddress;

            var grouped = _endpoints.Values
                .Where(endpoint => IsSameDisplayGroup(endpoint, primary))
                .OrderByDescending(endpoint => endpoint.LastSeen)
                .ToArray();
            var ipv4 = grouped.FirstOrDefault(endpoint => IsAddressFamily(endpoint, AddressFamily.InterNetwork))?.Address;
            var ipv6 = grouped.FirstOrDefault(endpoint => IsAddressFamily(endpoint, AddressFamily.InterNetworkV6))?.Address;
            if (!string.IsNullOrWhiteSpace(ipv4) && !string.IsNullOrWhiteSpace(ipv6)) return $"{ipv4} ({ipv6})";
            return ipv4 ?? ipv6 ?? NetworkAddress;
        }
    }

    public string HostDisplayName
    {
        get
        {
            var baseName = DisplayName;
            if (IsHost && ServerPort > 0)
            {
                var hostName = $"{baseName}:{ServerPort}";
                return LastRttMs is null ? $"{hostName} (—)" : $"{hostName} ({LastRttMs} ms)";
            }

            return baseName;
        }
    }

    public string PackStatus
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PackHash) || PackHash == "missing") return "missing";
            if (string.IsNullOrWhiteSpace(LocalPackHash) || LocalPackHash == "missing") return "local missing";
            return string.Equals(PackHash, LocalPackHash, StringComparison.OrdinalIgnoreCase) ? "OK" : "MISMATCH";
        }
    }

    public string RttDisplay => LastRttMs is null ? "—" : $"{LastRttMs} ms";
    public string LastSeenText => LastSeen == default ? "" : LastSeen.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(PeerAnnouncement announcement, string localPackHash)
    {
        PlayerName = announcement.PlayerName;
        IdentityId = announcement.IdentityId;
        IdentityName = announcement.IdentityName;
        IsMinecraftRunning = announcement.IsMinecraftRunning;
        IsMinecraftPreparing = announcement.IsMinecraftPreparing;
        IsSkinAvailable = announcement.IsSkinAvailable;
        SkinSha256 = announcement.SkinSha256;
        SkinModel = announcement.SkinModel;
        HostedWorldId = announcement.HostedWorldId;
        WaypointProtocolVersion = announcement.WaypointProtocolVersion;
        WaypointProviders = announcement.WaypointProviders?.ToArray() ?? Array.Empty<WaypointProviderAnnouncement>();
        DiagnosticLogProtocolVersion = announcement.DiagnosticLogProtocolVersion;
        DiagnosticTlsFingerprint = announcement.DiagnosticTlsFingerprint;
        PackHash = announcement.PackHash;
        LanSessionId = announcement.LanSessionId;
        LanWorldName = announcement.LanWorldName;
        LanRelayProtocolVersion = announcement.LanRelayProtocolVersion;
        LocalPackHash = localPackHash;
        var now = DateTimeOffset.Now;
        LastSeen = now;

        if (IPAddress.TryParse(announcement.NetworkAddress, out var observedAddress) &&
            VirtualNetworkService.IsUsableAddress(observedAddress))
        {
            var endpointKey = GetEndpointKey(
                observedAddress.ToString(),
                announcement.LocalInterfaceId);
            if (!_endpoints.TryGetValue(endpointKey, out var endpoint))
            {
                endpoint = new PeerEndpointInfo { Address = observedAddress.ToString() };
                _endpoints[endpointKey] = endpoint;
            }

            endpoint.LocalAddress = announcement.LocalAddress?.Trim() ?? "";
            endpoint.LocalInterfaceId = announcement.LocalInterfaceId?.Trim() ?? "";
            endpoint.AddressFamily = observedAddress.AddressFamily == AddressFamily.InterNetworkV6
                ? "IPv6"
                : "IPv4";
            endpoint.IsHost = announcement.IsHost;
            endpoint.ServerPort = announcement.ServerPort;
            endpoint.LastSeen = now;
        }

        SelectPrimaryEndpoint();
        NotifyAddressDisplayChanged();
    }

    public bool PruneEndpoints(DateTimeOffset cutoff)
    {
        foreach (var pair in _endpoints.Where(pair => pair.Value.LastSeen < cutoff).ToArray())
        {
            _endpoints.Remove(pair.Key);
        }

        SelectPrimaryEndpoint();
        NotifyAddressDisplayChanged();
        return _endpoints.Count > 0;
    }

    public bool TryGetObservedRemoteAddress(
        string? selectedLocalAddress,
        string? selectedLocalInterfaceId,
        DateTimeOffset cutoff,
        out string remoteAddress)
    {
        remoteAddress = string.Empty;
        if (string.IsNullOrWhiteSpace(selectedLocalInterfaceId) ||
            !IPAddress.TryParse(selectedLocalAddress, out var localAddress))
        {
            return false;
        }

        var observed = _endpoints.Values
            .Select(endpoint => new
            {
                Endpoint = endpoint,
                RemoteAddress = IPAddress.TryParse(endpoint.Address, out var remote)
                    ? remote
                    : null,
                LocalAddress = IPAddress.TryParse(endpoint.LocalAddress, out var local)
                    ? local
                    : null
            })
            .Where(item =>
                item.Endpoint.LastSeen >= cutoff &&
                item.RemoteAddress is not null &&
                item.LocalAddress is not null &&
                VirtualNetworkService.IsUsableAddress(item.RemoteAddress) &&
                !IPAddress.IsLoopback(item.RemoteAddress) &&
                item.LocalAddress.Equals(localAddress) &&
                string.Equals(
                    item.Endpoint.LocalInterfaceId,
                    selectedLocalInterfaceId,
                    StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Endpoint.LastSeen)
            .ThenBy(item => item.RemoteAddress!.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ThenBy(item => item.RemoteAddress!.ToString(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (observed?.RemoteAddress is null)
        {
            return false;
        }

        remoteAddress = observed.RemoteAddress.ToString();
        return true;
    }

    public void SetLocalEndpoints(
        IEnumerable<NetworkEndpointInfo> endpoints,
        NetworkEndpointInfo? preferredEndpoint)
    {
        _endpoints.Clear();
        var now = DateTimeOffset.Now;
        foreach (var endpoint in endpoints)
        {
            var item = new PeerEndpointInfo
            {
                Address = endpoint.NetworkAddress,
                LocalAddress = endpoint.NetworkAddress,
                LocalInterfaceId = endpoint.InterfaceId,
                AddressFamily = endpoint.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4",
                LastSeen = now
            };
            _endpoints[GetEndpointKey(item.Address, item.LocalInterfaceId)] = item;
        }

        SelectPrimaryEndpoint();
        if (preferredEndpoint is not null &&
            _endpoints.Values.FirstOrDefault(endpoint =>
                string.Equals(endpoint.Address, preferredEndpoint.NetworkAddress, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(endpoint.LocalInterfaceId, preferredEndpoint.InterfaceId, StringComparison.OrdinalIgnoreCase)) is { } preferred)
        {
            _primaryEndpointKey = GetEndpointKey(preferred.Address, preferred.LocalInterfaceId);
            NetworkAddress = preferred.Address;
        }
        NotifyAddressDisplayChanged();
    }

    public IReadOnlyList<string> GetCandidateAddresses(bool requireHost = false)
        => GetCandidateEndpoints(requireHost)
            .Select(endpoint => endpoint.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private PeerEndpointInfo[] GetCandidateEndpoints(bool requireHost = false)
    {
        return _endpoints.Values
            .Where(endpoint => !requireHost || endpoint.IsHost)
            .OrderByDescending(endpoint => endpoint.Address.Equals(NetworkAddress, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(endpoint => endpoint.LastSeen)
            .GroupBy(
                endpoint => GetEndpointKey(endpoint.Address, endpoint.LocalInterfaceId),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private void SelectPrimaryEndpoint()
    {
        var primary = _endpoints.Values
            .OrderByDescending(endpoint => endpoint.IsHost)
            .ThenByDescending(endpoint => endpoint.LastSeen)
            .FirstOrDefault();
        if (primary is null)
        {
            _primaryEndpointKey = "";
            NetworkAddress = "";
            IsHost = false;
            ServerPort = 0;
            return;
        }

        _primaryEndpointKey = GetEndpointKey(primary.Address, primary.LocalInterfaceId);
        NetworkAddress = primary.Address;
        IsHost = _endpoints.Values.Any(endpoint => endpoint.IsHost);
        ServerPort = _endpoints.Values
            .Where(endpoint => endpoint.IsHost && endpoint.ServerPort is > 0 and <= 65535)
            .OrderByDescending(endpoint => endpoint.LastSeen)
            .Select(endpoint => endpoint.ServerPort)
            .FirstOrDefault();
    }

    private static bool IsSameDisplayGroup(PeerEndpointInfo left, PeerEndpointInfo right)
    {
        if (!string.IsNullOrWhiteSpace(left.LocalInterfaceId) ||
            !string.IsNullOrWhiteSpace(right.LocalInterfaceId))
        {
            return string.Equals(
                left.LocalInterfaceId,
                right.LocalInterfaceId,
                StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static bool IsAddressFamily(PeerEndpointInfo endpoint, AddressFamily family)
    {
        if (IPAddress.TryParse(endpoint.Address, out var parsed)) return parsed.AddressFamily == family;
        return family == AddressFamily.InterNetworkV6
            ? string.Equals(endpoint.AddressFamily, "IPv6", StringComparison.OrdinalIgnoreCase)
            : string.Equals(endpoint.AddressFamily, "IPv4", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetEndpointKey(string address, string? localInterfaceId) =>
        $"{localInterfaceId?.Trim()}|{address.Trim()}";

    private void NotifyAddressDisplayChanged()
    {
        OnPropertyChanged(nameof(AddressDisplay));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(HostDisplayName));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class WorldViewModel
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string BuildName { get; init; }
    public string DisplayName => $"{Name} ({BuildName})";
}

public sealed class ClientBuildViewModel
{
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public required string FullPath { get; init; }
    public bool IsInstalled { get; init; } = true;
}

public sealed class WorldTransferHeader
{
    public string Protocol { get; set; } = "";
    public int ProtocolVersion { get; set; }
    public string MessageType { get; set; } = "";
    public string TransferId { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string SenderIdentityId { get; set; } = "";
    public string SenderIdentityName { get; set; } = "";
    public string OwnerIdentityId { get; set; } = "";
    public string OwnerIdentityName { get; set; } = "";
    public long Size { get; set; }
    public string WorldSha256 { get; set; } = "";
    public string PlayerManifestSha256 { get; set; } = "";
    public string WaypointManifestSha256 { get; set; } = "";
    public string FileName { get; set; } = "world.zip";
    public string WorldName { get; set; } = "World";
}

public sealed class WorldTransferProgressFrame
{
    public string Protocol { get; set; } = "";
    public int ProtocolVersion { get; set; }
    public string MessageType { get; set; } = "";
    public string TransferId { get; set; } = "";
    public string Stage { get; set; } = "";
    public long Current { get; set; }
    public long Total { get; set; }
}

public sealed class WorldTransferAck
{
    public string Protocol { get; set; } = "";
    public int ProtocolVersion { get; set; }
    public bool Ok { get; set; }
    public string Stage { get; set; } = "";
    public string TransferId { get; set; } = "";
    public string Message { get; set; } = "";
    public string WorldSha256 { get; set; } = "";
    public string PlayerManifestSha256 { get; set; } = "";
    public string WaypointManifestSha256 { get; set; } = "";
}

public sealed class WorldTransferControl
{
    public string Protocol { get; set; } = "";
    public int ProtocolVersion { get; set; }
    public string TransferId { get; set; } = "";
    public string MessageType { get; set; } = "";
    // No default command: a frame that omits the field must never be mistaken
    // for a commit.
    public string Command { get; set; } = "";
}

public sealed class WorldTransferJournal
{
    public int SchemaVersion { get; set; } = 1;
    public string TransferId { get; set; } = "";
    public string Role { get; set; } = "";
    public string State { get; set; } = "";
    public string SourceWorldPath { get; set; } = "";
    public string EscrowPath { get; set; } = "";
    public string InstalledWorldPath { get; set; } = "";
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WorldMetadata
{
    public int SchemaVersion { get; set; } = 5;
    public string WorldId { get; set; } = "";
    public string BuildName { get; set; } = "";
    public string BuildRelativePath { get; set; } = "";
    public string PackHash { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string MarkedBy { get; set; } = "LANMinecraft.exe";
    public string OwnerIdentityId { get; set; } = "";
    public string OwnerIdentityName { get; set; } = "";
    public string CurrentHolderIdentityId { get; set; } = "";
    public string CurrentHolderIdentityName { get; set; } = "";
    public DateTimeOffset? LastSuccessfulTransferUtc { get; set; }
}

public sealed class WorldMetadataContext
{
    public required string BuildName { get; init; }
    public required string BuildRelativePath { get; init; }
    public required string PackHash { get; init; }
    public required string OwnerIdentityId { get; init; }
    public required string OwnerIdentityName { get; init; }
}

