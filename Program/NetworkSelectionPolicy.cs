using System.Net;
using System.Net.Sockets;

namespace Minecraft;

public static class NetworkSelectionPolicy
{
    public static NetworkAddressOption? Find(
        IEnumerable<NetworkAddressOption> addresses,
        string? interfaceId,
        string? address)
    {
        if (string.IsNullOrWhiteSpace(interfaceId) || string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        return addresses.FirstOrDefault(candidate =>
            string.Equals(candidate.InterfaceId, interfaceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.Address, address, StringComparison.OrdinalIgnoreCase));
    }

    public static NetworkAddressOption? SelectStartupFallback(
        IReadOnlyList<NetworkAddressOption> addresses,
        string? savedInterfaceId,
        string? savedAddress)
    {
        var restored = Find(addresses, savedInterfaceId, savedAddress);
        if (restored is not null) return restored;

        var softwareAddresses = addresses
            .Where(option => !option.IsHardware)
            .ToArray();
        if (softwareAddresses.Length == 1) return softwareAddresses[0];
        if (softwareAddresses.Length > 1) return null;
        return addresses.FirstOrDefault(option =>
            option.IsHardware &&
            option.HasDefaultRoute);
    }

    public static NetworkAddressOption? SelectNewestSoftwareAddress(
        IReadOnlyList<NetworkAddressOption> addresses,
        IReadOnlySet<string> knownAddressKeys)
    {
        var newlyAppeared = addresses
            .Where(option =>
                !option.IsHardware &&
                !knownAddressKeys.Contains(GetKey(option)))
            .ToArray();
        var newestInterfaceId = newlyAppeared.LastOrDefault()?.InterfaceId;
        return newlyAppeared
            .Where(option => string.Equals(
                option.InterfaceId,
                newestInterfaceId,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(option =>
                IPAddress.TryParse(option.Address, out var parsed) &&
                parsed.AddressFamily == AddressFamily.InterNetwork
                    ? 0
                    : 1)
            .FirstOrDefault();
    }

    public static string GetKey(NetworkAddressOption address) =>
        $"{address.InterfaceId}|{address.Address}";
}
