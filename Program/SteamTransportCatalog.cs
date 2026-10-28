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
    /// <summary>The author's exact artifact, kept as provenance rather than a delivery fallback.</summary>
    public Uri UpstreamDownloadUri { get; } = new(
        $"https://github.com/Kamilhik/e4steam/releases/download/v{Version}/{FileName}",
        UriKind.Absolute);

    /// <summary>The single shared delivery source in the launcher's own repository.</summary>
    public IReadOnlyList<Uri> DownloadUris { get; } = Array.AsReadOnly<Uri>(
    [
        new Uri(
            $"https://github.com/MarkZamore/e4steam/releases/download/v{Version}/{FileName}",
            UriKind.Absolute)
    ]);
}

/// <summary>
/// The latest author transport available for each loader and Minecraft branch.
/// Metadata and SHA-256 pins belong to each artifact independently; modern
/// releases do not imply that a new retro JAR was published alongside them.
/// </summary>
public static class SteamTransportCatalog
{
    // Only exact previously shipped bytes may be replaced; unknown JARs remain conflicts.
    // A legacy row still active below is ignored when its filename is the selected build.
    internal static IReadOnlyList<ManagedComponentDescriptor> SupersededBuilds { get; } =
    [
        new(ManagedComponentService.E4steamComponentId, 30_001, "e4steam-forge-mc1.7.x-v0.3.0.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.0/e4steam-forge-mc1.7.x-v0.3.0.jar")],
            7_209_088, "438385f680998080974fdae19f8eab937e2ae4833c28cee38fbe4a753ecbdac6"),
        new(ManagedComponentService.E4steamComponentId, 30_002, "e4steam-forge-mc1.8.x-v0.3.0.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.0/e4steam-forge-mc1.8.x-v0.3.0.jar")],
            2_925_015, "0b1b9cb197d00f7966e026c225776168b39a2bb9e460b34915705871551f554d"),
        new(ManagedComponentService.E4steamComponentId, 30_003, "e4steam-forge-mc1.9.x-v0.3.0.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.0/e4steam-forge-mc1.9.x-v0.3.0.jar")],
            2_924_633, "bd37c8e67c4a54700ef60f2fae448e08a5daf089374cefbb1c5a34779361c81f"),
        new(ManagedComponentService.E4steamComponentId, 30_004, "e4steam-forge-mc1.10.x-v0.3.0.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.0/e4steam-forge-mc1.10.x-v0.3.0.jar")],
            2_924_637, "f6e1b2505e2b13a22c6e7b5abd9d9bd5da89b664807376cc40951b882c0b5182"),
        new(ManagedComponentService.E4steamComponentId, 30_005, "e4steam-forge-mc1.11.x-v0.3.0.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.0/e4steam-forge-mc1.11.x-v0.3.0.jar")],
            2_924_637, "ad991789c1998a1f37a7c75ccdcd9ceb454a4153aee1ad6c978acf0238ed31f3"),
        new(ManagedComponentService.E4steamComponentId, 30_006, "e4steam-forge-mc1.12.x-v0.3.0.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.0/e4steam-forge-mc1.12.x-v0.3.0.jar")],
            2_924_661, "a3f1b5e6ca37894f7025b51724c32fd813f3bcc6b596eff75d8ef48587b293f7"),
        new(ManagedComponentService.E4steamComponentId, 30_007, "e4steam-forge-mc1.13.x-v0.3.0.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.0/e4steam-forge-mc1.13.x-v0.3.0.jar")],
            2_797_406, "9fa6e50a5dd1921cf2ebaca062162a6350a67ced210181843eaea8d14e8b540d"),
        new(ManagedComponentService.E4steamComponentId, 30_008, "e4steam-forge-mc1.14.x-v0.3.0.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.0/e4steam-forge-mc1.14.x-v0.3.0.jar")],
            2_800_207, "2dc36dd3bb96e743ee503a185b20c3b5090fdf98f759a01321f3366c05e7ff9b"),
        new(ManagedComponentService.E4steamComponentId, 30_009, "e4steam-forge-mc1.15.x-v0.3.0.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.0/e4steam-forge-mc1.15.x-v0.3.0.jar")],
            2_795_343, "0526b6957390548653bd7fcb11c12929317e59b09c1971c8fd3b8200bd2c57c5"),
        new(ManagedComponentService.E4steamComponentId, 30_010, "e4steam-forge-mc1.16.x-v0.3.0.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.0/e4steam-forge-mc1.16.x-v0.3.0.jar")],
            2_795_660, "6998cb00175e6d506ed1f79d75f70f3d417f402ca7bbc6243757bf98c6de303a"),
        new(ManagedComponentService.E4steamComponentId, 30_011, "e4steam-forge-mc1.17.1-1.18.1-v0.3.0.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.0/e4steam-forge-mc1.17.1-1.18.1-v0.3.0.jar")],
            3_636_879, "2c6155665bfd5aacf2663f170d6874db1f3a6ada42b13af819242dd46002d0ed"),
        new(ManagedComponentService.E4steamComponentId, 30_012, "e4steam-forge-mc1.18.2-1.20.2-v0.3.0.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.0/e4steam-forge-mc1.18.2-1.20.2-v0.3.0.jar")],
            3_633_909, "7351b3e21845c6928fa8bf6ed834e2a9cbab660b7513afabb117b848f7670d15"),
        new(ManagedComponentService.E4steamComponentId, 30_013, "e4steam-neoforge-mc1.20.2-26.2-v0.3.0.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.0/e4steam-neoforge-mc1.20.2-26.2-v0.3.0.jar")],
            3_634_360, "3d2b56b50f6646733a3e41e67aedb3cb7baf96e48707284083c473908bbf4adb"),
        new(ManagedComponentService.E4steamComponentId, 30_014, "e4steam-fabric-mc1.14.x-v0.3.0.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.0/e4steam-fabric-mc1.14.x-v0.3.0.jar")],
            3_486_907, "408edb8b3f44817038f60b88eaef8abdccaf96c12e2f7905477ba63d3d274b49"),
        new(ManagedComponentService.E4steamComponentId, 30_015, "e4steam-fabric-mc1.15.x-v0.3.0.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.0/e4steam-fabric-mc1.15.x-v0.3.0.jar")],
            3_485_464, "28fcc5871a1d8cd3a73d1da3fcc0c738da660988a224b381f0b51ba6f53fda11"),
        new(ManagedComponentService.E4steamComponentId, 30_016, "e4steam-fabric-mc1.16.x-v0.3.0.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.0/e4steam-fabric-mc1.16.x-v0.3.0.jar")],
            3_485_768, "66719c182a2c2c0d9c0bbc988099974427ed569e6a1b053fc3e3c793ad622d68"),
        new(ManagedComponentService.E4steamComponentId, 30_017, "e4steam-fabric-quilt-mc1.17-1.18.2-v0.3.0.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.0/e4steam-fabric-quilt-mc1.17-1.18.2-v0.3.0.jar")],
            3_634_508, "f1fb415f6e7d019381a14d1c9b39fe1b84cb585d7f3f673f85c5ef23bc939db7"),
        new(ManagedComponentService.E4steamComponentId, 30_018, "e4steam-fabric-quilt-mc1.19-1.21.11-v0.3.0.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.0/e4steam-fabric-quilt-mc1.19-1.21.11-v0.3.0.jar")],
            3_631_710, "d22b994d94a48143640879fff7a52071a32a3f3fcea3ce686052a001b26e8a1f"),
        new(ManagedComponentService.E4steamComponentId, 30_019, "e4steam-fabric-quilt-mc26.1-26.2-v0.3.0.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.0/e4steam-fabric-quilt-mc26.1-26.2-v0.3.0.jar")],
            3_632_179, "6578e4d71a5e1499d35aeff699ac84ade6047e104a0439efca9d13cdea0ad443"),
        new(ManagedComponentService.E4steamComponentId, 32_001, "e4steam-forge-mc1.7.x-v0.3.2.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.2/e4steam-forge-mc1.7.x-v0.3.2.jar")],
            2_107_709, "c44434c31a5bbfcf5cc34a9327f2c6c08e8ae633e34727486146d8f0ffb03c9c"),
        new(ManagedComponentService.E4steamComponentId, 32_002, "e4steam-forge-mc1.8.x-v0.3.2.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.2/e4steam-forge-mc1.8.x-v0.3.2.jar")],
            3_039_265, "35caf65a4d97904066e2f1f6454d1ba4661bc73130305ffdb3917f90b3449972"),
        new(ManagedComponentService.E4steamComponentId, 32_003, "e4steam-forge-mc1.9.x-v0.3.2.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.2/e4steam-forge-mc1.9.x-v0.3.2.jar")],
            3_038_887, "263b4b6f75f06aa0c5c8380d8ae34415e4ae735925bb952f9718c2bc24539e66"),
        new(ManagedComponentService.E4steamComponentId, 32_004, "e4steam-forge-mc1.10.x-v0.3.2.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.2/e4steam-forge-mc1.10.x-v0.3.2.jar")],
            3_038_892, "ede466e6c19878c513419d971ca860b533eaa1ea756bf2c34a0adfbe80ad82ba"),
        new(ManagedComponentService.E4steamComponentId, 32_005, "e4steam-forge-mc1.11.x-v0.3.2.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.2/e4steam-forge-mc1.11.x-v0.3.2.jar")],
            3_038_892, "3371478eac829f01a879962d1c2e0f3b31684aa16ab8e8ac3ec06e46bcf5a26f"),
        new(ManagedComponentService.E4steamComponentId, 32_006, "e4steam-forge-mc1.12.x-v0.3.2.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.2/e4steam-forge-mc1.12.x-v0.3.2.jar")],
            3_038_928, "f554e6d20f037dbf8a2596eb0c3aaa3cf3ba0625d1caca9a3925cad297506e24"),
        new(ManagedComponentService.E4steamComponentId, 32_007, "e4steam-forge-mc1.13.x-v0.3.2.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.2/e4steam-forge-mc1.13.x-v0.3.2.jar")],
            2_901_410, "0cdb0ef6ce450faf2583ffd75b43e17426b20b402628e7c38466ce84d080f2ec"),
        new(ManagedComponentService.E4steamComponentId, 32_008, "e4steam-forge-mc1.14.x-v0.3.2.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.2/e4steam-forge-mc1.14.x-v0.3.2.jar")],
            2_903_985, "b7d08bbfadbee160f37eeb1e665d55c4a50b4682806dd63f3db5879b8fa5e1c7"),
        new(ManagedComponentService.E4steamComponentId, 32_009, "e4steam-forge-mc1.15.x-v0.3.2.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.2/e4steam-forge-mc1.15.x-v0.3.2.jar")],
            2_899_585, "5de9df05e6834a1279631eedfa5879bf8abfa955226dc127fffd2a4292768b91"),
        new(ManagedComponentService.E4steamComponentId, 32_010, "e4steam-forge-mc1.16.x-v0.3.2.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.2/e4steam-forge-mc1.16.x-v0.3.2.jar")],
            2_899_902, "a0e6a1ab6b29c36733c929e86905521e58b2943307fab4fda7da05b51b3449ca"),
        new(ManagedComponentService.E4steamComponentId, 32_011, "e4steam-forge-mc1.17.1-1.18.1-v0.3.2.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.2/e4steam-forge-mc1.17.1-1.18.1-v0.3.2.jar")],
            3_739_438, "b3498cabcbc6ddfc87b62e433cfe2335f5f58aab182b1848cca505bffa5da99c"),
        new(ManagedComponentService.E4steamComponentId, 32_012, "e4steam-forge-mc1.18.2-1.20.2-v0.3.2.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.2/e4steam-forge-mc1.18.2-1.20.2-v0.3.2.jar")],
            3_735_682, "9a0fc4e1a8c170f37e04cf8469091990d5c1f0953b334481dbb5eab9475e3e1a"),
        new(ManagedComponentService.E4steamComponentId, 32_013, "e4steam-neoforge-mc1.20.2-26.2-v0.3.2.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.2/e4steam-neoforge-mc1.20.2-26.2-v0.3.2.jar")],
            3_734_370, "4f182b2560a6e001f248cd59cc063dd1c99d92a9b1b38ef099bc8ee822fe0418"),
        new(ManagedComponentService.E4steamComponentId, 32_014, "e4steam-fabric-mc1.14.x-v0.3.2.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.2/e4steam-fabric-mc1.14.x-v0.3.2.jar")],
            3_590_675, "c9467b97bfa7691007b4226af0ba7d776393fd013a0ddc908f36e63d818e85f2"),
        new(ManagedComponentService.E4steamComponentId, 32_015, "e4steam-fabric-mc1.15.x-v0.3.2.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.2/e4steam-fabric-mc1.15.x-v0.3.2.jar")],
            3_589_233, "61e7c1783a3338bee600660ac630ed3d6fcb220831f110661e01dbdfee7679b2"),
        new(ManagedComponentService.E4steamComponentId, 32_016, "e4steam-fabric-mc1.16.x-v0.3.2.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.2/e4steam-fabric-mc1.16.x-v0.3.2.jar")],
            3_589_535, "b387a2b076527dd2f1ccbb3cf44ac97d3f4ce89722becb5e3bc57ad7933ba70f"),
        new(ManagedComponentService.E4steamComponentId, 32_017, "e4steam-fabric-quilt-mc1.17-1.18.2-v0.3.2.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.2/e4steam-fabric-quilt-mc1.17-1.18.2-v0.3.2.jar")],
            3_735_432, "7e0b582c528c8cfbfba16c9e9b14b7e4ef3f480b56ce36cc54638a9690a5e290"),
        new(ManagedComponentService.E4steamComponentId, 32_018, "e4steam-fabric-quilt-mc1.19-1.21.11-v0.3.2.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.2/e4steam-fabric-quilt-mc1.19-1.21.11-v0.3.2.jar")],
            3_731_863, "a7575fe3efb058d6918ae8f5a375491fb9d0c3edce40f91e4b7e3155898b7a7b"),
        new(ManagedComponentService.E4steamComponentId, 32_019, "e4steam-fabric-quilt-mc26.1-26.2-v0.3.2.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.3.2/e4steam-fabric-quilt-mc26.1-26.2-v0.3.2.jar")],
            3_732_350, "1d189e09cfe2be5e441201ef4aa516b5d294e9e7b0017e6ea1b9f81230ff3012"),
    ];

    /// <summary>The newest author release; retro branches retain their newest published compatible JAR.</summary>
    public const string Version = "0.3.4";

    /// <summary>
    /// Six modern branches use 0.3.4. The author published no retro artifacts
    /// in that release, so thirteen older branches keep their latest 0.3.2 files.
    /// Every range is taken from the actual JAR metadata, not its filename.
    /// </summary>
    public static IReadOnlyList<SteamTransportBuild> Builds { get; } =
        Array.AsReadOnly<SteamTransportBuild>(
        [
            new(
                "0.3.2",
                "e4steam-forge-mc1.7.x-v0.3.2.jar",
                2_107_709,
                "c44434c31a5bbfcf5cc34a9327f2c6c08e8ae633e34727486146d8f0ffb03c9c",
                32_001,
                [PackLoaderKind.Forge],
                [1, 7],
                [1, 8]),
            new(
                "0.3.2",
                "e4steam-forge-mc1.8.x-v0.3.2.jar",
                3_039_265,
                "35caf65a4d97904066e2f1f6454d1ba4661bc73130305ffdb3917f90b3449972",
                32_002,
                [PackLoaderKind.Forge],
                [1, 8],
                [1, 9]),
            new(
                "0.3.2",
                "e4steam-forge-mc1.9.x-v0.3.2.jar",
                3_038_887,
                "263b4b6f75f06aa0c5c8380d8ae34415e4ae735925bb952f9718c2bc24539e66",
                32_003,
                [PackLoaderKind.Forge],
                [1, 9],
                [1, 10]),
            new(
                "0.3.2",
                "e4steam-forge-mc1.10.x-v0.3.2.jar",
                3_038_892,
                "ede466e6c19878c513419d971ca860b533eaa1ea756bf2c34a0adfbe80ad82ba",
                32_004,
                [PackLoaderKind.Forge],
                [1, 10],
                [1, 11]),
            new(
                "0.3.2",
                "e4steam-forge-mc1.11.x-v0.3.2.jar",
                3_038_892,
                "3371478eac829f01a879962d1c2e0f3b31684aa16ab8e8ac3ec06e46bcf5a26f",
                32_005,
                [PackLoaderKind.Forge],
                [1, 11],
                [1, 12]),
            new(
                "0.3.2",
                "e4steam-forge-mc1.12.x-v0.3.2.jar",
                3_038_928,
                "f554e6d20f037dbf8a2596eb0c3aaa3cf3ba0625d1caca9a3925cad297506e24",
                32_006,
                [PackLoaderKind.Forge],
                [1, 12],
                [1, 13]),
            new(
                "0.3.2",
                "e4steam-forge-mc1.13.x-v0.3.2.jar",
                2_901_410,
                "0cdb0ef6ce450faf2583ffd75b43e17426b20b402628e7c38466ce84d080f2ec",
                32_007,
                [PackLoaderKind.Forge],
                [1, 13],
                [1, 14]),
            new(
                "0.3.2",
                "e4steam-forge-mc1.14.x-v0.3.2.jar",
                2_903_985,
                "b7d08bbfadbee160f37eeb1e665d55c4a50b4682806dd63f3db5879b8fa5e1c7",
                32_008,
                [PackLoaderKind.Forge],
                [1, 14],
                [1, 15]),
            new(
                "0.3.2",
                "e4steam-forge-mc1.15.x-v0.3.2.jar",
                2_899_585,
                "5de9df05e6834a1279631eedfa5879bf8abfa955226dc127fffd2a4292768b91",
                32_009,
                [PackLoaderKind.Forge],
                [1, 15],
                [1, 16]),
            new(
                "0.3.2",
                "e4steam-forge-mc1.16.x-v0.3.2.jar",
                2_899_902,
                "a0e6a1ab6b29c36733c929e86905521e58b2943307fab4fda7da05b51b3449ca",
                32_010,
                [PackLoaderKind.Forge],
                [1, 16],
                [1, 17]),
            new(
                Version,
                "e4steam-forge-mc1.17.1-1.18.1-v0.3.4.jar",
                3_820_487,
                "b1a6b34ca34edff830414a676de33a7ebe6fd3a43d0f18e687b717d45d87a312",
                34_011,
                [PackLoaderKind.Forge],
                [1, 17, 1],
                [1, 18, 2]),
            new(
                Version,
                "e4steam-forge-mc1.18.2-1.20.2-v0.3.4.jar",
                3_817_117,
                "33421acbea4d904ac3cefa62ac1d4bd5f62f9e9169b81a4d16a052d2330bc6fc",
                34_012,
                [PackLoaderKind.Forge],
                [1, 18, 2],
                [1, 20, 3]),
            new(
                Version,
                "e4steam-neoforge-mc1.20.2-26.3-v0.3.4.jar",
                3_815_800,
                "b7c388393b7c27d188c17981073a2471c7ac52b95d5cff8b8dc8f19946d67be0",
                34_013,
                [PackLoaderKind.NeoForge],
                [1, 20, 2],
                [26, 4]),
            new(
                "0.3.2",
                "e4steam-fabric-mc1.14.x-v0.3.2.jar",
                3_590_675,
                "c9467b97bfa7691007b4226af0ba7d776393fd013a0ddc908f36e63d818e85f2",
                32_014,
                [PackLoaderKind.Fabric],
                [1, 14],
                [1, 15]),
            new(
                "0.3.2",
                "e4steam-fabric-mc1.15.x-v0.3.2.jar",
                3_589_233,
                "61e7c1783a3338bee600660ac630ed3d6fcb220831f110661e01dbdfee7679b2",
                32_015,
                [PackLoaderKind.Fabric],
                [1, 15],
                [1, 16]),
            new(
                "0.3.2",
                "e4steam-fabric-mc1.16.x-v0.3.2.jar",
                3_589_535,
                "b387a2b076527dd2f1ccbb3cf44ac97d3f4ce89722becb5e3bc57ad7933ba70f",
                32_016,
                [PackLoaderKind.Fabric],
                [1, 16],
                [1, 17]),
            new(
                Version,
                "e4steam-fabric-quilt-mc1.17-1.18.2-v0.3.4.jar",
                3_816_090,
                "0aaef192271c57c9af3be4a8b09e62101592953bb7c97c276df4b26c289aeef3",
                34_017,
                [PackLoaderKind.Fabric, PackLoaderKind.Quilt],
                [1, 17],
                [1, 19]),
            new(
                Version,
                "e4steam-fabric-quilt-mc1.19-1.21.11-v0.3.4.jar",
                3_812_904,
                "ec692ae255704971b24bdff28724ec48224d6c87c7e5d568e4252b4df15a447d",
                34_018,
                [PackLoaderKind.Fabric, PackLoaderKind.Quilt],
                [1, 19],
                [26, 1]),
            new(
                Version,
                "e4steam-fabric-quilt-mc26.1-26.3-v0.3.4.jar",
                3_813_546,
                "ea7ed37c3c2b8b41c7e458043b051725c22e81b9f9fa318a1474831cc484854e",
                34_019,
                [PackLoaderKind.Fabric, PackLoaderKind.Quilt],
                [26, 1],
                [26, 4])
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
