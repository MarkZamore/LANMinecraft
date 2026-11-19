using System.IO;
using System.IO.Compression;
using System.Text;
using Minecraft;

namespace Minecraft.Tests;

public sealed class WindowTitleMappingServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "window-title-mapping-" + Guid.NewGuid().ToString("N"));
    private static readonly WindowTitleSettings Enabled = new(true, "Fixture", "config-sha");

    [Fact]
    public void ExplicitDisabledControl_PerformsNoRuntimeDiscoveryAndAddsNoTarget()
    {
        var configuration = WindowTitleMappingService.Build(Runtime("26.2", PackLoaderKind.NeoForge, "26.2.0.84"), WindowTitleSettings.Disabled);
        Assert.Equal("false", configuration.Properties["windowTitleEnabled"]);
        Assert.Empty(configuration.Targets);
        Assert.DoesNotContain("windowTitleText", configuration.Properties.Keys);
    }

    [Fact]
    public void Named26Runtime_UsesExactPatchedLoaderOutputWithoutMappings()
    {
        var runtime = Runtime("26.2", PackLoaderKind.NeoForge, "26.2.0.84");
        var actual = PatchedJar(runtime, "26.2.0.84");
        WriteJar(actual, Class("createTitle"));
        WriteJar(runtime.ClientJarPath, Class("createTitle"));
        WriteJar(PatchedJar(runtime, "26.2.0.85"), Class("createTitle"));
        WriteJar(NeoForgeClientJar(runtime, "26.2.0.84"), Class("m_91270_"));
        var configuration = WindowTitleMappingService.Build(runtime, Enabled);
        Assert.Equal(new IdentityAdapterTarget(actual, WindowTitleMappingService.MinecraftClass), Assert.Single(configuration.Targets));
        Assert.Equal("createTitle", configuration.Properties["createTitleMethods"]);
        Assert.Equal(WindowTitleMappingService.TitleDescriptor, configuration.Properties["createTitleDescriptors"]);
        Assert.Equal(Enabled.Text, configuration.Properties["windowTitleText"]);
        Assert.Equal("true", configuration.Properties["windowTitleEnabled"]);
    }

    [Fact]
    public void Forge1201_PrefersExactPatchedLoaderClientOverSrgIntermediate()
    {
        var runtime = Runtime("1.20.1", PackLoaderKind.Forge, "47.3.7");
        var actual = ForgeClientJar(runtime, "47.3.7");
        WriteJar(actual, Class("m_91270_"));
        WriteJar(ForgeClientJar(runtime, "47.4.0"), Class("createTitle"));
        WriteJar(SrgJar(runtime, "1.20.1"), Class("createTitle"));
        var configuration = WindowTitleMappingService.Build(runtime, Enabled);
        Assert.Equal(actual, Assert.Single(configuration.Targets).JarPath);
        Assert.Equal("m_91270_", configuration.Properties["createTitleMethods"]);
    }

    [Fact]
    public void AnotherForgeLoaderVersion_IsNeverUsedAsFallback()
    {
        var runtime = Runtime("1.20.1", PackLoaderKind.Forge, "47.3.7");
        WriteJar(ForgeClientJar(runtime, "47.4.0"), Class("m_91270_"));
        Assert.Throws<NotSupportedException>(() => WindowTitleMappingService.Build(runtime, Enabled));
    }

    [Fact]
    public void OlderNeoForge_UsesExactPatchedLoaderClient()
    {
        var runtime = Runtime("1.21.1", PackLoaderKind.NeoForge, "21.1.251");
        var actual = NeoForgeClientJar(runtime, "21.1.251");
        WriteJar(actual, Class("createTitle"));
        WriteJar(NeoForgeClientJar(runtime, "21.1.259"), Class("m_91270_"));
        WriteJar(runtime.ClientJarPath, Class("m_91270_"));
        var configuration = WindowTitleMappingService.Build(runtime, Enabled);
        Assert.Equal(actual, Assert.Single(configuration.Targets).JarPath);
        Assert.Equal("createTitle", configuration.Properties["createTitleMethods"]);
    }

    [Fact]
    public void AnotherOlderNeoForgeVersion_IsNeverUsedAsFallback()
    {
        var runtime = Runtime("1.21.1", PackLoaderKind.NeoForge, "21.1.251");
        WriteJar(NeoForgeClientJar(runtime, "21.1.259"), Class("createTitle"));
        Assert.Throws<NotSupportedException>(() => WindowTitleMappingService.Build(runtime, Enabled));
    }

    [Fact]
    public void Forge1201_UsesOnlyTheMatchingMinecraftSrgClient()
    {
        var runtime = Runtime("1.20.1", PackLoaderKind.Forge, "47.3.7");
        var actual = SrgJar(runtime, "1.20.1");
        WriteJar(actual, Class("m_91270_"));
        WriteJar(SrgJar(runtime, "1.21.1"), Class("createTitle"));
        WriteJar(runtime.ClientJarPath, Class("createTitle"), "obfuscated/Minecraft");
        var configuration = WindowTitleMappingService.Build(runtime, Enabled);
        Assert.Equal(actual, Assert.Single(configuration.Targets).JarPath);
        Assert.Equal("m_91270_", configuration.Properties["createTitleMethods"]);
    }

    [Fact]
    public void AnotherMinecraftInTheSharedStore_IsNeverUsedAsFallback()
    {
        var runtime = Runtime("1.20.1", PackLoaderKind.Forge, "47.3.7");
        WriteJar(SrgJar(runtime, "1.21.1"), Class("createTitle"));
        WriteJar(runtime.ClientJarPath, Class("createTitle"), "obfuscated/Minecraft");
        Assert.Throws<NotSupportedException>(() => WindowTitleMappingService.Build(runtime, Enabled));
    }

    [Fact]
    public void AnotherNeoForgePatch_IsNeverUsedAsFallback()
    {
        var runtime = Runtime("26.2", PackLoaderKind.NeoForge, "26.2.0.84");
        WriteJar(PatchedJar(runtime, "26.2.0.85"), Class("createTitle"));
        WriteJar(runtime.ClientJarPath, Class("createTitle"), "obfuscated/Minecraft");
        Assert.Throws<NotSupportedException>(() => WindowTitleMappingService.Build(runtime, Enabled));
    }

    [Theory]
    [InlineData("1.21.1", PackLoaderKind.Fabric, "fgo", "bd", "net/minecraft/class_310", "method_24287", false)]
    [InlineData("1.18.2", PackLoaderKind.Quilt, "fixture/OlderClient", "x", "net/minecraft/class_999", "method_999", false)]
    [InlineData("1.21.1", PackLoaderKind.Fabric, "fgo", "bd", "net/minecraft/class_310", "method_24287", true)]
    public void IntermediaryRuntime_UsesItsMappedFactoryAndExactNativePreflightTarget(string minecraft,
        PackLoaderKind loader, string sourceClass, string sourceMethod, string runtimeClass, string runtimeMethod, bool remapped)
    {
        var runtime = Runtime(minecraft, loader, "fixture-loader");
        Mappings(runtime, sourceClass, sourceMethod, runtimeClass, runtimeMethod);
        var actualClass = remapped ? runtimeClass : sourceClass;
        WriteJar(runtime.ClientJarPath, Class(remapped ? runtimeMethod : sourceMethod, className: actualClass), actualClass);

        var configuration = WindowTitleMappingService.Build(runtime, Enabled);

        Assert.Equal("true", configuration.Properties["windowTitleEnabled"]);
        Assert.Equal(Enabled.Text, configuration.Properties["windowTitleText"]);
        Assert.Equal(runtimeClass + "," + sourceClass, configuration.Properties["windowTitleClasses"]);
        Assert.Equal(runtimeMethod + "," + sourceMethod, configuration.Properties["createTitleMethods"]);
        Assert.Equal(WindowTitleMappingService.TitleDescriptor, configuration.Properties["createTitleDescriptors"]);
        Assert.Equal(new IdentityAdapterTarget(runtime.ClientJarPath, actualClass), Assert.Single(configuration.Targets));
    }

    [Theory]
    [InlineData("()V", 2, true)]
    [InlineData("()Ljava/lang/String;", 10, true)]
    [InlineData("()Ljava/lang/String;", 2, false)]
    public void MappedFactory_WrongDescriptorStaticOrCodelessSourceIsRejected(string descriptor, int flags, bool code)
    {
        var runtime = Runtime("1.21.1", PackLoaderKind.Fabric, "fixture-loader");
        Mappings(runtime, "fgo", "bd", "net/minecraft/class_310", "method_24287");
        WriteJar(runtime.ClientJarPath, Class("bd", descriptor, (ushort)flags, code, "fgo"), "fgo");
        Assert.Throws<NotSupportedException>(() => WindowTitleMappingService.Build(runtime, Enabled));
    }

    [Fact]
    public void MappedFactory_ClassHeaderMustMatchTheExactSourceEntry()
    {
        var runtime = Runtime("1.21.1", PackLoaderKind.Fabric, "fixture-loader");
        Mappings(runtime, "fgo", "bd", "net/minecraft/class_310", "method_24287");
        WriteJar(runtime.ClientJarPath, Class("bd", className: "another/Client"), "fgo");
        Assert.Throws<InvalidDataException>(() => WindowTitleMappingService.Build(runtime, Enabled));
    }

    [Fact]
    public void AnotherMinecraftsIntermediaryMappings_AreNeverUsedForTheTitle()
    {
        var runtime = Runtime("1.21.1", PackLoaderKind.Fabric, "fixture-loader");
        Mappings(Runtime("1.18.2", PackLoaderKind.Fabric, "fixture-loader"),
            "fgo", "bd", "net/minecraft/class_310", "method_24287");
        WriteJar(runtime.ClientJarPath, Class("bd", className: "fgo"), "fgo");
        Assert.Throws<NotSupportedException>(() => WindowTitleMappingService.Build(runtime, Enabled));
    }

    [Theory]
    [InlineData("()V", 2, true)]
    [InlineData("()Ljava/lang/String;", 10, true)]
    [InlineData("()Ljava/lang/String;", 2, false)]
    public void WrongDescriptorStaticOrCodelessMethod_IsRejected(string descriptor, int flags, bool code)
    {
        var runtime = Runtime("26.2", PackLoaderKind.NeoForge, "26.2.0.84");
        WriteJar(PatchedJar(runtime, "26.2.0.84"), Class("createTitle", descriptor, (ushort)flags, code));
        Assert.Throws<NotSupportedException>(() => WindowTitleMappingService.Build(runtime, Enabled));
    }

    [Fact]
    public void MalformedClass_IsRejectedBeforePreflight()
    {
        var runtime = Runtime("26.2", PackLoaderKind.NeoForge, "26.2.0.84");
        WriteJar(PatchedJar(runtime, "26.2.0.84"), [0xCA, 0xFE, 0xBA]);
        Assert.Throws<InvalidDataException>(() => WindowTitleMappingService.Build(runtime, Enabled));
    }

    private PreparedRuntime Runtime(string minecraft, PackLoaderKind loader, string version)
    {
        var shared = Path.Combine(_root, "Shared");
        return new PreparedRuntime(Path.Combine(_root, "Runtime"), "selected-profile", "java.exe",
            Path.Combine(shared, "versions", minecraft, minecraft + ".jar"),
            new PackRuntimeDescriptor(1, minecraft, new PackLoaderDescriptor(loader, version), "", "descriptor"))
            { LibrariesRoot = Path.Combine(shared, "libraries") };
    }
    private static string PatchedJar(PreparedRuntime runtime, string loaderVersion) => Path.Combine(runtime.LibrariesRoot,
        "net", "neoforged", "minecraft-client-patched", loaderVersion, $"minecraft-client-patched-{loaderVersion}.jar");
    private static string ForgeClientJar(PreparedRuntime runtime, string forgeVersion) => Path.Combine(runtime.LibrariesRoot,
        "net", "minecraftforge", "forge", runtime.Descriptor.MinecraftVersion + "-" + forgeVersion,
        $"forge-{runtime.Descriptor.MinecraftVersion}-{forgeVersion}-client.jar");
    private static string NeoForgeClientJar(PreparedRuntime runtime, string neoForgeVersion) => Path.Combine(runtime.LibrariesRoot,
        "net", "neoforged", "neoforge", neoForgeVersion, $"neoforge-{neoForgeVersion}-client.jar");
    private static string SrgJar(PreparedRuntime runtime, string minecraft) => Path.Combine(runtime.LibrariesRoot,
        "net", "minecraft", "client", minecraft + "-fixture", $"client-{minecraft}-fixture-srg.jar");
    private static void WriteJar(string path, byte[] bytes, string className = WindowTitleMappingService.MinecraftClass)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var stream = archive.CreateEntry(className + ".class").Open();
        stream.Write(bytes);
    }

    private static void Mappings(PreparedRuntime runtime, string sourceClass, string sourceMethod,
        string runtimeClass, string runtimeMethod)
    {
        var minecraft = runtime.Descriptor.MinecraftVersion;
        var path = Path.Combine(runtime.LibrariesRoot, "net", "minecraft", "client", minecraft + "-fixture", "client-mappings.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"net.minecraft.client.Minecraft -> {sourceClass.Replace('/', '.')}:\n" +
            $"    java.lang.String createTitle() -> {sourceMethod}\n");
        path = Path.Combine(runtime.LibrariesRoot, "net", "fabricmc", "intermediary", minecraft, $"intermediary-{minecraft}.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("mappings/mappings.tiny").Open());
        writer.Write($"v1\tofficial\tintermediary\nCLASS\t{sourceClass}\t{runtimeClass}\n" +
            $"METHOD\t{sourceClass}\t{WindowTitleMappingService.TitleDescriptor}\t{sourceMethod}\t{runtimeMethod}\n");
    }

    // A real bounded class-file method table, with an executable String-returning body.
    // No javac or game process is necessary for these discovery/negative tests.
    private static byte[] Class(string name, string descriptor = WindowTitleMappingService.TitleDescriptor,
        ushort flags = 2, bool code = true, string className = WindowTitleMappingService.MinecraftClass)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        void U2(int value) { writer.Write((byte)(value >> 8)); writer.Write((byte)value); }
        void U4(uint value) { writer.Write((byte)(value >> 24)); writer.Write((byte)(value >> 16)); writer.Write((byte)(value >> 8)); writer.Write((byte)value); }
        void Utf8(string value) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write((byte)1); U2(bytes.Length); writer.Write(bytes); }
        U4(0xCAFEBABE); U2(0); U2(69); U2(8);
        Utf8(className); // 1
        writer.Write((byte)7); U2(1); // 2: this class
        Utf8("java/lang/Object"); // 3
        writer.Write((byte)7); U2(3); // 4: superclass
        Utf8(name); Utf8(descriptor); Utf8("Code"); // 5, 6, 7
        U2(0x0021); U2(2); U2(4); U2(0); U2(0); U2(1);
        U2(flags); U2(5); U2(6); U2(code ? 1 : 0);
        if (code)
        {
            U2(7); U4(14); U2(1); U2(1); U4(2);
            writer.Write((byte)1); writer.Write((byte)0xB0); // aconst_null, areturn
            U2(0); U2(0);
        }
        U2(0);
        return stream.ToArray();
    }
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
