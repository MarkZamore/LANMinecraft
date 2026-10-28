using System.IO;
using System.IO.Compression;
using System.Text;
using Minecraft;

namespace Minecraft.Tests;

public sealed class ModRuntimeMetadataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mod-runtime-" + Guid.NewGuid().ToString("N"));

    public ModRuntimeMetadataTests() => Directory.CreateDirectory(Path.Combine(_root, "mods"));
    public void Dispose() => TempTree.Delete(_root);

    private void Jar(string name, params (string Name, string Content)[] entries)
    {
        using var stream = File.Create(Path.Combine(_root, "mods", name));
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry.Name).Open(), new UTF8Encoding(false));
            writer.Write(entry.Content);
        }
    }

    [Fact]
    public void NeoForge_FmlApiIsSeparateFromActualPlatformVersion()
    {
        Jar("test.jar", ("META-INF/neoforge.mods.toml", """
            modLoader = 'javafml'
            loaderVersion = '[4,5)'
            [[dependencies.example]]
            modId = 'minecraft'
            type = 'required'
            versionRange = '[1.21.1]'
            [[dependencies.example]]
            modId = 'neoforge'
            type = 'required'
            versionRange = '[21.1.100,)'
            """));
        var option = Assert.Single(Assert.Single(ModRuntimeMetadata.Read(_root)).Options);
        Assert.Equal(PackLoaderKind.NeoForge, option.Loader);
        Assert.Equal(["[4,5)"], option.FmlRanges);
        Assert.Equal("javafml", option.FmlLanguage);
        Assert.Equal(["[21.1.100,)"], option.LoaderRanges);
        Assert.Equal(["[1.21.1]"], option.MinecraftRanges);
    }

    [Fact]
    public void ForgeToml_NeoForgeDependencyIdentifiesEarlyNeoForge()
    {
        Jar("early.jar", ("META-INF/mods.toml", """
            modLoader="javafml"
            loaderVersion="[2,)"
            [[dependencies.example]]
            modId="neoforge"
            versionRange="[20.4.72-beta,)"
            [[dependencies.example]]
            modId="minecraft"
            versionRange="[1.20.4]"
            """));
        var option = Assert.Single(Assert.Single(ModRuntimeMetadata.Read(_root)).Options);
        Assert.Equal(PackLoaderKind.NeoForge, option.Loader);
        Assert.Equal(["[20.4.72-beta,)"], option.LoaderRanges);
    }

    [Fact]
    public void MultiloaderJar_HasAlternativesAndNeutralLibrariesStayNeutral()
    {
        Jar("multi.jar", ("fabric.mod.json", """{"depends":{"minecraft":"1.21.1","fabricloader":">=0.16"}}"""),
            ("META-INF/neoforge.mods.toml", """
            modLoader="lowcodefml"
            loaderVersion="[4,)"
            [[dependencies.example]]
            modId="minecraft"
            versionRange="[1.21.1]"
            """));
        Jar("library.jar", ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nFMLModType: LIBRARY\n"));
        var jars = ModRuntimeMetadata.Read(_root);
        Assert.Empty(jars.Single(jar => jar.FileName == "library.jar").Options);
        Assert.True(jars.Single(jar => jar.FileName == "library.jar").IsNeutralLibrary);
        Assert.Equal([PackLoaderKind.NeoForge, PackLoaderKind.Fabric], jars.Single(jar => jar.FileName == "multi.jar").Options.Select(option => option.Loader));
    }

    [Fact]
    public void FabricVersionArrays_AreOrNotAnd_AndDoNotInventQuiltSupport()
    {
        Jar("fabric.jar", ("fabric.mod.json", """{"depends":{"minecraft":["1.20.1","1.21.1"],"fabricloader":">=0.16 <0.20","irrelevant":{"unknown":true}}}"""));
        var option = Assert.Single(Assert.Single(ModRuntimeMetadata.Read(_root)).Options);
        Assert.Equal(PackLoaderKind.Fabric, option.Loader);
        Assert.True(StrictVersionRange.Accepts(Assert.Single(option.MinecraftRanges), "1.21.1"));
        Assert.False(StrictVersionRange.Accepts(Assert.Single(option.MinecraftRanges), "26.3"));
        Assert.False(StrictVersionRange.Accepts(Assert.Single(option.LoaderRanges), "0.20"));
    }

    [Fact]
    public void QuiltNativeMetadata_PreservesQuiltAndExpandsAnyAllExactly()
    {
        Jar("quilt.jar", ("quilt.mod.json", """
            {"quilt_loader":{"depends":[
                {"id":"minecraft","versions":{"any":[{"all":[">=1.20.1","<1.21"]},"=26.3"]}},
                {"id":"quilt_loader","versions":">=0.29.0-beta.1"}
            ]}}
            """));
        var options = Assert.Single(ModRuntimeMetadata.Read(_root)).Options;
        Assert.Equal(2, options.Count);
        Assert.All(options, option => Assert.Equal(PackLoaderKind.Quilt, option.Loader));
        Assert.True(options.Any(option => option.MinecraftRanges.All(range => StrictVersionRange.Accepts(range, "1.20.6"))));
        Assert.True(options.Any(option => option.MinecraftRanges.All(range => StrictVersionRange.Accepts(range, "26.3"))));
        Assert.False(options.Any(option => option.MinecraftRanges.All(range => StrictVersionRange.Accepts(range, "1.21.1"))));
    }

    [Fact]
    public void QuiltBareVersionUsesCaret_AndDependencyArraysAreAlternatives()
    {
        Jar("quilt.jar", ("quilt.mod.json", """
            {"quilt_loader":{"depends":[
                [{"id":"minecraft","versions":"26.1"},{"id":"minecraft","versions":"=1.21.1"}]
            ]}}
            """));
        var options = Assert.Single(ModRuntimeMetadata.Read(_root)).Options;
        Assert.Equal(2, options.Count);
        Assert.Contains(options, option => option.MinecraftRanges.All(range => StrictVersionRange.Accepts(range, "26.3")));
        Assert.Contains(options, option => option.MinecraftRanges.All(range => StrictVersionRange.Accepts(range, "1.21.1")));
        Assert.DoesNotContain(options, option => option.MinecraftRanges.All(range => StrictVersionRange.Accepts(range, "27.0")));
    }

    [Fact]
    public void QuiltFabricAliasConstraints_RemainSeparateFromNativeQuiltLoader()
    {
        Jar("quilt.jar", ("quilt.mod.json", """
            {"quilt_loader":{"depends":[
                {"id":"minecraft","versions":"=26.3"},
                {"id":"quilt_loader","versions":">=0.29"},
                {"id":"fabricloader","versions":{"any":[{"all":[">=0.16","<0.17"]},"=0.18"]}}
            ]}}
            """));
        var options = Assert.Single(ModRuntimeMetadata.Read(_root)).Options;
        Assert.Equal(2, options.Count);
        Assert.All(options, option =>
        {
            Assert.Equal(PackLoaderKind.Quilt, option.Loader);
            Assert.Equal([">=0.29"], option.LoaderRanges);
            Assert.Equal(["=26.3"], option.MinecraftRanges);
            Assert.NotEmpty(option.FabricApiRanges);
        });
        Assert.Contains(options, option => option.FabricApiRanges.All(range => StrictVersionRange.Accepts(range, "0.16.10")));
        Assert.Contains(options, option => option.FabricApiRanges.All(range => StrictVersionRange.Accepts(range, "0.18")));
        Assert.DoesNotContain(options, option => option.FabricApiRanges.All(range => StrictVersionRange.Accepts(range, "0.29")));
    }

    [Fact]
    public void LegacyMcmodInfo_ReadsActualMinecraftAndForgeRequirement()
    {
        Jar("legacy.jar", ("mcmod.info", """
            {"modList":[{"modid":"legacy","mcversion":"1.7.10","dependencies":["required-after:Forge@[10.13.4.1558,)"]}]}
            """));
        var option = Assert.Single(Assert.Single(ModRuntimeMetadata.Read(_root)).Options);
        Assert.Equal(PackLoaderKind.Forge, option.Loader);
        Assert.Equal(["1.7.10"], option.MinecraftRanges);
        Assert.Equal(["[10.13.4.1558,)"], option.LoaderRanges);
    }

    [Fact]
    public void DescriptionCannotManufactureDependencies_AndLowcodeDoesNotInventMinecraft()
    {
        Jar("lowcode.jar", ("META-INF/neoforge.mods.toml", """"
            modLoader="lowcodefml"
            loaderVersion="[4,)"
            [[mods]]
            description='''
            [[dependencies.fake]]
            modId="minecraft"
            versionRange="[26.3]"
            '''
            """"));
        var option = Assert.Single(Assert.Single(ModRuntimeMetadata.Read(_root)).Options);
        Assert.Empty(option.MinecraftRanges);
        Assert.Equal(["[4,)"], option.FmlRanges);
        Assert.Equal("lowcodefml", option.FmlLanguage);
    }

    [Theory]
    [InlineData("javafml")]
    [InlineData("lowcodefml")]
    public void LanguageKind_IsPreservedEvenWhenTheApiRangeIsAbsent(string language)
    {
        Jar("language.jar", ("META-INF/neoforge.mods.toml", $"modLoader=\"{language}\"\n"));
        var option = Assert.Single(Assert.Single(ModRuntimeMetadata.Read(_root)).Options);
        Assert.Equal(language, option.FmlLanguage);
        Assert.Empty(option.FmlRanges);
    }

    [Fact]
    public void OptionalBuiltinDependency_StillConstrainsItsPresentRuntime()
    {
        Jar("optional.jar", ("META-INF/mods.toml", """
            [[dependencies.example]]
            modId="minecraft"
            mandatory=false
            type="optional"
            versionRange="[1.20.1]"
            """));
        var option = Assert.Single(Assert.Single(ModRuntimeMetadata.Read(_root)).Options);
        Assert.Equal(["[1.20.1]"], option.MinecraftRanges);
    }

    [Theory]
    [InlineData("REQUIRED", false)]
    [InlineData("ReQuIrEd", false)]
    [InlineData("OPTIONAL", false)]
    [InlineData("OpTiOnAl", false)]
    [InlineData("INCOMPATIBLE", true)]
    [InlineData("InCoMpAtIbLe", true)]
    public void NeoForgeDependencyType_UsesNativeCaseInsensitiveEnumSemantics(string type, bool excluded)
    {
        Jar("native-type.jar", ("META-INF/neoforge.mods.toml", $"""
            [[dependencies.example]]
            modId="minecraft"
            type="{type}"
            versionRange="[26.3]"
            """));
        if (excluded)
        {
            var error = Assert.Throws<InvalidDataException>(() => ModRuntimeMetadata.Read(_root));
            Assert.Contains("native-type.jar", error.Message);
            Assert.Contains("portable-pack.json", error.Message);
        }
        else
        {
            var option = Assert.Single(Assert.Single(ModRuntimeMetadata.Read(_root)).Options);
            Assert.Equal(PackLoaderKind.NeoForge, option.Loader);
            Assert.Equal(["[26.3]"], option.MinecraftRanges);
        }
    }

    [Fact]
    public void DiscouragedNeoForgeDependency_DoesNotReclassifyAForgeJar()
    {
        Jar("forge.jar", ("META-INF/mods.toml", """
            [[dependencies.example]]
            modId="minecraft"
            versionRange="[1.20.1]"
            [[dependencies.example]]
            modId="forge"
            versionRange="[47,)"
            [[dependencies.example]]
            modId="neoforge"
            type="discouraged"
            versionRange="[20,)"
            """));
        var option = Assert.Single(Assert.Single(ModRuntimeMetadata.Read(_root)).Options);
        Assert.Equal(PackLoaderKind.Forge, option.Loader);
        Assert.Equal(["[47,)"], option.LoaderRanges);
    }

    [Theory]
    [InlineData("FMLCorePlugin: example.CorePlugin\n", "payload.class")]
    [InlineData("TweakClass: example.Tweaker\n", "payload.class")]
    [InlineData("MixinConfigs: example.mixins.json\n", "example.mixins.json")]
    [InlineData("MixinConnector: example.Connector\n", "payload.class")]
    [InlineData("FMLAT: access-transformers.cfg\n", "META-INF/access-transformers.cfg")]
    [InlineData("", "META-INF/coremods.json")]
    [InlineData("", "META-INF/services/example.Provider")]
    public void ExplicitLibrary_WithConflictingCodeMarkersIsNotNeutral(string extraManifest, string payload)
    {
        Jar("not-neutral.jar", ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nFMLModType: LIBRARY\n" + extraManifest),
            (payload, "not executed"));
        Assert.False(Assert.Single(ModRuntimeMetadata.Read(_root)).IsNeutralLibrary);
    }

    [Theory]
    [InlineData("fabric.mod.json", "{\"depends\":{\"minecraft\":\"latest\"}}")]
    [InlineData("fabric.mod.json", "{\"depends\":{\"minecraft\":[]}}")]
    [InlineData("fabric.mod.json", "{\"schemaVersion\":99,\"depends\":{\"minecraft\":\"26.3\"}}")]
    [InlineData("fabric.mod.json", "{\"depends\":{\"minecraft\":\">=1.20\"},\"breaks\":{\"minecraft\":\">=1.21\"}}")]
    [InlineData("fabric.mod.json", "{\"depends\":{\"minecraft\":\"1.21.1\",\"minecraft\":\"26.3\"}}")]
    [InlineData("quilt.mod.json", "{\"quilt_loader\":{\"depends\":[{\"id\":\"minecraft\",\"unless\":\"other\",\"versions\":\"*\"}]}}")]
    [InlineData("quilt.mod.json", "{\"quilt_loader\":{\"depends\":[{\"id\":\"fabricloader\",\"unless\":\"other\",\"versions\":\"*\"}]}}")]
    [InlineData("quilt.mod.json", "{\"quilt_loader\":{\"depends\":[{\"id\":\"minecraft\",\"versions\":{\"unknown\":[]}}]}}")]
    [InlineData("quilt.mod.json", "{\"schema_version\":99,\"quilt_loader\":{\"depends\":[{\"id\":\"minecraft\",\"versions\":\"=26.3\"}]}}")]
    [InlineData("quilt.mod.json", "{\"quilt_loader\":{\"depends\":[{\"id\":\"minecraft\",\"versions\":\">=1.20\"}],\"breaks\":[[{\"id\":\"minecraft\",\"versions\":\">=1.21\"}]]}}")]
    [InlineData("quilt.mod.json", "{\"quilt_loader\":{\"depends\":[{\"id\":\"minecraft\",\"versions\":\"=26.3\"}],\"breaks\":[[{\"id\":\"fabricloader\",\"versions\":\">=0.16\"}]]}}")]
    [InlineData("quilt.mod.json", "{\"quilt_loader\":{\"depends\":[[{\"id\":\"minecraft\",\"versions\":\"=1.20.1\"},{\"id\":\"missing-library\",\"versions\":\"*\"}]]}}")]
    [InlineData("quilt.mod.json", "{\"quilt_loader\":{\"depends\":[[{\"id\":\"fabricloader\",\"versions\":\">=0.16\"},{\"id\":\"missing-library\",\"versions\":\"*\"}]]}}")]
    [InlineData("META-INF/mods.toml", "modLoader=\"javafml\"\nloaderVersion=\"latest\"")]
    [InlineData("META-INF/neoforge.mods.toml", "[[dependencies.test]]\nmodId=\"minecraft\"\ntype=\"incompatible\"\nversionRange=\"[26.3]\"")]
    [InlineData("META-INF/mods.toml", "[[dependencies.test]]\nmodId=\"minecraft\"\nversionRange=\"[1.21.1]\"\nversionRange=\"[26.3]\"")]
    [InlineData("mcmod.info", "[{\"mcversion\":\"1.7.10\",\"dependencies\":[\"required-after:Forge@latest\"]}]")]
    [InlineData("mcmod.info", "{\"modList\":[{\"mcversion\":\"1.20.1\"}],\"modList\":[{\"mcversion\":\"26.3\"}]}")]
    public void MalformedRelevantMetadata_IsARefusal(string entry, string contents)
    {
        Jar("bad.jar", (entry, contents));
        Assert.Contains("bad.jar", Assert.Throws<InvalidDataException>(() => ModRuntimeMetadata.Read(_root)).Message);
    }

    [Fact]
    public void ArchiveDuplicateMetadataAndOversizedMetadata_AreRejected()
    {
        Jar("bad.jar", ("fabric.mod.json", "{}"), ("FABRIC.MOD.JSON", "{}"));
        Assert.Throws<InvalidDataException>(() => ModRuntimeMetadata.Read(_root));
        File.Delete(Path.Combine(_root, "mods", "bad.jar"));
        Jar("huge.jar", ("fabric.mod.json", new string(' ', 2 * 1024 * 1024 + 1)));
        Assert.Throws<InvalidDataException>(() => ModRuntimeMetadata.Read(_root));
    }

    [Fact]
    public void ReaderOnlyInspectsTopLevelModsJars_AndNeverExtractsZipEntries()
    {
        Jar("library.jar", ("../fabric.mod.json", "{bad}"), ("payload.exe", "not executed"));
        File.WriteAllText(Path.Combine(_root, "client.jar"), "not a mod jar");
        Directory.CreateDirectory(Path.Combine(_root, "mods", "nested"));
        File.WriteAllText(Path.Combine(_root, "mods", "nested", "bad.jar"), "not a zip");
        var jar = Assert.Single(ModRuntimeMetadata.Read(_root));
        Assert.Equal("library.jar", jar.FileName);
        Assert.Empty(jar.Options);
        Assert.False(jar.IsNeutralLibrary);
        Assert.False(File.Exists(Path.Combine(_root, "fabric.mod.json")));
    }
}
