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
    /// Every build its author publishes, and therefore every pack this
    /// launcher can carry into a Steam session.
    ///
    /// All nineteen. There is no shortlist any more and no version of
    /// Minecraft between 1.7 and 26.2 that is left out for want of a row: a
    /// player who assembles a pack of their own gets Steam play if e4steam has
    /// a build for it, and it has one for almost everything.
    ///
    /// Every range is read from the build's own metadata rather than from its
    /// file name. The names understate them - the file called
    /// forge-mc1.18.2-1.20.2 declares Minecraft up to 1.20.3 - and the six
    /// oldest Forge builds predate mods.toml entirely and declare "1.12.x" and
    /// the like in mcmod.info, which is where those ranges come from.
    /// </summary>
    public static IReadOnlyList<SteamTransportBuild> Builds { get; } =
        Array.AsReadOnly<SteamTransportBuild>(
        [
            new(
                Version,
                "e4steam-forge-mc1.7.x-v0.3.0.jar",
                7_209_088,
                "438385f680998080974fdae19f8eab937e2ae4833c28cee38fbe4a753ecbdac6",
                30_001,
                [PackLoaderKind.Forge],
                [1, 7],
                [1, 8]),
            new(
                Version,
                "e4steam-forge-mc1.8.x-v0.3.0.jar",
                2_925_015,
                "0b1b9cb197d00f7966e026c225776168b39a2bb9e460b34915705871551f554d",
                30_002,
                [PackLoaderKind.Forge],
                [1, 8],
                [1, 9]),
            new(
                Version,
                "e4steam-forge-mc1.9.x-v0.3.0.jar",
                2_924_633,
                "bd37c8e67c4a54700ef60f2fae448e08a5daf089374cefbb1c5a34779361c81f",
                30_003,
                [PackLoaderKind.Forge],
                [1, 9],
                [1, 10]),
            new(
                Version,
                "e4steam-forge-mc1.10.x-v0.3.0.jar",
                2_924_637,
                "f6e1b2505e2b13a22c6e7b5abd9d9bd5da89b664807376cc40951b882c0b5182",
                30_004,
                [PackLoaderKind.Forge],
                [1, 10],
                [1, 11]),
            new(
                Version,
                "e4steam-forge-mc1.11.x-v0.3.0.jar",
                2_924_637,
                "ad991789c1998a1f37a7c75ccdcd9ceb454a4153aee1ad6c978acf0238ed31f3",
                30_005,
                [PackLoaderKind.Forge],
                [1, 11],
                [1, 12]),
            new(
                Version,
                "e4steam-forge-mc1.12.x-v0.3.0.jar",
                2_924_661,
                "a3f1b5e6ca37894f7025b51724c32fd813f3bcc6b596eff75d8ef48587b293f7",
                30_006,
                [PackLoaderKind.Forge],
                [1, 12],
                [1, 13]),
            new(
                Version,
                "e4steam-forge-mc1.13.x-v0.3.0.jar",
                2_797_406,
                "9fa6e50a5dd1921cf2ebaca062162a6350a67ced210181843eaea8d14e8b540d",
                30_007,
                [PackLoaderKind.Forge],
                [1, 13],
                [1, 14]),
            new(
                Version,
                "e4steam-forge-mc1.14.x-v0.3.0.jar",
                2_800_207,
                "2dc36dd3bb96e743ee503a185b20c3b5090fdf98f759a01321f3366c05e7ff9b",
                30_008,
                [PackLoaderKind.Forge],
                [1, 14],
                [1, 15]),
            new(
                Version,
                "e4steam-forge-mc1.15.x-v0.3.0.jar",
                2_795_343,
                "0526b6957390548653bd7fcb11c12929317e59b09c1971c8fd3b8200bd2c57c5",
                30_009,
                [PackLoaderKind.Forge],
                [1, 15],
                [1, 16]),
            new(
                Version,
                "e4steam-forge-mc1.16.x-v0.3.0.jar",
                2_795_660,
                "6998cb00175e6d506ed1f79d75f70f3d417f402ca7bbc6243757bf98c6de303a",
                30_010,
                [PackLoaderKind.Forge],
                [1, 16],
                [1, 17]),
            new(
                Version,
                "e4steam-forge-mc1.17.1-1.18.1-v0.3.0.jar",
                3_636_879,
                "2c6155665bfd5aacf2663f170d6874db1f3a6ada42b13af819242dd46002d0ed",
                30_011,
                [PackLoaderKind.Forge],
                [1, 17, 1],
                [1, 18, 2]),
            new(
                Version,
                "e4steam-forge-mc1.18.2-1.20.2-v0.3.0.jar",
                3_633_909,
                "7351b3e21845c6928fa8bf6ed834e2a9cbab660b7513afabb117b848f7670d15",
                30_012,
                [PackLoaderKind.Forge],
                [1, 18, 2],
                [1, 20, 3]),
            new(
                Version,
                "e4steam-neoforge-mc1.20.2-26.2-v0.3.0.jar",
                3_634_360,
                "3d2b56b50f6646733a3e41e67aedb3cb7baf96e48707284083c473908bbf4adb",
                30_013,
                [PackLoaderKind.NeoForge],
                [1, 20, 2],
                [26, 3]),
            new(
                Version,
                "e4steam-fabric-mc1.14.x-v0.3.0.jar",
                3_486_907,
                "408edb8b3f44817038f60b88eaef8abdccaf96c12e2f7905477ba63d3d274b49",
                30_014,
                [PackLoaderKind.Fabric],
                [1, 14],
                [1, 15]),
            new(
                Version,
                "e4steam-fabric-mc1.15.x-v0.3.0.jar",
                3_485_464,
                "28fcc5871a1d8cd3a73d1da3fcc0c738da660988a224b381f0b51ba6f53fda11",
                30_015,
                [PackLoaderKind.Fabric],
                [1, 15],
                [1, 16]),
            new(
                Version,
                "e4steam-fabric-mc1.16.x-v0.3.0.jar",
                3_485_768,
                "66719c182a2c2c0d9c0bbc988099974427ed569e6a1b053fc3e3c793ad622d68",
                30_016,
                [PackLoaderKind.Fabric],
                [1, 16],
                [1, 17]),
            new(
                Version,
                "e4steam-fabric-quilt-mc1.17-1.18.2-v0.3.0.jar",
                3_634_508,
                "f1fb415f6e7d019381a14d1c9b39fe1b84cb585d7f3f673f85c5ef23bc939db7",
                30_017,
                [PackLoaderKind.Fabric, PackLoaderKind.Quilt],
                [1, 17],
                [1, 19]),
            new(
                Version,
                "e4steam-fabric-quilt-mc1.19-1.21.11-v0.3.0.jar",
                3_631_710,
                "d22b994d94a48143640879fff7a52071a32a3f3fcea3ce686052a001b26e8a1f",
                30_018,
                [PackLoaderKind.Fabric, PackLoaderKind.Quilt],
                [1, 19],
                [26, 1]),
            new(
                Version,
                "e4steam-fabric-quilt-mc26.1-26.2-v0.3.0.jar",
                3_632_179,
                "6578e4d71a5e1499d35aeff699ac84ade6047e104a0439efca9d13cdea0ad443",
                30_019,
                [PackLoaderKind.Fabric, PackLoaderKind.Quilt],
                [26, 1],
                [26, 3])
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
