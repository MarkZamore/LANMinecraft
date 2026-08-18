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
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
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
            MemorySizingService.GetRecommendedDefaultMemoryGb(profile, 32UL * 1024 * 1024 * 1024) <= 6,
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
}
