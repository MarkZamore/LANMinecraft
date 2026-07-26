using System.Net;
using System.Net.Sockets;
using Minecraft;
using static Minecraft.Tests.NetworkTestData;

namespace Minecraft.Tests;

public sealed class VirtualNetworkServiceTests
{
    [Fact]
    public void AddressSelection_ExcludesFilterEndpoints_AndKeepsOneSelectedPath()
    {
        var softwareA = Endpoint("software-a", "10.10.0.1", 10);
        var softwareB = Endpoint("software-b", "10.20.0.1", 20);
        var filter = Endpoint("filter", "10.30.0.1", 30, isFilter: true);
        var endpoint = Endpoint("endpoint", "10.40.0.1", 40, isEndpoint: true);
        var physical = Endpoint(
            "ethernet",
            "192.168.1.5",
            50,
            isHardware: true,
            hasDefaultRoute: true);
        var environment = new FakeNetworkEnvironment(
            [physical, filter, softwareB, endpoint, softwareA]);
        var service = new VirtualNetworkService(environment);

        var options = service.CaptureAvailableAddresses();

        Assert.Equal(
            ["10.10.0.1", "10.20.0.1", "192.168.1.5"],
            options.Select(option => option.Address));
        Assert.All(options, option => Assert.Equal(option.Address, option.DisplayName));
        Assert.True(service.SelectAddress(softwareA.InterfaceId, softwareA.NetworkAddress));

        var snapshot = service.GetSnapshot();

        Assert.Same(softwareA, snapshot.PrimaryEndpoint);
        Assert.Collection(snapshot.Endpoints, selected => Assert.Same(softwareA, selected));

        environment.Endpoints =
        [
            Endpoint("software-new", "10.50.0.1", 60),
            softwareB,
            softwareA,
            physical
        ];
        _ = service.CaptureAvailableAddresses();

        Assert.Equal("10.10.0.1", service.SelectedAddress?.Address);
        Assert.Same(softwareA, service.GetSnapshot().PrimaryEndpoint);

        Assert.True(service.SelectAddress(softwareB.InterfaceId, softwareB.NetworkAddress));
        Assert.Same(softwareB, service.GetSnapshot().PrimaryEndpoint);
    }

    [Fact]
    public void SelectedRoute_RequiresMatchingAddressInterfaceAndFamily()
    {
        var selected = Endpoint("software-a", "10.10.0.1", 10);
        var environment = new FakeNetworkEnvironment([selected]);
        var service = new VirtualNetworkService(environment);
        _ = service.CaptureAvailableAddresses();
        Assert.True(service.SelectAddress(selected.InterfaceId, selected.NetworkAddress));

        Assert.Same(
            selected,
            service.SelectLocalEndpoint(
                IPAddress.Parse("10.10.0.25"),
                selected.NetworkAddress,
                selected.InterfaceId));
        Assert.Null(service.SelectLocalEndpoint(
            IPAddress.Parse("10.10.0.25"),
            selected.NetworkAddress,
            "other-interface"));
        Assert.Null(service.SelectLocalEndpoint(
            IPAddress.Parse("10.10.0.25"),
            "10.10.0.2",
            selected.InterfaceId));
        Assert.Null(service.SelectLocalEndpoint(IPAddress.IPv6Loopback));
        Assert.Null(service.SelectLocalEndpoint(IPAddress.Parse("fd00::25")));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("169.254.12.34")]
    [InlineData("224.0.2.60")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fe80::1234")]
    public void LoopbackAndNonRoutableAddresses_AreRejected(string value)
    {
        Assert.False(VirtualNetworkService.IsUsableAddress(IPAddress.Parse(value)));
    }

    [Fact]
    public async Task DynamicTargets_AreRequestedOnlyForSelectedEndpoint()
    {
        var selected = Endpoint("software-a", "10.10.0.1", 10);
        var environment = new FakeNetworkEnvironment([selected])
        {
            DynamicTargets = [IPAddress.Parse("10.10.0.25")]
        };
        var service = new VirtualNetworkService(environment);

        Assert.Empty(await service.GetDynamicPeerTargetsAsync(
            new NetworkEnvironmentSnapshot(),
            CancellationToken.None));

        _ = service.CaptureAvailableAddresses();
        _ = service.SelectAddress(selected.InterfaceId, selected.NetworkAddress);
        var targets = await service.GetDynamicPeerTargetsAsync(
            service.GetSnapshot(),
            CancellationToken.None);

        Assert.Equal(IPAddress.Parse("10.10.0.25"), Assert.Single(targets));
    }

    [Fact]
    public async Task SelectionChange_CannotBeOverwrittenByAnOlderSnapshotCapture()
    {
        var first = Endpoint("software-a", "10.10.0.1", 10);
        var second = Endpoint("software-b", "10.20.0.1", 20);
        var environment = new BlockingNetworkEnvironment([first, second]);
        var service = new VirtualNetworkService(environment);
        _ = service.CaptureAvailableAddresses();
        Assert.True(service.SelectAddress(
            first.InterfaceId,
            first.NetworkAddress));
        environment.BlockNextCapture = true;

        var staleCapture = Task.Run(service.GetSnapshot);
        Assert.True(environment.CaptureEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(service.SelectAddress(
            second.InterfaceId,
            second.NetworkAddress));
        environment.ReleaseCapture.Set();

        var snapshot = await staleCapture.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Same(second, snapshot.PrimaryEndpoint);
        Assert.Same(second, service.GetSnapshot().PrimaryEndpoint);
    }

    [Fact]
    public void WindowsInterfaceFlags_UseNativeMibIfRow2Layout()
    {
        if (!OperatingSystem.IsWindows()) return;

        var layout = WindowsNetworkEnvironment.GetMibIfRow2Layout();

        Assert.Equal(1352, layout.Size);
        Assert.Equal(8, layout.InterfaceIndexOffset);
        Assert.Equal(1152, layout.FlagsOffset);
    }

    [Fact]
    public void WindowsEnvironment_EnumeratesOnlyUsableNonFilterAddresses()
    {
        if (!OperatingSystem.IsWindows()) return;
        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"minecraft-interface-test-{Guid.NewGuid():N}.log");
        try
        {
            var environment = new WindowsNetworkEnvironment(
                new Logger(logPath));

            var endpoints = environment.CaptureEndpoints();

            Assert.All(endpoints, endpoint =>
            {
                Assert.True(endpoint.InterfaceIndex > 0);
                Assert.False(endpoint.IsFilterInterface);
                Assert.False(endpoint.IsEndpointInterface);
                Assert.True(VirtualNetworkService.IsUsableAddress(
                    IPAddress.Parse(endpoint.NetworkAddress)));
            });
        }
        finally
        {
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    private sealed class BlockingNetworkEnvironment(
        IReadOnlyList<NetworkEndpointInfo> endpoints) : INetworkEnvironment
    {
        public ManualResetEventSlim CaptureEntered { get; } = new(false);
        public ManualResetEventSlim ReleaseCapture { get; } = new(false);
        public bool BlockNextCapture { get; set; }

        public IReadOnlyList<NetworkEndpointInfo> CaptureEndpoints()
        {
            if (BlockNextCapture)
            {
                BlockNextCapture = false;
                CaptureEntered.Set();
                if (!ReleaseCapture.Wait(TimeSpan.FromSeconds(2)))
                {
                    throw new TimeoutException("Snapshot capture was not released.");
                }
            }
            return endpoints;
        }

        public Task<IReadOnlyList<IPAddress>> GetDynamicPeerTargetsAsync(
            NetworkEndpointInfo selectedEndpoint,
            CancellationToken token) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([]);
    }
}
