using System.Globalization;

namespace Minecraft;

/// <summary>
/// Decides which packs get the Steam transport mod. Play over Steam needs
/// e4steam inside the game, and e4steam is a NeoForge mod declared for
/// Minecraft [1.20.2, 26.3); every pack in that range is served, which is
/// deliberately wider than <see cref="ManagedTeleportPackPolicy"/> (that one
/// gates Infinity-only gameplay extras).
/// </summary>
public static class SteamPlayPolicy
{
    private static readonly int[] MinimumMinecraftVersion = [1, 20, 2];
    private static readonly int[] ExclusiveMaximumMinecraftVersion = [26, 3];

    public static bool IsSupported(PackRuntimeDescriptor? descriptor) =>
        descriptor is not null &&
        descriptor.Loader.Type == PackLoaderKind.NeoForge &&
        IsSupportedMinecraftVersion(descriptor.MinecraftVersion);

    internal static bool IsSupportedMinecraftVersion(string? minecraftVersion)
    {
        if (!TryParseVersion(minecraftVersion, out var parsed)) return false;
        return Compare(parsed, MinimumMinecraftVersion) >= 0 &&
               Compare(parsed, ExclusiveMaximumMinecraftVersion) < 0;
    }

    private static bool TryParseVersion(string? value, out int[] parts)
    {
        parts = [];
        if (string.IsNullOrWhiteSpace(value)) return false;

        var segments = value.Split('.', StringSplitOptions.TrimEntries);
        var parsed = new int[segments.Length];
        for (var index = 0; index < segments.Length; index++)
        {
            if (!int.TryParse(segments[index], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[index]))
            {
                return false;
            }
        }

        parts = parsed;
        return parts.Length > 0;
    }

    private static int Compare(int[] left, int[] right)
    {
        for (var index = 0; index < Math.Max(left.Length, right.Length); index++)
        {
            var leftPart = index < left.Length ? left[index] : 0;
            var rightPart = index < right.Length ? right[index] : 0;
            if (leftPart != rightPart) return leftPart.CompareTo(rightPart);
        }
        return 0;
    }
}
