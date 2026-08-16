using System.Net;
using System.Net.Sockets;
using Minecraft;

namespace Minecraft.Tests;

internal static class NetworkTestData
{
    public static NetworkEndpointInfo Endpoint(
        string interfaceId,
        string address,
        int interfaceIndex = 1,
        bool isHardware = false,
        bool isFilter = false,
        bool isEndpoint = false,
        bool hasDefaultRoute = false) =>
        new()
        {
            InterfaceId = interfaceId,
            InterfaceIndex = interfaceIndex,
            InterfaceName = interfaceId,
            NetworkAddress = address,
            PrefixLength = IPAddress.Parse(address).AddressFamily ==
                           System.Net.Sockets.AddressFamily.InterNetwork
                ? 24
                : 64,
            BroadcastAddress = IPAddress.TryParse(address, out var parsed) &&
                               parsed.AddressFamily ==
                               System.Net.Sockets.AddressFamily.InterNetwork
                ? $"{parsed.GetAddressBytes()[0]}.{parsed.GetAddressBytes()[1]}.{parsed.GetAddressBytes()[2]}.255"
                : "",
            IsHardware = isHardware,
            IsFilterInterface = isFilter,
            IsEndpointInterface = isEndpoint,
            HasDefaultRoute = hasDefaultRoute
        };

    internal sealed class FakeNetworkEnvironment(
        IEnumerable<NetworkEndpointInfo> endpoints) : INetworkEnvironment
    {
        public IReadOnlyList<NetworkEndpointInfo> Endpoints { get; set; } =
            endpoints.ToArray();

        public IReadOnlyList<IPAddress> DynamicTargets { get; set; } = [];

        public IReadOnlyList<NetworkEndpointInfo> CaptureEndpoints() =>
            Endpoints.ToArray();

        public Task<IReadOnlyList<IPAddress>> GetDynamicPeerTargetsAsync(
            NetworkEndpointInfo selectedEndpoint,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(DynamicTargets);
        }
    }

    internal sealed class FakeNetworkClock(DateTimeOffset utcNow) : INetworkClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            UtcNow += delay;
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeSelectedNetworkTransport : ISelectedNetworkTransport
    {
        public NetworkEnvironmentSnapshot GetSnapshot() => new();

        public TcpClient CreateBoundTcpClient(
            IPAddress remoteAddress,
            string? localAddress = null,
            string? localInterfaceId = null) =>
            throw new InvalidOperationException("No remote connection was expected.");

        public UdpClient CreateBoundUdpClient(
            NetworkEndpointInfo endpoint,
            int port,
            bool reuseAddress) =>
            throw new InvalidOperationException("No datagram socket was expected.");

        public Task<IReadOnlyList<IPAddress>> GetDynamicPeerTargetsAsync(
            NetworkEnvironmentSnapshot snapshot,
            CancellationToken token) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([]);
    }

    internal sealed class RecordingSelectedNetworkTransport : ISelectedNetworkTransport
    {
        public IPAddress? LastRemoteAddress { get; private set; }
        public string? LastLocalAddress { get; private set; }
        public string? LastLocalInterfaceId { get; private set; }

        public NetworkEnvironmentSnapshot GetSnapshot() => new();

        public TcpClient CreateBoundTcpClient(
            IPAddress remoteAddress,
            string? localAddress = null,
            string? localInterfaceId = null)
        {
            LastRemoteAddress = remoteAddress;
            LastLocalAddress = localAddress;
            LastLocalInterfaceId = localInterfaceId;
            return new TcpClient(remoteAddress.AddressFamily);
        }

        public UdpClient CreateBoundUdpClient(
            NetworkEndpointInfo endpoint,
            int port,
            bool reuseAddress) =>
            throw new InvalidOperationException("No datagram socket was expected.");

        public Task<IReadOnlyList<IPAddress>> GetDynamicPeerTargetsAsync(
            NetworkEnvironmentSnapshot snapshot,
            CancellationToken token) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([]);
    }
}
