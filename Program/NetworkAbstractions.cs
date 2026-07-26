using System.Net;
using System.Net.Sockets;

namespace Minecraft;

public interface INetworkClock
{
    DateTimeOffset UtcNow { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken token);
}

public sealed class SystemNetworkClock : INetworkClock
{
    public static SystemNetworkClock Instance { get; } = new();

    private SystemNetworkClock()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken token) =>
        Task.Delay(delay, token);
}

public interface ISelectedNetworkTransport
{
    NetworkEnvironmentSnapshot GetSnapshot();

    TcpClient CreateBoundTcpClient(
        IPAddress remoteAddress,
        string? localAddress = null,
        string? localInterfaceId = null);

    UdpClient CreateBoundUdpClient(
        NetworkEndpointInfo endpoint,
        int port,
        bool reuseAddress);

    Task<IReadOnlyList<IPAddress>> GetDynamicPeerTargetsAsync(
        NetworkEnvironmentSnapshot snapshot,
        CancellationToken token);
}

internal interface ILanRelayPeerConnector
{
    Task<TcpClient> ConnectAsync(
        LanRelayTarget target,
        int remotePort,
        CancellationToken token);
}

internal sealed class SelectedNetworkLanRelayPeerConnector(
    ISelectedNetworkTransport network) : ILanRelayPeerConnector
{
    public async Task<TcpClient> ConnectAsync(
        LanRelayTarget target,
        int remotePort,
        CancellationToken token)
    {
        var client = network.CreateBoundTcpClient(
            target.Address,
            target.LocalAddress,
            target.LocalInterfaceId);
        try
        {
            await client.ConnectAsync(
                target.Address,
                remotePort,
                token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
