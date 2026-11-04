using System.IO;
using System.IO.Compression;
using Minecraft;

namespace Minecraft.Tests;

public sealed class IdentityAdapterPlayerInfoSkinTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "player-info-skins-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(PackLoaderKind.Fabric)]
    [InlineData(PackLoaderKind.Quilt)]
    public void LoginFallback_RetainsIndependentlyMappedPlayerInfoSkins(PackLoaderKind loader)
    {
        var (service, runtime, game) = Fixture(loader);
        var configuration = service.Build(runtime, game);
        Assert.Equal("false", configuration.Properties["identityHooksEnabled"]);
        AssertSkinFamily(configuration, runtime.ClientJarPath);
        Assert.DoesNotContain(configuration.Targets, target => target.ClassName.Contains("ServerLogin", StringComparison.Ordinal));
    }

    [Fact]
    public void PreflightFallback_ReloadsOptionalMappingsRatherThanDiscardingPlayerInfoSkins()
    {
        var (service, runtime, game) = Fixture(PackLoaderKind.Fabric);
        var configuration = service.BuildSkins(runtime, game, "unrelated login bytecode refused");
        Assert.Equal("false", configuration.Properties["identityHooksEnabled"]);
        AssertSkinFamily(configuration, runtime.ClientJarPath);
    }

    [Theory]
    [InlineData("lookup")]
    [InlineData("selector")]
    [InlineData("textureUrl")]
    [InlineData("secure")]
    [InlineData("skin-class")]
    [InlineData("selector-return")]
    public void IncompleteNativeSkinFamily_LeavesAuthlibEnabledWithoutGuessingAliases(string missing)
    {
        var (service, runtime, game) = Fixture(PackLoaderKind.Fabric, missing);
        var configuration = service.Build(runtime, game);
        Assert.Equal("false", configuration.Properties["playerInfoSkinEnabled"]);
        Assert.Contains(configuration.Targets, target => target.ClassName == "com/mojang/authlib/GameProfile");
        Assert.Contains(configuration.Targets, target => target.ClassName == "com/mojang/authlib/yggdrasil/YggdrasilMinecraftSessionService");
        Assert.DoesNotContain(configuration.Targets, target => target.ClassName is "fzq" or "net/minecraft/class_640");
    }

    [Fact]
    public void AbsentActualPlayerInfoClass_DisablesOnlyTheOptionalSkinSelectionFamily()
    {
        var (service, runtime, game) = Fixture(PackLoaderKind.Fabric, omitPlayerInfo: true);
        var configuration = service.Build(runtime, game);
        Assert.Equal("false", configuration.Properties["playerInfoSkinEnabled"]);
        Assert.Equal("getTextures,getPackedTextures", configuration.Properties["skinReaderMethods"]);
        Assert.DoesNotContain(configuration.Targets, target => target.ClassName == "fzq");
    }

    private static void AssertSkinFamily(IdentityAdapterConfiguration configuration, string source)
    {
        var properties = configuration.Properties;
        Assert.Equal("true", properties["playerInfoSkinEnabled"]);
        Assert.Equal("net/minecraft/class_640,fzq", properties["playerInfoClasses"]);
        Assert.Equal("net/minecraft/class_8685,grl", properties["playerSkinClasses"]);
        Assert.Equal("method_52803,a", properties["skinLookupMethods"]);
        Assert.Equal("(Lcom/mojang/authlib/GameProfile;)Ljava/util/function/Supplier;", properties["skinLookupDescriptors"]);
        Assert.Equal("method_52806,a", properties["skinSelectionMethods"]);
        Assert.Equal("(Ljava/util/concurrent/CompletableFuture;Lnet/minecraft/class_8685;Z)Lnet/minecraft/class_8685;," +
            "(Ljava/util/concurrent/CompletableFuture;Lgrl;Z)Lgrl;", properties["skinSelectionDescriptors"]);
        Assert.Equal("comp_1911,b", properties["skinTextureUrlMethods"]);
        Assert.Equal("comp_1630,f", properties["skinSecureMethods"]);
        Assert.Contains(new IdentityAdapterTarget(source, "fzq"), configuration.Targets);
        Assert.Contains(new IdentityAdapterTarget(source, "grl"), configuration.PreflightSources);
        Assert.DoesNotContain(configuration.Targets, target => target.ClassName == "grl");
    }

    private (IdentityAdapterMappingService Service, PreparedRuntime Runtime, string Game) Fixture(
        PackLoaderKind loader, string? missing = null, bool omitPlayerInfo = false)
    {
        var runtimeRoot = Path.Combine(_root, "runtime");
        var libraries = Path.Combine(runtimeRoot, "libraries");
        var mappingDirectory = Path.Combine(libraries, "net", "minecraft", "client", "1.21.1-fixture");
        Directory.CreateDirectory(mappingDirectory);
        var lines = new List<string> { "net.minecraft.client.multiplayer.PlayerInfo -> fzq:" };
        if (missing != "lookup") lines.Add("    java.util.function.Supplier createSkinLookup(com.mojang.authlib.GameProfile) -> a");
        if (missing != "selector") lines.Add("    " + (missing == "selector-return" ? "java.lang.String" : "net.minecraft.client.resources.PlayerSkin") +
            " lambda$createSkinLookup$2(java.util.concurrent.CompletableFuture,net.minecraft.client.resources.PlayerSkin,boolean) -> a");
        if (missing != "skin-class")
        {
            lines.Add("net.minecraft.client.resources.PlayerSkin -> grl:");
            if (missing != "textureUrl") lines.Add("    java.lang.String textureUrl() -> b");
            if (missing != "secure") lines.Add("    boolean secure() -> f");
        }
        File.WriteAllLines(Path.Combine(mappingDirectory, "client-mappings.txt"), lines);
        var intermediary = Path.Combine(libraries, "net", "fabricmc", "intermediary", "1.21.1", "intermediary-1.21.1.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(intermediary)!);
        using (var archive = ZipFile.Open(intermediary, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("mappings/mappings.tiny").Open()))
            writer.Write("v1\tofficial\tintermediary\nCLASS\tfzq\tnet/minecraft/class_640\nCLASS\tgrl\tnet/minecraft/class_8685\n" +
                "METHOD\tfzq\t(Lcom/mojang/authlib/GameProfile;)Ljava/util/function/Supplier;\ta\tmethod_52803\n" +
                "METHOD\tfzq\t(Ljava/util/concurrent/CompletableFuture;Lgrl;Z)Lgrl;\ta\tmethod_52806\n" +
                "METHOD\tgrl\t()Ljava/lang/String;\tb\tcomp_1911\nMETHOD\tgrl\t()Z\tf\tcomp_1630\n");
        var clientJar = Path.Combine(runtimeRoot, "client.jar");
        using (var archive = ZipFile.Open(clientJar, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "grl", "com/mojang/authlib/GameProfile", "com/mojang/authlib/yggdrasil/YggdrasilMinecraftSessionService" })
                archive.CreateEntry(name + ".class");
            if (!omitPlayerInfo) archive.CreateEntry("fzq.class");
        }
        var game = Path.Combine(_root, "game"); Directory.CreateDirectory(game);
        var runtime = new PreparedRuntime(runtimeRoot, "fixture-profile", "java.exe", clientJar,
            new PackRuntimeDescriptor(1, "1.21.1", new PackLoaderDescriptor(loader, "fixture"), "client.jar", "fixture"));
        return (new IdentityAdapterMappingService(new AppPaths(_root)), runtime, game);
    }

    public void Dispose() => TempTree.Delete(_root);
}
