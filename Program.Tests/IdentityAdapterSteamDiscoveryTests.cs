using System.Reflection;

namespace Minecraft.Tests;

public sealed class IdentityAdapterSteamDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "steam-discovery-mappings-" + Guid.NewGuid().ToString("N"));
    private const BindingFlags Hidden = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    // Actual Forge 1.20.1 classes/member shapes, with the obfuscated names
    // deliberately different so both namespaces must be carried into Java.
    private const string Mappings = """
        tsrg2 obf mojmap
        exn net/minecraft/client/gui/screens/multiplayer/JoinMultiplayerScreen
        	b ()V init
        	ax_ ()V removed
        	a (Lffd;)V join
        	v lanServerList
        ffd net/minecraft/client/multiplayer/ServerData
        	b ip
        fyq net/minecraft/client/server/LanServer
        	b ()Ljava/lang/String; getAddress
        fyr$b net/minecraft/client/server/LanServerDetection$LanServerList
        	a servers
        	b isDirty
        """;

    [Fact]
    public void Forge1201Discovery_UsesBothRuntimeNamesAndTheExactJoinDescriptor()
    {
        var properties = Build(Mappings, out var target);
        Assert.NotNull(target);
        Assert.Equal("true", properties["steamDiscoveryEnabled"]);
        Assert.Equal("net/minecraft/client/gui/screens/multiplayer/JoinMultiplayerScreen,exn", properties["steamDiscoveryScreenClasses"]);
        Assert.Equal("join,a", properties["steamDiscoveryJoinMethods"]);
        Assert.Equal("(Lnet/minecraft/client/multiplayer/ServerData;)V,(Lffd;)V", properties["steamDiscoveryJoinDescriptors"]);
        Assert.Equal("net.minecraft.client.server.LanServer,fyq", properties["steamDiscoveryLanServerClasses"]);
        Assert.Equal("1.20.1", properties["steamDiscoveryMinecraftVersion"]);
        Assert.Equal("0.3.4", properties["steamDiscoveryTransportVersion"]);
    }

    [Theory]
    [InlineData("\tv lanServerList")]
    [InlineData("\tb isDirty")]
    [InlineData("\ta (Lffd;)V join")]
    public void MissingScreenMember_DisablesDiscoveryWithoutDisablingOtherHooks(string missing)
    {
        var properties = Build(Mappings.Replace(missing, "", StringComparison.Ordinal), out var target);
        Assert.Null(target);
        Assert.Equal("false", properties["steamDiscoveryEnabled"]);
        Assert.Equal("net/minecraft/client/gui/screens/multiplayer/JoinMultiplayerScreen", properties["steamDiscoveryScreenClasses"]);
    }

    [Theory]
    [InlineData(PackLoaderKind.Forge, "1.20.1", "0.3.4")]
    [InlineData(PackLoaderKind.NeoForge, "1.21.1", "0.3.4")]
    public void DiscoveryVersion_FollowsTheReviewedPlatformTransport(PackLoaderKind loader, string minecraft, string version)
    {
        var properties = Build(Mappings, out var target, loader, minecraft);
        Assert.NotNull(target);
        Assert.Equal(version, properties["steamDiscoveryTransportVersion"]);
    }

    private Dictionary<string, string> Build(string mappings, out object? target,
        PackLoaderKind loader = PackLoaderKind.Forge, string minecraft = "1.20.1")
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "mappings.txt");
        File.WriteAllText(path, mappings);
        var type = typeof(IdentityAdapterMappingService).GetNestedType("RuntimeNames", Hidden)!;
        var names = type.GetMethod("Read", Hidden)!.Invoke(null, [path, null]);
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        var descriptor = new PackRuntimeDescriptor(1, minecraft, new PackLoaderDescriptor(loader, "fixture"), "client.jar", "fixture");
        target = typeof(IdentityAdapterMappingService).GetMethod("AddSteamDiscoveryProperties", Hidden)!
            .Invoke(null, [names, properties, descriptor]);
        return properties;
    }

    public void Dispose() => TempTree.Delete(_root);
}
