using System.Reflection;

namespace Minecraft.Tests;

public sealed class IdentityAdapterSteamDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "steam-discovery-mappings-" + Guid.NewGuid().ToString("N"));
    private const BindingFlags Hidden = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    // Actual Forge 1.20.1 classes/member shapes, with the obfuscated names
    // deliberately different so both namespaces must be carried into Java.
    private static readonly string Mappings = """
        tsrg2 obf mojmap
        exn net/minecraft/client/gui/screens/multiplayer/JoinMultiplayerScreen
        \tb ()V init
        \tax_ ()V removed
        \ta (Lffd;)V join
        \tv lanServerList
        ffd net/minecraft/client/multiplayer/ServerData
        \tb ip
        fyq net/minecraft/client/server/LanServer
        \tb ()Ljava/lang/String; getAddress
        fyr$b net/minecraft/client/server/LanServerDetection$LanServerList
        \ta servers
        \tb isDirty
        \tc (Ljava/lang/String;Ljava/lang/String;)V addServer
        lanentry net/minecraft/client/gui/screens/multiplayer/ServerSelectionList$NetworkServerEntry
        \ta serverData
        \tb LAN_SERVER_HEADER
        \tc (Ljava/lang/Object;IIIIIIIZF)V render
        \td ()Lcmp; getServerNarration
        mc net/minecraft/client/Minecraft
        \tu ()Lusr; getUser
        usr net/minecraft/client/User
        \tn ()Ljava/lang/String; getName
        wd net/minecraft/world/level/storage/WorldData
        \tn ()Ljava/lang/String; getLevelName
        cmp net/minecraft/network/chat/Component
        \ta (Ljava/lang/String;)Lcmp; literal
        """.Replace("\\t", "\t", StringComparison.Ordinal);

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
        Assert.Equal("addServer,c", properties["steamDiscoveryLanAddMethods"]);
        Assert.Equal("getName,n", properties["steamDiscoveryUserNameMethods"]);
        Assert.Equal("getLevelName,n", properties["steamDiscoveryLevelNameMethods"]);
        Assert.Equal("render,c", properties["steamDiscoveryLanEntryRenderMethods"]);
    }

    [Theory]
    [InlineData("\tv lanServerList")]
    [InlineData("\tb isDirty")]
    [InlineData("\ta (Lffd;)V join")]
    [InlineData("\tc (Ljava/lang/String;Ljava/lang/String;)V addServer")]
    [InlineData("\tb LAN_SERVER_HEADER")]
    [InlineData("\tn ()Ljava/lang/String; getLevelName")]
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

    [Fact]
    public void ModernExtractContentAndLegacyNarration_AreMappedByNativeNames()
    {
        var properties = Build(Mappings.Replace(" render", " extractContent", StringComparison.Ordinal)
            .Replace(" getServerNarration", " getNarration", StringComparison.Ordinal), out var target);
        Assert.NotNull(target);
        Assert.Equal("extractContent,c", properties["steamDiscoveryLanEntryRenderMethods"]);
        Assert.Equal("getNarration,d", properties["steamDiscoveryLanEntryNarrationMethods"]);
    }

    [Fact]
    public void ActualNativeUdpPacketDescriptor_UsesInetAddressWithoutGuessingStringShape()
    {
        var properties = Build(Mappings.Replace("(Ljava/lang/String;Ljava/lang/String;)V addServer",
            "(Ljava/lang/String;Ljava/net/InetAddress;)V addServer", StringComparison.Ordinal), out var target);
        Assert.NotNull(target);
        Assert.Equal("(Ljava/lang/String;Ljava/net/InetAddress;)V", properties["steamDiscoveryLanAddDescriptors"]);
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
