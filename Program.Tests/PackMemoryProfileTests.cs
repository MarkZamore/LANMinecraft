using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Memory sizing asks the pack folder what it weighs, so what counts as weight
/// is pinned here: loaded jars, wherever in the mods tree they sit, the texture
/// the pack ships loose, and the Minecraft the manifest names. A pack that is
/// not there at all is not weighed - it is unknown, and the sizing rules have
/// their own answer for that.
/// </summary>
public sealed class PackMemoryProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-pack-memory-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        TempTree.Delete(_root);
    }

    [Fact]
    public void APackFolder_IsWeighedByWhatTheGameWillLoad()
    {
        var pack = Path.Combine(_root, "Limitless");
        WriteFile(Path.Combine(pack, "mods", "one.jar"), 3 * 1024 * 1024);
        // Packs that keep one mods folder per loader version put jars a level
        // down; those load like any other.
        WriteFile(Path.Combine(pack, "mods", "1.21.1", "two.jar"), 1024 * 1024);
        // A jar renamed out of the way is not loaded and must not be counted.
        WriteFile(Path.Combine(pack, "mods", "three.jar.disabled"), 8 * 1024 * 1024);
        WriteFile(Path.Combine(pack, "resourcepacks", "faithful.zip"), 2 * 1024 * 1024);
        WriteFile(Path.Combine(pack, "shaderpacks", "complementary.zip"), 1024 * 1024);
        WriteFile(Path.Combine(pack, "config", "anything.toml"), 4 * 1024 * 1024);
        WriteManifest(pack, "1.21.1");

        var profile = PackMemoryProfile.Measure(pack);

        Assert.True(profile.IsKnown);
        Assert.Equal(2, profile.ModCount);
        Assert.Equal(4L * 1024 * 1024, profile.ModBytes);
        Assert.Equal(3L * 1024 * 1024, profile.AssetBytes);
        Assert.Equal("1.21.1", profile.MinecraftVersion);
        Assert.True(profile.IsModernMinecraft);
    }

    /// <summary>A vanilla pack has no mods folder at all, and that is a weight too.</summary>
    [Fact]
    public void AVanillaPack_IsKnownAndWeighsNothingButItself()
    {
        var pack = Path.Combine(_root, "Vanilla");
        WriteManifest(pack, "1.7.10", loader: null);

        var profile = PackMemoryProfile.Measure(pack);

        Assert.True(profile.IsKnown);
        Assert.Equal(0, profile.ModCount);
        Assert.Equal(0, profile.ModBytes);
        Assert.False(profile.IsModernMinecraft);
        Assert.True(
            MemorySizingService.GetRecommendedMemoryGb(profile, 32UL * 1024 * 1024 * 1024) <= 6,
            "vanilla must not be offered a modpack's number");
    }

    [Fact]
    public void APackThatIsNotThere_IsUnknown()
    {
        Assert.False(PackMemoryProfile.Measure(Path.Combine(_root, "missing")).IsKnown);
        Assert.False(PackMemoryProfile.Measure("").IsKnown);
    }

    /// <summary>
    /// A folder with jars but no readable manifest is still weighed; only the
    /// version is missing, and an unreadable version counts as the modern one,
    /// which is the larger estimate of the two.
    /// </summary>
    [Fact]
    public void APackWithoutAManifest_IsStillWeighed()
    {
        var pack = Path.Combine(_root, "Nameless");
        WriteFile(Path.Combine(pack, "mods", "one.jar"), 1024);

        var profile = PackMemoryProfile.Measure(pack);

        Assert.True(profile.IsKnown);
        Assert.Equal(1, profile.ModCount);
        Assert.Null(profile.MinecraftVersion);
        Assert.True(profile.IsModernMinecraft);
    }

    private static void WriteFile(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private static void WriteManifest(string packDirectory, string minecraftVersion, string? loader = "neoforge")
    {
        Directory.CreateDirectory(packDirectory);
        var loaderJson = loader is null
            ? """{"type": "vanilla"}"""
            : $$"""{"type": "{{loader}}", "version": "21.1.100"}""";
        File.WriteAllText(
            Path.Combine(packDirectory, PackManifestService.ManifestFileName),
            $$"""
            {
              "schemaVersion": {{PackManifestService.CurrentSchemaVersion}},
              "minecraftVersion": "{{minecraftVersion}}",
              "loader": {{loaderJson}},
              "clientJar": "client.jar"
            }
            """);
    }

    /// <summary>
    /// A pack that is offered but not installed is weighed from the file list
    /// its manifest publishes: the jars under mods, and the texture under
    /// resourcepacks and shaderpacks. Nothing else counts, and nothing is
    /// fetched.
    /// </summary>
    [Fact]
    public void APublishedFileList_IsWeighedByItsModsAndItsTexture()
    {
        var profile = PackMemoryProfile.FromPublishedFiles(
            [
                ("mods/create-1.21.1-6.0.10.jar", 20_000_000),
                ("mods/sodium.jar", 2_000_000),
                ("mods/notes.txt", 900),                       // not a jar
                ("config/foo.json", 4_000),                    // not memory
                ("resourcepacks/pretty.zip", 8_000_000),
                ("shaderpacks/shiny.zip", 1_000_000),
                ("minecraft-1.21.1-client.jar", 26_000_000)    // not under mods
            ],
            "1.21.1");

        Assert.True(profile.IsKnown);
        Assert.Equal(2, profile.ModCount);
        Assert.Equal(2, profile.JarCount);
        Assert.Equal(22_000_000, profile.ModBytes);
        Assert.Equal(9_000_000, profile.AssetBytes);
        Assert.Equal("1.21.1", profile.MinecraftVersion);
    }

    /// <summary>A manifest with no mods in it is one this does not understand.</summary>
    [Fact]
    public void APublishedListWithNoMods_IsNotAWeight()
    {
        Assert.False(
            PackMemoryProfile.FromPublishedFiles([("config/foo.json", 10)], "1.21.1").IsKnown);
        Assert.False(PackMemoryProfile.FromPublishedFiles([], null).IsKnown);
    }

    /// <summary>A manifest written with backslashes says the same thing.</summary>
    [Fact]
    public void APublishedListIsReadWhicheverSlashItUses()
    {
        var profile = PackMemoryProfile.FromPublishedFiles(
            [("mods\\a.jar", 5), ("resourcepacks\\b.zip", 7)], null);
        Assert.Equal(1, profile.ModCount);
        Assert.Equal(7, profile.AssetBytes);
    }

    /// <summary>
    /// The point of the whole thing. Create &amp; Ars is 88 jars and All The
    /// Mods-sized packs are hundreds; before this, neither was weighed until it
    /// was installed and both were offered two thirds of the machine - so the
    /// small one asked for more than the large one, which is what a player sees
    /// and disbelieves.
    /// </summary>
    [Fact]
    public void TheSmallPack_NowAsksForLessThanTheLargeOne()
    {
        var small = PackMemoryProfile.FromPublishedFiles(
            Enumerable.Range(0, 88).Select(i => ($"mods/small{i}.jar", 1_200_000L)), "1.21.1");
        var large = PackMemoryProfile.FromPublishedFiles(
            Enumerable.Range(0, 882).Select(i => ($"mods/large{i}.jar", 2_200_000L)), "1.21.1");

        var smallGb = MemorySizingService.GetRecommendedMemoryGb(small);
        var largeGb = MemorySizingService.GetRecommendedMemoryGb(large);
        var unweighed = MemorySizingService.GetRecommendedMemoryGb(PackMemoryProfile.Unknown);

        Assert.True(
            smallGb < largeGb,
            $"a pack of 88 jars must not ask for more than one of 882: {smallGb} vs {largeGb}");
        Assert.True(
            smallGb < unweighed,
            $"weighing the small pack must beat the machine-sized guess: {smallGb} vs {unweighed}");
        // And it lands where the pack was built to land: two and a half to three.
        Assert.InRange(smallGb, 2, 4);
    }
}
