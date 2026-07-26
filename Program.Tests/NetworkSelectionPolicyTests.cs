using Minecraft;

namespace Minecraft.Tests;

public sealed class NetworkSelectionPolicyTests
{
    [Fact]
    public void SavedAddress_IsRestoredWhenSeveralSoftwareAddressesExist()
    {
        var first = Option("interface-a", "10.1.0.1");
        var saved = Option("interface-b", "10.2.0.1");

        var selected = NetworkSelectionPolicy.SelectStartupFallback(
            [first, saved],
            saved.InterfaceId,
            saved.Address);

        Assert.Same(saved, selected);
    }

    [Fact]
    public void MissingSavedAddress_RequiresManualChoiceWithSeveralSoftwareAddresses()
    {
        var selected = NetworkSelectionPolicy.SelectStartupFallback(
            [
                Option("interface-a", "10.1.0.1"),
                Option("interface-b", "10.2.0.1")
            ],
            "missing-interface",
            "10.99.0.1");

        Assert.Null(selected);
    }

    [Fact]
    public void SingleSoftwareAddress_WinsOverPhysicalDefaultRoute()
    {
        var software = Option("software", "10.3.0.1");
        var physical = Option(
            "physical",
            "192.168.0.2",
            isHardware: true,
            hasDefaultRoute: true);

        var selected = NetworkSelectionPolicy.SelectStartupFallback(
            [physical, software],
            "",
            "");

        Assert.Same(software, selected);
    }

    [Fact]
    public void PhysicalMainRoute_IsFallbackWhenNoSoftwareAddressExists()
    {
        var secondary = Option("ethernet", "192.168.0.2", isHardware: true);
        var primary = Option(
            "wifi",
            "192.168.1.2",
            isHardware: true,
            hasDefaultRoute: true);

        var selected = NetworkSelectionPolicy.SelectStartupFallback(
            [secondary, primary],
            "",
            "");

        Assert.Same(primary, selected);
    }

    [Fact]
    public void NewlyAppearedInterface_PrefersItsIpv4Address()
    {
        var old = Option("old-interface", "10.1.0.1");
        var newIpv4 = Option("new-interface", "10.2.0.1");
        var newIpv6 = Option("new-interface", "fd00::1");
        var known = new HashSet<string>(
            [NetworkSelectionPolicy.GetKey(old)],
            StringComparer.OrdinalIgnoreCase);

        var selected = NetworkSelectionPolicy.SelectNewestSoftwareAddress(
            [old, newIpv6, newIpv4],
            known);

        Assert.Same(newIpv4, selected);
    }

    private static NetworkAddressOption Option(
        string interfaceId,
        string address,
        bool isHardware = false,
        bool hasDefaultRoute = false) =>
        new()
        {
            InterfaceId = interfaceId,
            InterfaceIndex = 1,
            Address = address,
            DisplayName = address,
            IsHardware = isHardware,
            HasDefaultRoute = hasDefaultRoute
        };
}
