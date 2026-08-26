using System.IO;
using System.IO.Compression;
using System.Text;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Reading a folder of mods and saying what pack it is.
/// </summary>
/// <remarks>
/// Every case here is one a real pack contains. The rules were settled by
/// measuring 1101 jars across five packs, and the ones that look like
/// over-caution are the ones that were wrong the first time: a jar carrying two
/// loaders' metadata, a range whose author excluded his own version, a NeoForge
/// pack legitimately full of Fabric mods.
/// </remarks>
public sealed class PackDetectionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-detect-{Guid.NewGuid():N}");

    public void Dispose() => TempTree.Delete(_root);

    private string Pack(string name = "pack")
    {
        var pack = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(pack, "mods"));
        return pack;
    }

    /// <summary>Writes a jar carrying whichever metadata files are named.</summary>
    private static void Jar(string pack, string name, params (string Entry, string Text)[] entries)
    {
        using var file = File.Create(Path.Combine(pack, "mods", name));
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (entry, text) in entries)
        {
            using var stream = archive.CreateEntry(entry).Open();
            stream.Write(Encoding.UTF8.GetBytes(text));
        }
    }

    private static (string, string) ForgeToml(string range) => ("META-INF/mods.toml", $"""
        modLoader="javafml"
        loaderVersion="[47,)"
        [[dependencies.example]]
        modId="forge"
        mandatory=true
        versionRange="[47,)"
        [[dependencies.example]]
        modId="minecraft"
        mandatory=true
        versionRange="{range}"
        """);

    private static (string, string) NeoForgeToml(string range) => ("META-INF/neoforge.mods.toml", $"""
        modLoader="javafml"
        loaderVersion="[1,)"
        [[dependencies.example]]
        modId="neoforge"
        mandatory=true
        versionRange="[21.1,)"
        [[dependencies.example]]
        modId="minecraft"
        mandatory=true
        versionRange="{range}"
        """);

    private static (string, string) FabricJson(string range) => ("fabric.mod.json", $$$"""
        {"schemaVersion":1,"id":"example","version":"1.0.0",
         "depends":{"fabricloader":">=0.14.0","minecraft":"{{{range}}}"}}
        """);

    [Fact]
    public void AForgeFolder_IsForge()
    {
        var pack = Pack();
        for (var index = 0; index < 8; index++) Jar(pack, $"forge{index}.jar", ForgeToml("[1.20.1,1.21)"));

        var detected = PackDetector.Detect(pack);

        Assert.Equal(PackLoaderKind.Forge, detected.Loader);
        Assert.Equal("1.20.1", detected.MinecraftVersion);
    }

    /// <summary>
    /// The trap: NeoForge writes the same META-INF/mods.toml Forge does, and
    /// only the dependency inside it says which one meant it.
    /// </summary>
    [Fact]
    public void ModsTomlIsToldApartByItsDependency_NotItsName()
    {
        var pack = Pack();
        for (var index = 0; index < 8; index++)
        {
            Jar(pack, $"neo{index}.jar", ("META-INF/mods.toml", """
                modLoader="javafml"
                loaderVersion="[1,)"
                [[dependencies.example]]
                modId="neoforge"
                mandatory=true
                versionRange="[21.1,)"
                [[dependencies.example]]
                modId="minecraft"
                mandatory=true
                versionRange="[1.21.1]"
                """));
        }

        var detected = PackDetector.Detect(pack);

        Assert.Equal(PackLoaderKind.NeoForge, detected.Loader);
        Assert.Equal("1.21.1", detected.MinecraftVersion);
    }

    /// <summary>
    /// A jar that carries several loaders' metadata says nothing about which
    /// pack it is in - multi-loader mods ship all of it in one file - so it
    /// does not vote. Three such jars sit inside a real Forge pack.
    /// </summary>
    [Fact]
    public void AJarThatCouldRunAnywhere_DoesNotVote()
    {
        var pack = Pack();
        for (var index = 0; index < 8; index++) Jar(pack, $"forge{index}.jar", ForgeToml("[1.20.1]"));
        for (var index = 0; index < 5; index++)
        {
            Jar(pack, $"multi{index}.jar", FabricJson("1.20.1"), NeoForgeToml("[1.20.1]"), ForgeToml("[1.20.1]"));
        }

        var detected = PackDetector.Detect(pack);

        Assert.Equal(PackLoaderKind.Forge, detected.Loader);
        Assert.Contains("Forge by 8 of 8", detected.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void AFabricFolder_IsFabric()
    {
        var pack = Pack();
        for (var index = 0; index < 8; index++) Jar(pack, $"fabric{index}.jar", FabricJson(">=1.18.2 <1.19"));

        var detected = PackDetector.Detect(pack);

        Assert.Equal(PackLoaderKind.Fabric, detected.Loader);
        Assert.Equal("1.18.2", detected.MinecraftVersion);
    }

    /// <summary>
    /// A NeoForge pack may hold real Fabric mods, through Sinytra Connector.
    /// They belong there, so they are not counted against it.
    /// </summary>
    [Fact]
    public void FabricModsUnderConnector_DoNotMakeItAFabricPack()
    {
        var pack = Pack();
        Jar(pack, "connector-2.0.0-beta.17+1.21.1-full.jar");
        for (var index = 0; index < 6; index++) Jar(pack, $"neo{index}.jar", NeoForgeToml("[1.21.1,)"));
        for (var index = 0; index < 5; index++) Jar(pack, $"fabric{index}.jar", FabricJson("1.21.1"));

        var detected = PackDetector.Detect(pack);

        Assert.Equal(PackLoaderKind.NeoForge, detected.Loader);
        Assert.Equal("1.21.1", detected.MinecraftVersion);
    }

    /// <summary>
    /// Voting, not intersecting. Two jars exclude the very version their pack
    /// runs on - real jars do this, and intersecting the ranges of Limitless 8
    /// returns nothing at all over 754 of them.
    /// </summary>
    [Fact]
    public void AFewModsExcludingTheRightVersion_DoNotVetoIt()
    {
        var pack = Pack();
        for (var index = 0; index < 10; index++) Jar(pack, $"neo{index}.jar", NeoForgeToml("[1.21.1]"));
        Jar(pack, "mysticalagriculture.jar", NeoForgeToml("[1.21,1.21.1)"));
        Jar(pack, "cucumber.jar", NeoForgeToml("[1.21,1.21.1)"));

        var detected = PackDetector.Detect(pack);

        Assert.Equal("1.21.1", detected.MinecraftVersion);
    }

    /// <summary>
    /// A folder holding two packs at once gets no answer. Measured on the real
    /// jars, the loader vote came out 92 to 66 and the version won by a single
    /// jar out of 136 - a confident answer that was wrong for half the folder.
    /// </summary>
    [Fact]
    public void AFolderHoldingTwoPacks_IsRefused()
    {
        var pack = Pack();
        for (var index = 0; index < 9; index++) Jar(pack, $"fabric{index}.jar", FabricJson("1.18.2"));
        for (var index = 0; index < 8; index++) Jar(pack, $"forge{index}.jar", ForgeToml("[1.20.1]"));

        var detected = PackDetector.Detect(pack);

        Assert.Null(detected.Loader);
        Assert.Contains("disagree about the loader", detected.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Too few mods with an opinion is no answer either: a handful of jars and
    /// one stray is genuinely undecidable, and a wrong pack is worse than none.
    /// </summary>
    [Fact]
    public void TooFewModsWithAnOpinion_IsRefused()
    {
        var pack = Pack();
        for (var index = 0; index < 3; index++) Jar(pack, $"forge{index}.jar", ForgeToml("[1.20.1]"));

        var detected = PackDetector.Detect(pack);

        Assert.Null(detected.MinecraftVersion);
        Assert.Contains("too few", detected.Explanation, StringComparison.Ordinal);
    }

    /// <summary>A jar with no metadata at all abstains rather than breaking.</summary>
    [Fact]
    public void ALibraryJarWithNoMetadata_IsIgnored()
    {
        var pack = Pack();
        for (var index = 0; index < 8; index++) Jar(pack, $"fabric{index}.jar", FabricJson("1.18.2"));
        Jar(pack, "kotlinforforge-5.12.0-all.jar", ("kotlin/Unit.class", "not really a class"));

        var detected = PackDetector.Detect(pack);

        Assert.Equal(PackLoaderKind.Fabric, detected.Loader);
        Assert.Equal("1.18.2", detected.MinecraftVersion);
    }

    [Fact]
    public void AFolderWithoutMods_IsNotAPack()
    {
        Assert.Null(PackDetector.Detect(Path.Combine(_root, "absent")).Loader);
        var empty = Pack("empty");
        Assert.Contains("empty", PackDetector.Detect(empty).Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nested jars are not read. The five packs measured hold 517 of them, and
    /// their metadata would outvote the mods actually installed.
    /// </summary>
    [Fact]
    public void JarsInsideJars_AreNotCounted()
    {
        var pack = Pack();
        for (var index = 0; index < 8; index++) Jar(pack, $"neo{index}.jar", NeoForgeToml("[1.21.1]"));

        // One jar carrying a nested payload, which is never opened.
        Jar(
            pack,
            "bundle.jar",
            NeoForgeToml("[1.21.1]"),
            ("META-INF/jars/inner.jar", "not a real jar, and never opened"));

        var detected = PackDetector.Detect(pack);

        Assert.Equal(PackLoaderKind.NeoForge, detected.Loader);
        Assert.Equal("1.21.1", detected.MinecraftVersion);
    }
}
