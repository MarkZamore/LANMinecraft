using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Minecraft;

public sealed class NetworkAddressOption
{
    public required string InterfaceId { get; init; }
    public int InterfaceIndex { get; init; }
    public required string Address { get; init; }
    public required string DisplayName { get; init; }
    public bool IsHardware { get; init; }
    public bool HasDefaultRoute { get; init; }
}

public interface INetworkEnvironment
{
    IReadOnlyList<NetworkEndpointInfo> CaptureEndpoints();

    Task<IReadOnlyList<IPAddress>> GetDynamicPeerTargetsAsync(
        NetworkEndpointInfo selectedEndpoint,
        CancellationToken token);
}

public sealed class VirtualNetworkService : ISelectedNetworkTransport
{
    private const SocketOptionName UnicastInterfaceOption = (SocketOptionName)31;
    private readonly INetworkEnvironment _environment;
    private readonly object _selectionGate = new();
    private readonly object _snapshotGate = new();
    private NetworkAddressOption? _selectedAddress;
    private NetworkAddressOption[] _availableAddresses = [];
    private NetworkEnvironmentSnapshot? _cachedSnapshot;
    private long _snapshotGeneration;

    public VirtualNetworkService(Logger logger)
        : this(new WindowsNetworkEnvironment(logger))
    {
    }

    public VirtualNetworkService(INetworkEnvironment environment)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public NetworkAddressOption? SelectedAddress
    {
        get
        {
            lock (_selectionGate) return _selectedAddress;
        }
    }

    public IReadOnlyList<NetworkAddressOption> CaptureAvailableAddresses()
    {
        var options = _environment.CaptureEndpoints()
            .Where(endpoint => !endpoint.IsFilterInterface && !endpoint.IsEndpointInterface)
            .GroupBy(
                endpoint => BuildAddressKey(endpoint.InterfaceId, endpoint.NetworkAddress),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(endpoint => endpoint.IsHardware ? 1 : 0)
            .ThenByDescending(endpoint => endpoint.HasDefaultRoute)
            .ThenBy(endpoint => endpoint.InterfaceName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(endpoint => endpoint.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ThenBy(endpoint => endpoint.NetworkAddress, StringComparer.OrdinalIgnoreCase)
            .Select(endpoint => new NetworkAddressOption
            {
                InterfaceId = endpoint.InterfaceId,
                InterfaceIndex = endpoint.InterfaceIndex,
                Address = endpoint.NetworkAddress,
                DisplayName = endpoint.NetworkAddress,
                IsHardware = endpoint.IsHardware,
                HasDefaultRoute = endpoint.HasDefaultRoute
            })
            .ToArray();

        lock (_selectionGate) _availableAddresses = options;
        return options;
    }

    public bool SelectAddress(string? interfaceId, string? address)
    {
        interfaceId = interfaceId?.Trim() ?? "";
        address = NormalizeAddress(address);
        if (string.IsNullOrWhiteSpace(interfaceId) || string.IsNullOrWhiteSpace(address)) return false;

        NetworkAddressOption? selected;
        lock (_selectionGate)
        {
            selected = _availableAddresses.FirstOrDefault(candidate =>
                string.Equals(candidate.InterfaceId, interfaceId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Address, address, StringComparison.OrdinalIgnoreCase));
        }

        if (selected is null)
        {
            CaptureAvailableAddresses();
            lock (_selectionGate)
            {
                selected = _availableAddresses.FirstOrDefault(candidate =>
                    string.Equals(candidate.InterfaceId, interfaceId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.Address, address, StringComparison.OrdinalIgnoreCase));
            }
        }
        if (selected is null) return false;

        lock (_selectionGate)
        {
            if (AreSameAddress(_selectedAddress, selected)) return false;
            _selectedAddress = selected;
        }
        InvalidateSnapshot();
        return true;
    }

    public NetworkEnvironmentSnapshot GetSnapshot()
    {
        while (true)
        {
            long generation;
            lock (_snapshotGate)
            {
                if (_cachedSnapshot is not null) return _cachedSnapshot;
                generation = _snapshotGeneration;
            }

            NetworkAddressOption? selected;
            lock (_selectionGate) selected = _selectedAddress;
            var endpoint = selected is null
                ? null
                : _environment.CaptureEndpoints().FirstOrDefault(candidate =>
                    !candidate.IsFilterInterface &&
                    !candidate.IsEndpointInterface &&
                    string.Equals(candidate.InterfaceId, selected.InterfaceId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.NetworkAddress, selected.Address, StringComparison.OrdinalIgnoreCase));
            var snapshot = new NetworkEnvironmentSnapshot
            {
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Endpoints = endpoint is null ? [] : [endpoint],
                PrimaryEndpoint = endpoint
            };
            lock (_snapshotGate)
            {
                if (_cachedSnapshot is not null) return _cachedSnapshot;
                if (generation != _snapshotGeneration) continue;
                _cachedSnapshot = snapshot;
                return snapshot;
            }
        }
    }

    public void InvalidateSnapshot()
    {
        lock (_snapshotGate)
        {
            _cachedSnapshot = null;
            _snapshotGeneration++;
        }
    }

    public NetworkEndpointInfo? SelectLocalEndpoint(
        IPAddress remoteAddress,
        string? localAddress = null,
        string? localInterfaceId = null)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);
        if (!IsUsableAddress(remoteAddress)) return null;

        var endpoint = GetSnapshot().PrimaryEndpoint;
        if (endpoint is null || endpoint.AddressFamily != remoteAddress.AddressFamily) return null;
        if (!string.IsNullOrWhiteSpace(localInterfaceId) &&
            !string.Equals(endpoint.InterfaceId, localInterfaceId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(localAddress) &&
            !string.Equals(endpoint.NetworkAddress, NormalizeAddress(localAddress), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return endpoint;
    }

    public TcpClient CreateBoundTcpClient(
        IPAddress remoteAddress,
        string? localAddress = null,
        string? localInterfaceId = null)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);
        if (!IsUsableAddress(remoteAddress))
        {
            throw new SocketException((int)SocketError.NetworkUnreachable);
        }

        var localEndpoint = SelectLocalEndpoint(remoteAddress, localAddress, localInterfaceId) ??
            throw new SocketException((int)SocketError.NetworkUnreachable);
        var client = new TcpClient(remoteAddress.AddressFamily);
        try
        {
            ConfigureInterface(client.Client, localEndpoint);
            client.Client.Bind(new IPEndPoint(IPAddress.Parse(localEndpoint.NetworkAddress), 0));
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public UdpClient CreateBoundUdpClient(
        NetworkEndpointInfo endpoint,
        int port,
        bool reuseAddress)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var selected = GetSnapshot().PrimaryEndpoint;
        if (selected is null ||
            !string.Equals(selected.InterfaceId, endpoint.InterfaceId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(selected.NetworkAddress, endpoint.NetworkAddress, StringComparison.OrdinalIgnoreCase))
        {
            throw new SocketException((int)SocketError.NetworkUnreachable);
        }

        var localAddress = IPAddress.Parse(endpoint.NetworkAddress);
        var udp = new UdpClient(localAddress.AddressFamily);
        try
        {
            if (localAddress.AddressFamily == AddressFamily.InterNetworkV6)
            {
                udp.Client.DualMode = false;
            }
            if (reuseAddress)
            {
                udp.Client.ExclusiveAddressUse = false;
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            }
            ConfigureInterface(udp.Client, endpoint);
            udp.Client.Bind(new IPEndPoint(localAddress, port));
            return udp;
        }
        catch
        {
            udp.Dispose();
            throw;
        }
    }

    public static void ConfigureInterface(Socket socket, NetworkEndpointInfo endpoint)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.InterfaceIndex <= 0) return;

        if (endpoint.AddressFamily == AddressFamily.InterNetwork)
        {
            socket.SetSocketOption(
                SocketOptionLevel.IP,
                UnicastInterfaceOption,
                IPAddress.HostToNetworkOrder(endpoint.InterfaceIndex));
        }
        else if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
        {
            socket.SetSocketOption(
                SocketOptionLevel.IPv6,
                UnicastInterfaceOption,
                endpoint.InterfaceIndex);
        }
    }

    public Task<IReadOnlyList<IPAddress>> GetDynamicPeerTargetsAsync(
        NetworkEnvironmentSnapshot snapshot,
        CancellationToken token)
    {
        var selected = snapshot.PrimaryEndpoint;
        return selected is null
            ? Task.FromResult<IReadOnlyList<IPAddress>>([])
            : _environment.GetDynamicPeerTargetsAsync(selected, token);
    }

    public static IReadOnlyList<IPAddress> EnumerateProbeAddresses(
        NetworkEndpointInfo endpoint,
        int maxSubnetSize)
    {
        if (endpoint.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.TryParse(endpoint.NetworkAddress, out var adapterIp))
        {
            return Array.Empty<IPAddress>();
        }

        var mask = PrefixToMask(endpoint.PrefixLength);
        var adapterValue = ToUInt32(adapterIp);
        var maskValue = ToUInt32(mask);
        var network = adapterValue & maskValue;
        var broadcast = network | ~maskValue;
        var count = broadcast >= network ? broadcast - network + 1 : 0;
        if (count <= 2 || count > (uint)maxSubnetSize) return Array.Empty<IPAddress>();

        var result = new List<IPAddress>();
        for (var value = network + 1; value < broadcast && result.Count < maxSubnetSize; value++)
        {
            if (value != adapterValue) result.Add(FromUInt32(value));
            if (value == uint.MaxValue) break;
        }
        return result;
    }

    public static bool SupportsDirectedBroadcast(NetworkEndpointInfo endpoint)
    {
        if (endpoint.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.TryParse(endpoint.NetworkAddress, out var adapterIp) ||
            !IPAddress.TryParse(endpoint.BroadcastAddress, out var broadcast))
        {
            return false;
        }
        var addressCount = endpoint.PrefixLength >= 32 ? 1UL : 1UL << (32 - endpoint.PrefixLength);
        return addressCount > 2 && !broadcast.Equals(adapterIp);
    }

    public static bool IsUsableAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6 ||
            IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.IsIPv6Multicast ||
            address.IsIPv6LinkLocal)
        {
            return false;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] is > 0 and < 224 && !(bytes[0] == 169 && bytes[1] == 254);
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6;
    }

    private static bool AreSameAddress(NetworkAddressOption? left, NetworkAddressOption? right) =>
        left is not null &&
        right is not null &&
        string.Equals(left.InterfaceId, right.InterfaceId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Address, right.Address, StringComparison.OrdinalIgnoreCase);

    private static string BuildAddressKey(string interfaceId, string address) =>
        $"{interfaceId.Trim()}|{address.Trim()}";

    private static string NormalizeAddress(string? value) =>
        IPAddress.TryParse(value?.Trim(), out var address) && IsUsableAddress(address)
            ? address.ToString()
            : "";

    private static IPAddress PrefixToMask(int prefixLength)
    {
        var bits = Math.Clamp(prefixLength, 0, 32);
        var value = bits == 0 ? 0U : uint.MaxValue << (32 - bits);
        return FromUInt32(value);
    }

    private static uint ToUInt32(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress FromUInt32(uint value) => new(new[]
    {
        (byte)(value >> 24),
        (byte)(value >> 16),
        (byte)(value >> 8),
        (byte)value
    });
}

internal sealed class WindowsNetworkEnvironment : INetworkEnvironment
{
    private const int InterfaceFlagHardware = 1 << 0;
    private const int InterfaceFlagFilter = 1 << 1;
    private const int InterfaceFlagEndpoint = 1 << 7;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
    private readonly Logger _logger;
    private DateTimeOffset _lastCommandWarningUtc = DateTimeOffset.MinValue;

    public WindowsNetworkEnvironment(Logger logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<NetworkEndpointInfo> CaptureEndpoints()
    {
        var endpoints = new List<NetworkEndpointInfo>();
        var bestIpv4Interface = GetBestInterfaceIndex(IPAddress.Parse("1.1.1.1"));
        var bestIpv6Interface = GetBestInterfaceIndex(IPAddress.Parse("2606:4700:4700::1111"));
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (!VirtualNetworkService.IsUsableAddress(address)) continue;

                var interfaceIndex = GetInterfaceIndex(properties, address.AddressFamily);
                if (interfaceIndex <= 0) continue;
                var flags = GetInterfaceFlags(interfaceIndex, nic.NetworkInterfaceType);
                if ((flags & (InterfaceFlagFilter | InterfaceFlagEndpoint)) != 0) continue;

                var prefixLength = Math.Clamp(
                    unicast.PrefixLength,
                    0,
                    address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
                var bestInterface = address.AddressFamily == AddressFamily.InterNetwork
                    ? bestIpv4Interface
                    : bestIpv6Interface;
                var hasDefaultRoute = bestInterface.HasValue
                    ? bestInterface.Value == interfaceIndex
                    : HasConfiguredGateway(properties, address.AddressFamily);
                endpoints.Add(new NetworkEndpointInfo
                {
                    InterfaceId = nic.Id,
                    InterfaceIndex = interfaceIndex,
                    InterfaceName = nic.Name,
                    Description = nic.Description,
                    NetworkAddress = address.ToString(),
                    PrefixLength = prefixLength,
                    BroadcastAddress = address.AddressFamily == AddressFamily.InterNetwork
                        ? CalculateBroadcast(address, PrefixToMask(prefixLength)).ToString()
                        : "",
                    IsHardware = (flags & InterfaceFlagHardware) != 0,
                    IsFilterInterface = (flags & InterfaceFlagFilter) != 0,
                    IsEndpointInterface = (flags & InterfaceFlagEndpoint) != 0,
                    HasDefaultRoute = hasDefaultRoute,
                    SortPriority = (flags & InterfaceFlagHardware) == 0
                        ? 0
                        : hasDefaultRoute
                            ? 20
                            : 30
                });
            }
        }
        return endpoints;
    }

    public async Task<IReadOnlyList<IPAddress>> GetDynamicPeerTargetsAsync(
        NetworkEndpointInfo selectedEndpoint,
        CancellationToken token)
    {
        var localAddress = IPAddress.Parse(selectedEndpoint.NetworkAddress);
        var result = new HashSet<IPAddress>();
        var routeOutput = await TryRunCommandAsync(
            Path.Combine(Environment.SystemDirectory, "route.exe"),
            "PRINT",
            token).ConfigureAwait(false);
        foreach (var address in ExtractHostRoutes(
                     routeOutput,
                     selectedEndpoint.InterfaceIndex,
                     selectedEndpoint.NetworkAddress))
        {
            if (IsTargetForSelectedAddress(address, localAddress)) result.Add(address);
        }

        return result
            .OrderBy(address => address.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<string> TryRunCommandAsync(
        string executable,
        string arguments,
        CancellationToken token)
    {
        if (!File.Exists(executable)) return "";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(CommandTimeout);
        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return "";
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            _ = await errorTask.ConfigureAwait(false);
            return output;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
            }
            token.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow - _lastCommandWarningUtc > TimeSpan.FromMinutes(1))
            {
                _lastCommandWarningUtc = DateTimeOffset.UtcNow;
                _logger.Warn($"Network route enumeration failed: {ex.Message}");
            }
            return "";
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static IPAddress[] ExtractHostRoutes(
        string text,
        int selectedInterfaceIndex,
        string selectedLocalAddress)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var result = new HashSet<IPAddress>();
        foreach (var line in text.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 4 &&
                columns[1] == "255.255.255.255" &&
                string.Equals(columns[3], selectedLocalAddress, StringComparison.OrdinalIgnoreCase))
            {
                var address = ParseAddress(columns[0]);
                if (address is not null) result.Add(address);
            }

            if (columns.Length >= 3 &&
                int.TryParse(columns[0], out var interfaceIndex) &&
                interfaceIndex == selectedInterfaceIndex)
            {
                foreach (var column in columns.Where(value =>
                             value.EndsWith("/128", StringComparison.Ordinal)))
                {
                    var address = ParseAddress(column);
                    if (address is not null) result.Add(address);
                }
            }
        }
        return result.ToArray();
    }

    private static bool IsTargetForSelectedAddress(IPAddress target, IPAddress localAddress) =>
        target.AddressFamily == localAddress.AddressFamily &&
        !target.Equals(localAddress) &&
        VirtualNetworkService.IsUsableAddress(target);

    private static IPAddress? ParseAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim().Trim('[', ']', '"', '\'', ',', ';');
        var slash = candidate.LastIndexOf('/');
        if (slash > 0) candidate = candidate[..slash];
        if (!IPAddress.TryParse(candidate, out var address)) return null;
        return VirtualNetworkService.IsUsableAddress(address) ? address : null;
    }

    private static int GetInterfaceIndex(
        IPInterfaceProperties properties,
        AddressFamily family)
    {
        try
        {
            return family == AddressFamily.InterNetwork
                ? properties.GetIPv4Properties()?.Index ?? 0
                : properties.GetIPv6Properties()?.Index ?? 0;
        }
        catch (NetworkInformationException)
        {
            return 0;
        }
    }

    private static bool HasConfiguredGateway(
        IPInterfaceProperties properties,
        AddressFamily family) =>
        properties.GatewayAddresses.Any(gateway =>
            gateway.Address.AddressFamily == family &&
            !gateway.Address.Equals(IPAddress.Any) &&
            !gateway.Address.Equals(IPAddress.IPv6Any));

    private static int? GetBestInterfaceIndex(IPAddress destination)
    {
        var sockaddr = new byte[28];
        BitConverter.GetBytes((ushort)destination.AddressFamily).CopyTo(sockaddr, 0);
        var addressBytes = destination.GetAddressBytes();
        if (destination.AddressFamily == AddressFamily.InterNetwork)
        {
            addressBytes.CopyTo(sockaddr, 4);
        }
        else if (destination.AddressFamily == AddressFamily.InterNetworkV6)
        {
            addressBytes.CopyTo(sockaddr, 8);
        }
        else
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(sockaddr.Length);
        try
        {
            Marshal.Copy(sockaddr, 0, buffer, sockaddr.Length);
            return GetBestInterfaceEx(buffer, out var interfaceIndex) == 0 &&
                   interfaceIndex <= int.MaxValue
                ? (int)interfaceIndex
                : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int GetInterfaceFlags(
        int interfaceIndex,
        NetworkInterfaceType fallbackType)
    {
        var layout = GetMibIfRow2Layout();
        var rowSize = layout.Size;
        var buffer = Marshal.AllocHGlobal(rowSize);
        try
        {
            Marshal.Copy(new byte[rowSize], 0, buffer, rowSize);
            Marshal.WriteInt32(buffer, layout.InterfaceIndexOffset, interfaceIndex);
            if (GetIfEntry2(buffer) == 0)
            {
                return Marshal.PtrToStructure<MibIfRow2>(buffer).InterfaceAndOperStatusFlags;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return fallbackType is NetworkInterfaceType.Ethernet or
            NetworkInterfaceType.GigabitEthernet or
            NetworkInterfaceType.Wireless80211
                ? InterfaceFlagHardware
                : 0;
    }

    internal static (int Size, int InterfaceIndexOffset, int FlagsOffset) GetMibIfRow2Layout() =>
    (
        Marshal.SizeOf<MibIfRow2>(),
        Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.InterfaceIndex)).ToInt32(),
        Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.InterfaceAndOperStatusFlags)).ToInt32()
    );

    private static IPAddress PrefixToMask(int prefixLength)
    {
        var bits = Math.Clamp(prefixLength, 0, 32);
        var value = bits == 0 ? 0U : uint.MaxValue << (32 - bits);
        return FromUInt32(value);
    }

    private static IPAddress CalculateBroadcast(IPAddress ip, IPAddress mask)
    {
        var ipBytes = ip.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var result = new byte[4];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = (byte)(ipBytes[index] | ~maskBytes[index]);
        }
        return new IPAddress(result);
    }

    private static IPAddress FromUInt32(uint value) => new(new[]
    {
        (byte)(value >> 24),
        (byte)(value >> 16),
        (byte)(value >> 8),
        (byte)value
    });

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIfEntry2(IntPtr row);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestInterfaceEx(IntPtr destinationAddress, out uint bestInterfaceIndex);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MibIfRow2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
        public string Alias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
        public string Description;
        public uint PhysicalAddressLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] PhysicalAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] PermanentPhysicalAddress;
        public uint Mtu;
        public uint Type;
        public uint TunnelType;
        public uint MediaType;
        public uint PhysicalMediumType;
        public uint AccessType;
        public uint DirectionType;
        public byte InterfaceAndOperStatusFlags;
        public uint OperStatus;
        public uint AdminStatus;
        public uint MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed;
        public ulong ReceiveLinkSpeed;
        public ulong InOctets;
        public ulong InUcastPkts;
        public ulong InNUcastPkts;
        public ulong InDiscards;
        public ulong InErrors;
        public ulong InUnknownProtos;
        public ulong InUcastOctets;
        public ulong InMulticastOctets;
        public ulong InBroadcastOctets;
        public ulong OutOctets;
        public ulong OutUcastPkts;
        public ulong OutNUcastPkts;
        public ulong OutDiscards;
        public ulong OutErrors;
        public ulong OutUcastOctets;
        public ulong OutMulticastOctets;
        public ulong OutBroadcastOctets;
        public ulong OutQLen;
    }
}
