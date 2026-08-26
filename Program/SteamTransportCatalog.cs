using System.Globalization;

namespace Minecraft;

/// <summary>
/// One published build of the Steam transport mod: which packs it serves, and
/// the exact bytes that are it.
/// </summary>
/// <param name="Version">The mod release this build belongs to.</param>
/// <param name="FileName">Its name upstream, kept as the name in the instance.</param>
/// <param name="SizeBytes">What a complete download weighs.</param>
/// <param name="Sha256">What a correct download hashes to.</param>
/// <param name="CacheFileId">
/// The folder this build is cached under. Synthetic: upstream has no numeric
/// file id, so the release and the loader are encoded into one.
/// </param>
/// <param name="Loaders">The loaders that can load it.</param>
/// <param name="MinimumMinecraftVersion">The first Minecraft it declares.</param>
/// <param name="ExclusiveMaximumMinecraftVersion">The first it no longer declares.</param>
public sealed record SteamTransportBuild(
    string Version,
    string FileName,
    long SizeBytes,
    string Sha256,
    long CacheFileId,
    IReadOnlyList<PackLoaderKind> Loaders,
    IReadOnlyList<int> MinimumMinecraftVersion,
    IReadOnlyList<int> ExclusiveMaximumMinecraftVersion)
{
    /// <summary>Where this exact file can be fetched from.</summary>
    public IReadOnlyList<Uri> DownloadUris { get; } = Array.AsReadOnly<Uri>(
    [
        new Uri(
            $"https://github.com/Kamilhik/e4steam/releases/download/v{Version}/{FileName}",
            UriKind.Absolute)
    ]);
}

/// <summary>
/// The builds of e4steam the launcher will install, and which pack each one is
/// for.
///
/// The mod is not one artifact. Its author publishes a build per loader and per
/// range of Minecraft - nineteen of them in 0.3.0 - and for a long time this
/// launcher pinned exactly one of those and took its <c>mods.toml</c> to be a
/// statement about the mod. It is not: <c>[1.20.2,26.3)</c> is what the
/// NeoForge build declares, and the Forge build beside it declares
/// <c>[1.18.2,1.20.3)</c>. Reading one artifact as the whole shut every Forge
/// pack out of playing together for no reason but that.
///
/// Every range below is read from the build's own metadata rather than from its
/// file name, which understates them: the file called
/// <c>forge-mc1.18.2-1.20.2</c> declares Minecraft up to 1.20.3.
/// </summary>
public static class SteamTransportCatalog
{
    /// <summary>The release every build here comes from.</summary>
    public const string Version = "0.3.0";

    /// <summary>
    /// What is served, and what is deliberately not.
    ///
    /// Three of these joined the day the launcher stopped installing one Java
    /// for every pack. They were absent for exactly that reason and no other:
    /// the 26.x build asks for Java 25 and the 1.17-1.18.2 builds are for a
    /// Minecraft that does not run well on 21, so while there was one runtime
    /// they could not be offered. Now a pack gets the Java its Minecraft was
    /// built against, and they can.
    ///
    /// 0.3.0 also publishes Forge back to 1.7 and Fabric back to 1.14. Those
    /// stay out: a row here is a promise that the launcher can run such a pack,
    /// and Minecraft 1.16 and older want a Java 8 that the loaders of that era
    /// need more care with than one line in a table.
    /// </summary>
    public static IReadOnlyList<SteamTransportBuild> Builds { get; } =
        Array.AsReadOnly<SteamTransportBuild>(
        [
            new(
                Version,
                "e4steam-neoforge-mc1.20.2-26.2-v0.3.0.jar",
                3_634_360,
                "3d2b56b50f6646733a3e41e67aedb3cb7baf96e48707284083c473908bbf4adb",
                30_001,
                [PackLoaderKind.NeoForge],
                [1, 20, 2],
                [26, 3]),
            new(
                Version,
                "e4steam-forge-mc1.18.2-1.20.2-v0.3.0.jar",
                3_633_909,
                "7351b3e21845c6928fa8bf6ed834e2a9cbab660b7513afabb117b848f7670d15",
                30_002,
                [PackLoaderKind.Forge],
                [1, 18, 2],
                [1, 20, 3]),
            new(
                Version,
                "e4steam-forge-mc1.17.1-1.18.1-v0.3.0.jar",
                3_636_879,
                "2c6155665bfd5aacf2663f170d6874db1f3a6ada42b13af819242dd46002d0ed",
                30_004,
                [PackLoaderKind.Forge],
                [1, 17, 1],
                [1, 18, 2]),
            // Quilt loads Fabric mods, and these are the builds its author
            // tested it with, so the two loaders share a row rather than one
            // being refused for want of an artifact of its own.
            new(
                Version,
                "e4steam-fabric-quilt-mc1.17-1.18.2-v0.3.0.jar",
                3_634_508,
                "f1fb415f6e7d019381a14d1c9b39fe1b84cb585d7f3f673f85c5ef23bc939db7",
                30_005,
                [PackLoaderKind.Fabric, PackLoaderKind.Quilt],
                [1, 17],
                [1, 19]),
            // Minecraft 26 wants Java 25 and this build says so itself; the
            // runtime catalogue answers 25 for a 26.x pack, so it can be kept.
            new(
                Version,
                "e4steam-fabric-quilt-mc26.1-26.2-v0.3.0.jar",
                3_632_179,
                "6578e4d71a5e1499d35aeff699ac84ade6047e104a0439efca9d13cdea0ad443",
                30_006,
                [PackLoaderKind.Fabric, PackLoaderKind.Quilt],
                [26, 1],
                [26, 3]),
            new(
                Version,
                "e4steam-fabric-quilt-mc1.19-1.21.11-v0.3.0.jar",
                3_631_710,
                "d22b994d94a48143640879fff7a52071a32a3f3fcea3ce686052a001b26e8a1f",
                30_003,
                [PackLoaderKind.Fabric, PackLoaderKind.Quilt],
                [1, 19],
                [26, 1])
        ]);

    /// <summary>
    /// The build for this pack, or null when no published build serves it.
    /// </summary>
    public static SteamTransportBuild? Find(PackRuntimeDescriptor? descriptor)
    {
        if (descriptor is null) return null;
        if (!TryParseVersion(descriptor.MinecraftVersion, out var version)) return null;

        foreach (var build in Builds)
        {
            if (!build.Loaders.Contains(descriptor.Loader.Type)) continue;
            if (Compare(version, build.MinimumMinecraftVersion) < 0) continue;
            if (Compare(version, build.ExclusiveMaximumMinecraftVersion) >= 0) continue;
            return build;
        }
        return null;
    }

    /// <summary>Whether any build at all declares this Minecraft.</summary>
    public static bool CoversMinecraftVersion(string? minecraftVersion)
    {
        if (!TryParseVersion(minecraftVersion, out var version)) return false;
        foreach (var build in Builds)
        {
            if (Compare(version, build.MinimumMinecraftVersion) >= 0 &&
                Compare(version, build.ExclusiveMaximumMinecraftVersion) < 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Every cache folder a current build uses, for sweeping the rest.</summary>
    public static IReadOnlyCollection<string> CacheFileIds { get; } =
        Builds.Select(build => build.CacheFileId.ToString(CultureInfo.InvariantCulture)).ToArray();

    internal static bool TryParseVersion(string? value, out int[] parts)
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

    internal static int Compare(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        for (var index = 0; index < Math.Max(left.Count, right.Count); index++)
        {
            var leftPart = index < left.Count ? left[index] : 0;
            var rightPart = index < right.Count ? right[index] : 0;
            if (leftPart != rightPart) return leftPart.CompareTo(rightPart);
        }
        return 0;
    }
}
