using System.IO.Compression;
using Minecraft;

namespace Minecraft.Tests;

public sealed class IdentityAdapterMappingServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-identity-mappings-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // Verbatim excerpt of neoform-1.21.1-20240808.144430-mappings-merged.txt:
    // the exact member lines IdentityAdapterMappingService.Build requires. Any
    // drift between these lines and the emitted alias properties is a golden
    // regression, not a fixture update.
    private static readonly string GoldenMappings = string.Join('\n', new[]
    {
        "tsrg2 obf mojmap",
        "arw net/minecraft/server/network/ServerLoginPacketListenerImpl",
        "\tf server",
        "\tg connection",
        "\tj requestedUsername",
        "\ta (Lwz;)V disconnect",
        "\ta (Laiy;)V handleHello",
        "\tb (Lcom/mojang/authlib/GameProfile;)V startClientVerification",
        "\tc (Lcom/mojang/authlib/GameProfile;)V verifyLoginAndFinishConnectionSetup",
        "aiy net/minecraft/network/protocol/login/ServerboundHelloPacket",
        "\tb ()Ljava/lang/String; name",
        "\te ()Ljava/util/UUID; profileId",
        "net/minecraft/server/MinecraftServer net/minecraft/server/MinecraftServer",
        "\tah ()Laur; getPlayerList",
        "\tbb ()Lerl; getWorldData",
        "\tr ()Z isPublished",
        "\tu_ ()Ldct; getDefaultGameType",
        "vt net/minecraft/network/Connection",
        "\te ()Z isMemoryConnection",
        "aur net/minecraft/server/players/PlayerList",
        "\ta (Ljava/util/UUID;)Laqv; getPlayer",
        "wz net/minecraft/network/chat/Component",
        "\tb (Ljava/lang/String;)Lxn; literal",
        "fzq net/minecraft/client/multiplayer/PlayerInfo",
        "\ta (Ljava/util/concurrent/CompletableFuture;Lgrl;Z)Lgrl; lambda$createSkinLookup$2",
        "\ta (Lcom/mojang/authlib/GameProfile;)Ljava/util/function/Supplier; createSkinLookup",
        "grl net/minecraft/client/resources/PlayerSkin",
        "\tb ()Ljava/lang/String; textureUrl",
        "\tf ()Z secure",
        "fzg net/minecraft/client/multiplayer/ClientPacketListener",
        "\tc (Ljava/lang/String;)V sendCommand",
        "\td (Ljava/lang/String;)Z sendUnsignedCommand",
        "fod net/minecraft/client/gui/screens/Screen",
        "fqx$c net/minecraft/client/gui/screens/multiplayer/ServerSelectionList$NetworkServerEntry",
        "\tb serverData",
        "\td LAN_SERVER_HEADER",
        "\ta (Lfhz;IIIIIIIZF)V render",
        "gup net/minecraft/client/server/LanServer",
        "\ta ()Ljava/lang/String; getMotd",
        "foe net/minecraft/client/gui/screens/ShareToLanScreen",
        "\taT_ ()V init",
        "guo net/minecraft/client/server/IntegratedServer",
        "\ta (Ldct;ZI)Z publishServer",
        "fgo net/minecraft/client/Minecraft",
        "\tl gui",
        "\tQ ()Lfgo; getInstance",
        "\tV ()Lguo; getSingleplayerServer",
        "\ta (Lfod;)V setScreen",
        "\td ()V updateTitle",
        "fhy net/minecraft/client/gui/Gui",
        "\td ()Lfin; getChat",
        "fin net/minecraft/client/gui/components/ChatComponent",
        "\ta (Lwz;)V addMessage",
        "\ta (Lwz;Lxl;Lfgj;)V addMessage",
        "ayf net/minecraft/util/HttpUtil",
        "\ta ()I getAvailablePort",
        "dct net/minecraft/world/level/GameType",
        "ans net/minecraft/server/commands/PublishCommand",
        "\ta (I)Lxn; getSuccessMessage",
        "erl net/minecraft/world/level/storage/WorldData",
        "\tm ()Z isAllowCommands"
    });

    private static readonly string[] DefaultJarClasses =
    [
        "net/minecraft/server/network/ServerLoginPacketListenerImpl",
        "arw",
        "net/minecraft/client/multiplayer/PlayerInfo",
        "fzq",
        "net/minecraft/client/gui/screens/multiplayer/ServerSelectionList$NetworkServerEntry",
        "fqx$c",
        "net/minecraft/client/gui/screens/ShareToLanScreen",
        "foe",
        "com/mojang/authlib/yggdrasil/TextureUrlChecker"
    ];

    private (IdentityAdapterMappingService Service, PreparedRuntime Runtime, string GameDirectory) CreateFixture(
        string mappings,
        IReadOnlyList<string> jarClasses)
    {
        var paths = new AppPaths(_root);
        var runtimeRoot = Path.Combine(_root, "Minecraft", "Launcher", "Runtimes", "Test");
        var mappingDirectory = Path.Combine(runtimeRoot, "libraries", "neoform");
        Directory.CreateDirectory(mappingDirectory);
        File.WriteAllText(Path.Combine(mappingDirectory, "neoform-test-mappings-merged.txt"), mappings);

        var clientJar = Path.Combine(runtimeRoot, "client.jar");
        using (var archive = ZipFile.Open(clientJar, ZipArchiveMode.Create))
        {
            foreach (var className in jarClasses)
            {
                archive.CreateEntry(className + ".class");
            }
        }

        var gameDirectory = Path.Combine(_root, "Minecraft", "Personal", "Instances", "Test");
        Directory.CreateDirectory(gameDirectory);
        var runtime = new PreparedRuntime(
            runtimeRoot,
            "test-profile",
            Path.Combine(runtimeRoot, "java.exe"),
            clientJar,
            new PackRuntimeDescriptor(
                1,
                "1.21.1",
                new PackLoaderDescriptor(PackLoaderKind.NeoForge, "21.1.224"),
                "client.jar",
                "descriptor-hash"));
        return (new IdentityAdapterMappingService(paths), runtime, gameDirectory);
    }

    [Fact]
    public void Build_EmitsGoldenLanSharePublishAliases()
    {
        var (service, runtime, gameDirectory) = CreateFixture(GoldenMappings, DefaultJarClasses);

        var configuration = service.Build(runtime, gameDirectory, enableXaeroWaypointBridge: false);

        var properties = configuration.Properties;
        Assert.Equal("net/minecraft/client/gui/screens/ShareToLanScreen,foe", properties["lanShareScreenClasses"]);
        Assert.Equal("init,aT_", properties["lanShareInitMethods"]);
        Assert.Equal("net/minecraft/client/server/IntegratedServer,guo", properties["integratedServerClasses"]);
        Assert.Equal("publishServer,a", properties["publishServerMethods"]);
        Assert.Equal("getDefaultGameType,u_", properties["getDefaultGameTypeMethods"]);
        Assert.Equal("getWorldData,bb", properties["getWorldDataMethods"]);
        Assert.Equal("isPublished,r", properties["isPublishedMethods"]);
        Assert.Equal("net/minecraft/world/level/storage/WorldData,erl", properties["worldDataClasses"]);
        Assert.Equal("isAllowCommands,m", properties["isAllowCommandsMethods"]);
        Assert.Equal("net.minecraft.util.HttpUtil,ayf", properties["httpUtilClasses"]);
        Assert.Equal("getAvailablePort,a", properties["getAvailablePortMethods"]);
        Assert.Equal("net.minecraft.world.level.GameType,dct", properties["gameTypeClasses"]);
        Assert.Equal("net.minecraft.server.commands.PublishCommand,ans", properties["publishCommandClasses"]);
        Assert.Equal("getSuccessMessage,a", properties["publishSuccessMethods"]);
        Assert.Equal("net.minecraft.client.Minecraft,fgo", properties["minecraftClasses"]);
        Assert.Equal("getInstance,Q", properties["minecraftGetInstanceMethods"]);
        Assert.Equal("getSingleplayerServer,V", properties["getSingleplayerServerMethods"]);
        Assert.Equal("setScreen,a", properties["setScreenMethods"]);
        Assert.Equal("updateTitle,d", properties["updateTitleMethods"]);
        Assert.Equal("gui,l", properties["minecraftGuiFields"]);
        Assert.Equal("net.minecraft.client.gui.screens.Screen,fod", properties["screenClasses"]);
        Assert.Equal("net/minecraft/client/gui/Gui,fhy", properties["guiClasses"]);
        Assert.Equal("getChat,d", properties["guiChatMethods"]);
        Assert.Equal("net/minecraft/client/gui/components/ChatComponent,fin", properties["chatComponentClasses"]);
        Assert.Equal("addMessage,a", properties["chatAddMessageMethods"]);

        // MinecraftServer is not obfuscated, so the alias list collapses to a
        // single entry — the preflight's clamped alias lookup depends on it.
        Assert.Equal("net/minecraft/server/MinecraftServer", properties["serverClasses"]);

        // Pin a few pre-existing aliases so version drift in the shared classes
        // fails loudly here rather than at launch preflight.
        Assert.Equal("net/minecraft/server/network/ServerLoginPacketListenerImpl,arw", properties["loginClasses"]);
        Assert.Equal("literal,b", properties["componentLiteralMethods"]);
        Assert.Equal("net.minecraft.network.chat.Component,wz", properties["componentClasses"]);

        Assert.Contains(
            configuration.Targets,
            target => target.ClassName == "net/minecraft/client/gui/screens/ShareToLanScreen");
        Assert.Contains(configuration.Targets, target => target.ClassName == "foe");
    }

    [Fact]
    public void Build_WithoutShareScreenInitMapping_Throws()
    {
        var withoutInit = GoldenMappings.Replace("\taT_ ()V init", "\taT_ ()V initRenamed", StringComparison.Ordinal);
        var (service, runtime, gameDirectory) = CreateFixture(withoutInit, DefaultJarClasses);

        Assert.Throws<InvalidDataException>(
            () => service.Build(runtime, gameDirectory, enableXaeroWaypointBridge: false));
    }

    [Fact]
    public void Build_WithoutShareScreenClassInJars_Throws()
    {
        var jarClasses = DefaultJarClasses
            .Where(className => className is not "net/minecraft/client/gui/screens/ShareToLanScreen" and not "foe")
            .ToArray();
        var (service, runtime, gameDirectory) = CreateFixture(GoldenMappings, jarClasses);

        var exception = Assert.Throws<NotSupportedException>(
            () => service.Build(runtime, gameDirectory, enableXaeroWaypointBridge: false));
        Assert.Contains("required runtime classes are absent", exception.Message, StringComparison.Ordinal);
    }
}
