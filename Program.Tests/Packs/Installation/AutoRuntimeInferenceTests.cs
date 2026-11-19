using System.IO;
using System.IO.Compression;
using System.Text;
using Minecraft;

namespace Minecraft.Tests;

public sealed class AutoRuntimeInferenceTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "runtime-inference-" + Guid.NewGuid().ToString("N")));
    private const string Pack = "Dropped Mods";
    public void Dispose() => TempTree.Delete(_paths.Root);

    [Theory]
    [InlineData(PackLoaderKind.Forge, "1.20.1", "47.3.0", "47.3.0")]
    [InlineData(PackLoaderKind.NeoForge, "1.21.1", "21.1.259", "4.0.42")]
    [InlineData(PackLoaderKind.NeoForge, "26.3", "26.3.0.99", "12.0.12")]
    [InlineData(PackLoaderKind.Fabric, "1.21.1", "0.16.14", null)]
    [InlineData(PackLoaderKind.Quilt, "1.21.1", "0.29.3", null)]
    public async Task OneMod_IsEnoughWhenItsConstraintsAndOfficialCatalogAgree(PackLoaderKind loader, string minecraft, string version, string? fml)
    {
        var fmlMajor = fml is null ? 0 : int.Parse(fml.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
        var original = Jar("mod.jar", Metadata(loader, minecraft, "*", fml is null ? null : "[" + fmlMajor + "," + (fmlMajor + 1) + ")"));
        Assert.False(PackManifestService.HasManifest(Directory()));
        Assert.True(MinecraftProcessService.HasPackData(Directory()));
        var catalog = new Catalog([minecraft]).Add(loader, minecraft, [version], fml);
        Assert.True(await Service(catalog).EnsureAsync(Pack, default));
        var descriptor = PackManifestService.Load(Directory());
        Assert.Equal(minecraft, descriptor.MinecraftVersion);
        Assert.Equal(loader, descriptor.Loader.Type);
        Assert.Equal(version, descriptor.Loader.Version);
        Assert.Equal("", descriptor.ClientJar);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Directory(), "mods", "mod.jar")));
        var calls = catalog.Calls;
        Assert.False(await Service(catalog).EnsureAsync(Pack, default));
        Assert.Equal(calls, catalog.Calls);
    }

    [Theory]
    [InlineData("[48.3,49)")]
    [InlineData("[48]")]
    [InlineData("[48,48] || [48.2,49)")]
    [InlineData("[48,48.1) || [48.2,49)")]
    public async Task UnprovenForgeFmlPredicates_RefusePartialApiEvidence(string range)
    {
        Jar("narrow-fml.jar", Metadata(PackLoaderKind.Forge, "1.20.2", "[48,49)", range));
        var catalog = new Catalog(["1.20.2"]).Add(PackLoaderKind.Forge, "1.20.2", ["48.1.0"], "48.1.0");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Service(catalog).EnsureAsync(Pack, default));
        Assert.Contains("JavaFML", error.Message, StringComparison.Ordinal);
        Assert.Contains("portable-pack.json", error.Message, StringComparison.Ordinal);
        Assert.False(PackManifestService.HasManifest(Directory()));
    }

    [Theory]
    [InlineData("javafml", "[47]", true)]
    [InlineData("lowcodefml", "[47]", true)]
    [InlineData("javafml", "[47,47.1)", true)]
    [InlineData("lowcodefml", "[47,47] || [47.2,48)", true)]
    [InlineData("javafml", "[47.3,48)", false)]
    public async Task ProvenForge47FmlPredicates_AreEvaluatedAgainstTheNativeExactMajor(string language, string range, bool compatible)
    {
        var metadata = Metadata(PackLoaderKind.Forge, "1.20.1", "[47,48)", range);
        Jar("native-api.jar", (metadata.Path, metadata.Content.Replace("javafml", language, StringComparison.Ordinal)));
        var catalog = new Catalog(["1.20.1"]).Add(PackLoaderKind.Forge, "1.20.1", ["47.3.7"], "47.3.7");
        if (compatible) Assert.True(await Service(catalog).EnsureAsync(Pack, default));
        else await Assert.ThrowsAsync<InvalidDataException>(() => Service(catalog).EnsureAsync(Pack, default));
        Assert.Equal(compatible, PackManifestService.HasManifest(Directory()));
    }

    [Theory]
    [InlineData("[48,49)")]
    [InlineData(">=48 <49")]
    [InlineData("48.x")]
    [InlineData("^48")]
    [InlineData("~48")]
    [InlineData("[47,48) || [48,49)")]
    public async Task WholeFmlMajorBranches_AreCheckedWithoutInventingAPatchApi(string range)
    {
        Jar("mod.jar", Metadata(PackLoaderKind.Forge, "1.20.2", "[48,49)", range));
        await Service(new Catalog(["1.20.2"]).Add(PackLoaderKind.Forge, "1.20.2", ["48.1.0"], "48.1.0")).EnsureAsync(Pack, default);
        Assert.Equal(PackLoaderKind.Forge, PackManifestService.Load(Directory()).Loader.Type);
    }

    [Fact]
    public async Task NeoFml4LowCodeLanguage_RejectsADifferentApiFamily()
    {
        var toml = Metadata(PackLoaderKind.NeoForge, "1.21.1", "[21,22)", "[1,2)");
        Jar("lowcode.jar", (toml.Path, toml.Content.Replace("javafml", "lowcodefml", StringComparison.Ordinal)));
        var catalog = new Catalog(["1.21.1"]).Add(PackLoaderKind.NeoForge, "1.21.1", ["21.1.259"], "4.0.42");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Service(catalog).EnsureAsync(Pack, default));
        Assert.Contains("загрузчика", error.Message, StringComparison.Ordinal);
        Assert.False(PackManifestService.HasManifest(Directory()));
    }

    [Theory]
    [InlineData(PackLoaderKind.Forge, "1.20.1", "47.3.7", "47.3.7", "javafml", "[47,48)")]
    [InlineData(PackLoaderKind.Forge, "1.20.1", "47.4.0", "47.4.0", "lowcodefml", "[47,48)")]
    [InlineData(PackLoaderKind.NeoForge, "26.3", "26.3.0.99", "12.0.12", "javafml", "[12.0.12]")]
    [InlineData(PackLoaderKind.NeoForge, "1.21.1", "21.1.259", "4.0.42", "lowcodefml", "[4.0.42]")]
    [InlineData(PackLoaderKind.NeoForge, "26.3", "26.3.0.99", "12.0.12", "lowcodefml", "[1,2)")]
    [InlineData(PackLoaderKind.NeoForge, "26.3", "26.3.0.99", "12.0.12", "lowcodefml", "[99]")]
    public async Task FmlLanguagePolicy_UsesNativePlatformSemantics(PackLoaderKind loader, string game, string release, string evidence,
        string language, string range)
    {
        var metadata = Metadata(loader, game, "*", range);
        Jar("mod.jar", (metadata.Path, metadata.Content.Replace("javafml", language, StringComparison.Ordinal)));
        await Service(new Catalog([game]).Add(loader, game, [release], evidence)).EnsureAsync(Pack, default);
        Assert.Equal(release, PackManifestService.Load(Directory()).Loader.Version);
    }

    [Fact]
    public async Task NeoJavaFml_UsesTheExactPublishedModuleVersion()
    {
        Jar("new-api.jar", Metadata(PackLoaderKind.NeoForge, "26.3", "*", "[12.0.13,)"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(new Catalog(["26.3"])
            .Add(PackLoaderKind.NeoForge, "26.3", ["26.3.0.99"], "12.0.12")).EnsureAsync(Pack, default));
        Assert.False(PackManifestService.HasManifest(Directory()));
    }

    [Fact]
    public async Task AnUnprovenOldNeoLowCodeApi_IsRefusedWithAnExplicitManifestOption()
    {
        var metadata = Metadata(PackLoaderKind.NeoForge, "1.21.1", "*", "[1,2)");
        Jar("old-lowcode.jar", (metadata.Path, metadata.Content.Replace("javafml", "lowcodefml", StringComparison.Ordinal)));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Service(new Catalog(["1.21.1"])
            .Add(PackLoaderKind.NeoForge, "1.21.1", ["21.1.259"], "3.0.0")).EnsureAsync(Pack, default));
        Assert.Contains("lowcodefml", error.Message, StringComparison.Ordinal);
        Assert.Contains("portable-pack.json", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuiltAndFabricMods_CompareTheirOwnLoaderAndProvidedApiVersions()
    {
        Jar("quilt.jar", Metadata(PackLoaderKind.Quilt, "1.21.1", ">=0.30 <0.31"));
        Jar("fabric.jar", Metadata(PackLoaderKind.Fabric, "1.21.1", ">=0.19 <0.20"));
        var catalog = new Catalog(["1.21.1"]).Add(PackLoaderKind.Quilt, "1.21.1", ["0.30.1"])
            .FabricApi("0.30.1", "0.19.3");
        await Service(catalog).EnsureAsync(Pack, default);
        Assert.Equal(PackLoaderKind.Quilt, PackManifestService.Load(Directory()).Loader.Type);
        Assert.Equal(1, catalog.FabricApiQueries);
        Assert.DoesNotContain(catalog.LoaderQueries, query => query.Loader == PackLoaderKind.Fabric);
    }

    [Theory]
    [InlineData(">=0.30", "0.19.3")]
    [InlineData(">=0.19", null)]
    public async Task QuiltCannotSubstituteItsVersionForAnUnprovenOrIncompatibleFabricApi(string range, string? api)
    {
        Jar("quilt.jar", Metadata(PackLoaderKind.Quilt, "1.21.1", ">=0.30"));
        Jar("fabric.jar", Metadata(PackLoaderKind.Fabric, "1.21.1", range));
        var catalog = new Catalog(["1.21.1"]).Add(PackLoaderKind.Quilt, "1.21.1", ["0.30.1"]).FabricApi("0.30.1", api);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(catalog).EnsureAsync(Pack, default));
        Assert.False(PackManifestService.HasManifest(Directory()));
    }

    [Fact]
    public async Task AQuiltCompatibilityAlias_DoesNotInventAQuiltCandidateForFabricOnlyMods()
    {
        Jar("fabric.jar", Metadata(PackLoaderKind.Fabric, "1.21.1", ">=0.19"));
        var catalog = new Catalog(["1.21.1"]).Add(PackLoaderKind.Fabric, "1.21.1", ["0.19.3"])
            .Add(PackLoaderKind.Quilt, "1.21.1", ["0.30.1"]).FabricApi("0.30.1", "0.19.3");
        await Service(catalog).EnsureAsync(Pack, default);
        Assert.Equal(PackLoaderKind.Fabric, PackManifestService.Load(Directory()).Loader.Type);
        Assert.Equal(0, catalog.FabricApiQueries);
    }

    [Fact]
    public async Task QuiltCompatibility_DoesNotRelaxTheFabricModsMinecraftConstraint()
    {
        Jar("quilt.jar", Metadata(PackLoaderKind.Quilt, "1.21.1", "*"));
        Jar("fabric.jar", Metadata(PackLoaderKind.Fabric, "1.20.1", "*"));
        var catalog = new Catalog(["1.21.1", "1.20.1"]).Add(PackLoaderKind.Quilt, "1.21.1", ["0.30.1"])
            .FabricApi("0.30.1", "0.19.3");
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(catalog).EnsureAsync(Pack, default));
        Assert.Equal(0, catalog.FabricApiQueries);
        Assert.False(PackManifestService.HasManifest(Directory()));
    }

    [Theory]
    [InlineData("0.19.3", true)]
    [InlineData("0.16.14", false)]
    public async Task QuiltsOwnFabricLoaderDependency_IsCheckedSeparatelyFromItsQuiltVersion(string alias, bool compatible)
    {
        Jar("quilt.jar", ("quilt.mod.json", """
            {"schema_version":1,"quilt_loader":{"id":"example","version":"1.0","depends":[
              {"id":"minecraft","versions":"1.21.1"},{"id":"quilt_loader","versions":">=0.30"},
              {"id":"fabricloader","versions":">=0.19 <0.20"}]}}
            """));
        var catalog = new Catalog(["1.21.1"]).Add(PackLoaderKind.Quilt, "1.21.1", ["0.30.1"]).FabricApi("0.30.1", alias);
        if (compatible) Assert.True(await Service(catalog).EnsureAsync(Pack, default));
        else await Assert.ThrowsAsync<InvalidDataException>(() => Service(catalog).EnsureAsync(Pack, default));
        Assert.Equal(compatible, PackManifestService.HasManifest(Directory()));
        Assert.Equal(1, catalog.FabricApiQueries);
    }

    [Fact]
    public async Task QuiltCompatibility_SelectsTheNewestReleaseWithinBothDeclaredApiRanges()
    {
        Jar("quilt.jar", Metadata(PackLoaderKind.Quilt, "1.21.1", ">=0.29 <0.31"));
        Jar("fabric.jar", Metadata(PackLoaderKind.Fabric, "1.21.1", ">=0.16 <0.17"));
        var catalog = new Catalog(["1.21.1"]).Add(PackLoaderKind.Quilt, "1.21.1", ["0.30.1", "0.29.3"])
            .FabricApi("0.30.1", "0.19.3").FabricApi("0.29.3", "0.16.14");
        await Service(catalog).EnsureAsync(Pack, default);
        Assert.Equal("0.29.3", PackManifestService.Load(Directory()).Loader.Version);
    }

    [Fact]
    public async Task ACompleteNativeQuiltAssignment_DoesNotRequireAnUnusedFabricAlias()
    {
        Jar("quilt.jar", Metadata(PackLoaderKind.Quilt, "1.21.1", ">=0.30"));
        Jar("multi.jar", Metadata(PackLoaderKind.Quilt, "1.21.1", ">=0.30"), Metadata(PackLoaderKind.Fabric, "1.21.1", ">=0.99"));
        var catalog = new Catalog(["1.21.1"]).Add(PackLoaderKind.Quilt, "1.21.1", ["0.30.1"]);
        await Service(catalog).EnsureAsync(Pack, default);
        Assert.Equal(PackLoaderKind.Quilt, PackManifestService.Load(Directory()).Loader.Type);
        Assert.Equal(0, catalog.FabricApiQueries);
    }

    [Theory]
    [InlineData("=1.20.1", ">=0.30")]
    [InlineData("1.21.1", ">=0.31")]
    public async Task QuiltNativeMetadata_CannotFallBackToFabricMetadataWhenItsRuntimeIsIncompatible(string nativeMinecraft, string nativeLoader)
    {
        Jar("quilt.jar", Metadata(PackLoaderKind.Quilt, "1.21.1", ">=0.30"));
        Jar("multi.jar", Metadata(PackLoaderKind.Quilt, nativeMinecraft, nativeLoader),
            Metadata(PackLoaderKind.Fabric, "1.21.1", ">=0.19 <0.20"));
        var catalog = new Catalog(["1.21.1"]).Add(PackLoaderKind.Quilt, "1.21.1", ["0.30.1"])
            .FabricApi("0.30.1", "0.19.3");

        await Assert.ThrowsAsync<InvalidDataException>(() => Service(catalog).EnsureAsync(Pack, default));

        Assert.False(PackManifestService.HasManifest(Directory()));
        Assert.Equal(0, catalog.FabricApiQueries);
    }

    [Fact]
    public async Task LatestStableGameAndLoader_MustSatisfyEveryModRange()
    {
        Jar("a.jar", Metadata(PackLoaderKind.Fabric, ">=1.21 <1.22", ">=0.16 <0.17"));
        Jar("b.jar", Metadata(PackLoaderKind.Fabric, "<=1.21.1", ">=0.16.4"));
        var catalog = new Catalog(["1.21.2", "1.21.1", "1.21"]).Add(PackLoaderKind.Fabric, "1.21.1", ["0.18.0", "0.16.14", "0.16.3"]);
        await Service(catalog).EnsureAsync(Pack, default);
        var descriptor = PackManifestService.Load(Directory());
        Assert.Equal("1.21.1", descriptor.MinecraftVersion);
        Assert.Equal("0.16.14", descriptor.Loader.Version);
    }

    [Fact]
    public async Task MultiLoaderLibrary_OffersAlternativesWithoutOverrulingTheActualMod()
    {
        Jar("actual.jar", Metadata(PackLoaderKind.Forge, "1.20.1", "[47,48)", "[47,48)"));
        Jar("library.jar", Metadata(PackLoaderKind.Forge, "1.20.1", "[47,48)", "[47,48)"), Metadata(PackLoaderKind.Fabric, "1.20.1", ">=0.14"));
        var catalog = new Catalog(["1.20.1"]).Add(PackLoaderKind.Forge, "1.20.1", ["47.3.0"], "47.3.0");
        await Service(catalog).EnsureAsync(Pack, default);
        Assert.Equal(PackLoaderKind.Forge, PackManifestService.Load(Directory()).Loader.Type);
    }

    [Fact]
    public async Task ContradictoryMinecraftRange_CannotBeOutvotedByEightOtherMods()
    {
        for (var i = 0; i < 8; i++) Jar("a" + i + ".jar", Metadata(PackLoaderKind.Fabric, "1.21.1", "*"));
        Jar("contradiction.jar", Metadata(PackLoaderKind.Fabric, "1.20.1", "*"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(new Catalog(["1.21.1", "1.20.1"])).EnsureAsync(Pack, default));
        Assert.False(PackManifestService.HasManifest(Directory()));
    }

    [Fact]
    public async Task UnknownAnnotationOnlyJar_IsNotSilentlyTreatedAsACompatibleLibrary()
    {
        Jar("fabric.jar", Metadata(PackLoaderKind.Fabric, "1.21.1", "*"));
        Jar("legacy-forge.jar", ("example/Legacy.class", "Lcpw/mods/fml/common/Mod;"));
        var catalog = new Catalog(["1.21.1"]).Add(PackLoaderKind.Fabric, "1.21.1", ["0.16.14"]);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Service(catalog).EnsureAsync(Pack, default));
        Assert.Contains("legacy-forge.jar", error.Message, StringComparison.Ordinal);
        Assert.Contains("portable-pack.json", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, catalog.Calls);
        Assert.False(PackManifestService.HasManifest(Directory()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedOrAmbiguousLoaderMetadata_RequiresAnExplicitChoice(bool ambiguous)
    {
        if (ambiguous) Jar("multi.jar", Metadata(PackLoaderKind.Forge, "1.20.1", "*"), Metadata(PackLoaderKind.Fabric, "1.20.1", "*"));
        else
        {
            Jar("forge.jar", Metadata(PackLoaderKind.Forge, "1.20.1", "*"));
            Jar("fabric.jar", Metadata(PackLoaderKind.Fabric, "1.20.1", "*"));
        }
        var catalog = new Catalog(["1.20.1"]).Add(PackLoaderKind.Forge, "1.20.1", ["47.3.0"])
            .Add(PackLoaderKind.Fabric, "1.20.1", ["0.16.14"]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(catalog).EnsureAsync(Pack, default));
        Assert.False(PackManifestService.HasManifest(Directory()));
    }

    [Fact]
    public async Task ADeclaredNeutralLibrary_DoesNotInventOrContradictThePlatform()
    {
        Jar("fabric.jar", Metadata(PackLoaderKind.Fabric, "1.21.1", "*"));
        Jar("library.jar", ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\nFMLModType: LIBRARY\r\n\r\n"));
        await Service(new Catalog(["1.21.1"]).Add(PackLoaderKind.Fabric, "1.21.1", ["0.16.14"])).EnsureAsync(Pack, default);
        Assert.Equal(PackLoaderKind.Fabric, PackManifestService.Load(Directory()).Loader.Type);
    }

    [Fact]
    public async Task LowCodeAlternative_DoesNotOverrideAFullyDeclaredFabricBranch()
    {
        var lowcode = Metadata(PackLoaderKind.NeoForge, "1.21.1", "[21,22)", "[1,2)");
        Jar("library.jar", Metadata(PackLoaderKind.Fabric, "1.21.1", "*"),
            (lowcode.Path, lowcode.Content.Replace("javafml", "lowcodefml", StringComparison.Ordinal)));
        Jar("actual.jar", Metadata(PackLoaderKind.Fabric, "1.21.1", "*"));
        await Service(new Catalog(["1.21.1"]).Add(PackLoaderKind.Fabric, "1.21.1", ["0.16.14"])).EnsureAsync(Pack, default);
        Assert.Equal(PackLoaderKind.Fabric, PackManifestService.Load(Directory()).Loader.Type);
    }

    [Fact]
    public async Task AnExactGameWithoutSteamTransport_IsReportedWithoutEscapingItsConstraint()
    {
        Jar("mod.jar", Metadata(PackLoaderKind.Fabric, "99.0", "*"));
        var catalog = new Catalog(["99.0", "1.21.1"]).Add(PackLoaderKind.Fabric, "99.0", ["0.16.14"])
            .Add(PackLoaderKind.Fabric, "1.21.1", ["0.16.14"]);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Service(catalog).EnsureAsync(Pack, default));
        Assert.Contains("99.0", error.Message, StringComparison.Ordinal);
        Assert.Contains("Steam", error.Message, StringComparison.Ordinal);
        Assert.False(PackManifestService.HasManifest(Directory()));
    }

    [Fact]
    public async Task BroadRanges_SelectTheLatestDeclaredGameWithBothLoaderAndSteamSupport()
    {
        Jar("mod.jar", Metadata(PackLoaderKind.Forge, "[1.20.1,1.21)", "*"));
        var catalog = new Catalog(["1.20.6", "1.20.2", "1.20.1"]).Add(PackLoaderKind.Forge, "1.20.6", ["50.1.0"])
            .Add(PackLoaderKind.Forge, "1.20.2", ["48.1.0"]).Add(PackLoaderKind.Forge, "1.20.1", ["47.3.0"]);
        await Service(catalog).EnsureAsync(Pack, default);
        Assert.Equal("1.20.2", PackManifestService.Load(Directory()).MinecraftVersion);
        Assert.DoesNotContain(catalog.LoaderQueries, query => query.Game == "1.20.6");
    }

    [Fact]
    public async Task MinecraftEvidenceFromAnotherLoaderBranch_CannotAuthorizeAnUnconstrainedRuntime()
    {
        Jar("multi.jar", Metadata(PackLoaderKind.Fabric, "1.21.1", "*"),
            ("META-INF/neoforge.mods.toml", "modLoader=\"javafml\"\n[[mods]]\nmodId=\"example\"\n[[dependencies.example]]\nmodId=\"neoforge\"\nversionRange=\"[26,)\"\nmandatory=true\n"));
        var catalog = new Catalog(["26.3", "1.21.1"]).Add(PackLoaderKind.NeoForge, "26.3", ["26.3.0.99"])
            .Add(PackLoaderKind.Fabric, "1.21.1", ["0.16.14"]);
        await Service(catalog).EnsureAsync(Pack, default);
        Assert.Equal(PackLoaderKind.Fabric, PackManifestService.Load(Directory()).Loader.Type);
        Assert.Equal("1.21.1", PackManifestService.Load(Directory()).MinecraftVersion);
    }

    [Fact]
    public async Task ModMutationDuringResolution_DoesNotPublishAStaleDescriptor()
    {
        Jar("mod.jar", Metadata(PackLoaderKind.Fabric, "1.21.1", "*"));
        var catalog = new Catalog(["1.21.1"]).Add(PackLoaderKind.Fabric, "1.21.1", ["0.16.14"]);
        catalog.OnVersions = () => Jar("added.jar", Metadata(PackLoaderKind.Fabric, "1.21.1", "*"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(catalog).EnsureAsync(Pack, default));
        Assert.False(PackManifestService.HasManifest(Directory()));
        Assert.Empty(System.IO.Directory.EnumerateFiles(Directory(), ".portable-auto-*.tmp"));
    }

    [Fact]
    public async Task AnExplicitManifestAppearingDuringResolution_IsNeverOverwritten()
    {
        Jar("mod.jar", Metadata(PackLoaderKind.Fabric, "1.21.1", "*"));
        var catalog = new Catalog(["1.21.1"]).Add(PackLoaderKind.Fabric, "1.21.1", ["0.16.14"]);
        catalog.OnVersions = () => File.WriteAllText(Path.Combine(Directory(), "portable-pack.json"), "personal explicit descriptor");
        Assert.False(await Service(catalog).EnsureAsync(Pack, default));
        Assert.Equal("personal explicit descriptor", File.ReadAllText(Path.Combine(Directory(), "portable-pack.json")));
    }

    private PackAutoManifestService Service(Catalog catalog) => new(_paths, new Logger(_paths.LogFile), catalog);
    private string Directory()
    {
        _paths.Ensure();
        var pack = _paths.CombineUnderPacks(Pack);
        System.IO.Directory.CreateDirectory(Path.Combine(pack, "mods"));
        return pack;
    }
    private byte[] Jar(string name, params (string Path, string Content)[] metadata)
    {
        var path = Path.Combine(Directory(), "mods", name);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (entry, content) in metadata)
            { using var output = archive.CreateEntry(entry).Open(); output.Write(Encoding.UTF8.GetBytes(content)); }
        return File.ReadAllBytes(path);
    }
    private static (string Path, string Content) Metadata(PackLoaderKind loader, string minecraft, string platform, string? fml = null)
    {
        if (loader == PackLoaderKind.Fabric)
            return ("fabric.mod.json", System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, id = "example", version = "1.0",
                depends = new Dictionary<string, string> {{"minecraft", minecraft}, {"fabricloader", platform}} }));
        if (loader == PackLoaderKind.Quilt)
            return ("quilt.mod.json", System.Text.Json.JsonSerializer.Serialize(new { schema_version = 1, quilt_loader = new
            { id = "example", version = "1.0", depends = new[] {new {id = "minecraft", versions = minecraft}, new {id = "quilt_loader", versions = platform}} } }));
        var family = loader == PackLoaderKind.Forge ? "forge" : "neoforge";
        var range = minecraft.StartsWith('[') || minecraft.StartsWith('(') ? minecraft : "[" + minecraft + "]";
        return (loader == PackLoaderKind.Forge ? "META-INF/mods.toml" : "META-INF/neoforge.mods.toml", "modLoader=\"javafml\"\n" +
            (fml is null ? "" : "loaderVersion=\"" + fml + "\"\n") +
            "[[mods]]\nmodId=\"example\"\nversion=\"1\"\n[[dependencies.example]]\nmodId=\"minecraft\"\nversionRange=\"" + range +
            "\"\nmandatory=true\n[[dependencies.example]]\nmodId=\"" + family + "\"\nmandatory=true\nversionRange=\"" + platform + "\"\n");
    }

    private sealed class Catalog(string[] minecraft) : IAutoRuntimeCatalog
    {
        private readonly Dictionary<(PackLoaderKind, string), (string[] Versions, string? Fml)> _loaders = [];
        private readonly Dictionary<string, string?> _fabricApis = [];
        public int Calls { get; private set; }
        public int FabricApiQueries { get; private set; }
        public List<(PackLoaderKind Loader, string Game)> LoaderQueries { get; } = [];
        public Action? OnVersions { get; set; }
        public Catalog Add(PackLoaderKind loader, string game, string[] versions, string? fml = null)
        { _loaders[(loader, game)] = (versions, fml); return this; }
        public Catalog FabricApi(string quilt, string? api) { _fabricApis[quilt] = api; return this; }
        public Task<IReadOnlyList<string>> MinecraftVersionsAsync(CancellationToken token)
        { Calls++; OnVersions?.Invoke(); return Task.FromResult<IReadOnlyList<string>>(minecraft); }
        public Task<IReadOnlyList<string>> LoaderVersionsAsync(PackLoaderKind loader, string game, CancellationToken token)
        { Calls++; LoaderQueries.Add((loader, game)); return Task.FromResult<IReadOnlyList<string>>(_loaders.TryGetValue((loader, game), out var value) ? value.Versions : []); }
        public Task<string?> FmlVersionAsync(PackLoaderKind loader, string game, string version, CancellationToken token)
        { Calls++; return Task.FromResult(_loaders.TryGetValue((loader, game), out var value) ? value.Fml : null); }
        public Task<string?> FabricApiVersionAsync(string game, string version, CancellationToken token)
        { Calls++; FabricApiQueries++; return Task.FromResult(_fabricApis.GetValueOrDefault(version)); }
    }
}
