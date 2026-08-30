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
        TempTree.Delete(_root);
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
        "ayf net/minecraft/util/HttpUtil",
        "\ta ()I getAvailablePort",
        "dct net/minecraft/world/level/GameType",
        "ans net/minecraft/server/commands/PublishCommand",
        "\ta (I)Lxn; getSuccessMessage",
        "erl net/minecraft/world/level/storage/WorldData",
        "\tm ()Z isAllowCommands",
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
        "grm",
        "com/mojang/authlib/yggdrasil/TextureUrlChecker",
        "com/mojang/authlib/yggdrasil/YggdrasilMinecraftSessionService",
        "com/mojang/authlib/GameProfile"
    ];

    private (IdentityAdapterMappingService Service, PreparedRuntime Runtime, string GameDirectory) CreateFixture(
        string mappings,
        IReadOnlyList<string> jarClasses,
        bool withMappingFile = true)
    {
        var paths = new AppPaths(_root);
        var runtimeRoot = Path.Combine(_root, "Minecraft", "Launcher", "Runtimes", "Test");
        var mappingDirectory = Path.Combine(runtimeRoot, "libraries", "neoform");
        Directory.CreateDirectory(mappingDirectory);
        if (withMappingFile)
        {
            File.WriteAllText(Path.Combine(mappingDirectory, "neoform-test-mappings-merged.txt"), mappings);
        }

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

    /// <summary>
    /// A runtime with no mappings at all still gets its skins. This is every
    /// Fabric pack: Fabric ships intermediary, not TSRG2, so there is nothing
    /// for the launcher to read and the UUID hooks cannot be placed. The skin
    /// hooks never needed them - they are all in com.mojang.authlib, which no
    /// loader obfuscates - and refusing them along with the rest is what left
    /// All The Fabric 3 without a skin.
    /// </summary>
    [Fact]
    public void ARuntimeWithoutMappings_StillGetsItsSkins()
    {
        var (service, runtime, gameDirectory) =
            CreateFixture(GoldenMappings, DefaultJarClasses, withMappingFile: false);

        var configuration = service.Build(runtime, gameDirectory);

        // The skin hooks are configured...
        Assert.Equal("getTextures,getPackedTextures", configuration.Properties["skinReaderMethods"]);
        Assert.Contains(
            configuration.Targets,
            target => target.ClassName == "com/mojang/authlib/yggdrasil/YggdrasilMinecraftSessionService");

        // ...and the UUID hooks are switched off by a flag rather than by an
        // empty alias list, which would fall back to the built-in defaults and
        // have the transformer patch classes it knows nothing about.
        Assert.Equal("false", configuration.Properties["identityHooksEnabled"]);
        Assert.DoesNotContain(
            configuration.Targets,
            target => target.ClassName.Contains("ServerLoginPacketListenerImpl", StringComparison.Ordinal));
    }

    /// <summary>
    /// And it keeps everyone's identity too, which is the half that decides
    /// whether a player's inventory is still there after they change machines.
    /// </summary>
    /// <remarks>
    /// Offline Minecraft has one answer to who a player is: their name, hashed.
    /// The launcher knows whose Steam account each name belongs to, and the one
    /// place every version can be told is where the profile is built -
    /// com/mojang/authlib/GameProfile, which no loader obfuscates. So this
    /// survives having no mappings, where the login patches do not.
    /// </remarks>
    [Fact]
    public void ARuntimeWithoutMappings_StillKeepsEveryonesIdentity()
    {
        var (service, runtime, gameDirectory) =
            CreateFixture(GoldenMappings, DefaultJarClasses, withMappingFile: false);

        var configuration = service.Build(runtime, gameDirectory);

        Assert.Equal("com/mojang/authlib/GameProfile", configuration.Properties["gameProfileClasses"]);
        Assert.Contains(
            configuration.Targets,
            target => target.ClassName == "com/mojang/authlib/GameProfile");
    }

    /// <summary>
    /// And so does a runtime whose mappings are there but do not describe the
    /// classes the UUID hooks need. This is RPG Ars Nouveau: 1.20.1 has no
    /// net/minecraft/client/resources/PlayerSkin, which arrived in 1.20.2, so
    /// the whole adapter used to be refused over a class the skin never touches.
    /// </summary>
    [Fact]
    public void AMinecraftTooOldForTheUuidHooks_StillGetsItsSkins()
    {
        var withoutPlayerSkin = string.Join(
            '\n',
            GoldenMappings.Split('\n').Where(line => !line.Contains("PlayerSkin", StringComparison.Ordinal)));
        var (service, runtime, gameDirectory) = CreateFixture(withoutPlayerSkin, DefaultJarClasses);

        var configuration = service.Build(runtime, gameDirectory);

        Assert.Equal("false", configuration.Properties["identityHooksEnabled"]);
        Assert.Equal("getTextures,getPackedTextures", configuration.Properties["skinReaderMethods"]);
        Assert.Contains(
            configuration.Targets,
            target => target.ClassName == "com/mojang/authlib/yggdrasil/YggdrasilMinecraftSessionService");
    }

    /// <summary>
    /// The preflight refuses to run on a missing alias list, so every list it
    /// asks about is named even when the hooks behind it are switched off.
    /// </summary>
    [Fact]
    public void ASkinOnlyConfiguration_StillNamesEveryListThePreflightAsksFor()
    {
        var (service, runtime, gameDirectory) =
            CreateFixture(GoldenMappings, DefaultJarClasses, withMappingFile: false);

        var properties = service.Build(runtime, gameDirectory).Properties;

        foreach (var required in new[]
                 {
                     "loginClasses", "playerInfoClasses", "textureUrlCheckerClasses",
                     "ftbTeleportClasses", "solarFluxPackClasses", "xaeroWaypointTeleportClasses"
                 })
        {
            Assert.False(string.IsNullOrWhiteSpace(properties.GetValueOrDefault(required)), required);
        }
    }

    /// <summary>
    /// An authlib old enough to have no TextureUrlChecker is not a runtime to
    /// refuse: it keeps the same rule on the session service instead, and that
    /// class has been there throughout.
    /// </summary>
    [Fact]
    public void AnAuthlibWithoutTextureUrlChecker_IsStillPatched()
    {
        var older = DefaultJarClasses
            .Where(name => !name.EndsWith("TextureUrlChecker", StringComparison.Ordinal))
            .ToList();
        var (service, runtime, gameDirectory) = CreateFixture(GoldenMappings, older);

        var configuration = service.Build(runtime, gameDirectory);

        Assert.Contains(
            configuration.Targets,
            target => target.ClassName == "com/mojang/authlib/yggdrasil/YggdrasilMinecraftSessionService");
        Assert.DoesNotContain(
            configuration.Targets,
            target => target.ClassName.EndsWith("TextureUrlChecker", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_EmitsGoldenIdentityAliases()
    {
        var (service, runtime, gameDirectory) = CreateFixture(GoldenMappings, DefaultJarClasses);

        var configuration = service.Build(runtime, gameDirectory);

        var properties = configuration.Properties;

        // MinecraftServer is not obfuscated, so the alias list collapses to a
        // single entry — the preflight's clamped alias lookup depends on it.
        Assert.Equal("net/minecraft/server/MinecraftServer", properties["serverClasses"]);

        // Pin the aliases the identity and skin patches rely on, so version
        // drift fails loudly here rather than at launch preflight.
        Assert.Equal("net/minecraft/server/network/ServerLoginPacketListenerImpl,arw", properties["loginClasses"]);
        Assert.Equal("literal,b", properties["componentLiteralMethods"]);
        Assert.Equal("net.minecraft.network.chat.Component,wz", properties["componentClasses"]);

        // Opening a world to the network again skips the settings screen, so
        // the aliases the guard reads are pinned like every other pair. The LAN
        // list the VPN transport used is a different thing and stays gone.
        Assert.Equal("net/minecraft/client/gui/screens/ShareToLanScreen,foe", properties["lanShareScreenClasses"]);
        Assert.Equal("init,aT_", properties["lanShareInitMethods"]);
        Assert.Equal("net/minecraft/client/server/IntegratedServer,guo", properties["integratedServerClasses"]);
        Assert.Equal("publishServer,a", properties["publishServerMethods"]);
        Assert.Equal("isPublished,r", properties["isPublishedMethods"]);
        Assert.Equal("getDefaultGameType,u_", properties["getDefaultGameTypeMethods"]);
        Assert.Equal("getWorldData,bb", properties["getWorldDataMethods"]);
        Assert.Equal("isAllowCommands,m", properties["isAllowCommandsMethods"]);
        Assert.Equal("net.minecraft.util.HttpUtil,ayf", properties["httpUtilClasses"]);
        Assert.Equal("getAvailablePort,a", properties["getAvailablePortMethods"]);
        Assert.Equal("net.minecraft.world.level.GameType,dct", properties["gameTypeClasses"]);
        Assert.Equal("net.minecraft.client.Minecraft,fgo", properties["minecraftClasses"]);
        Assert.Equal("getInstance,Q", properties["minecraftGetInstanceMethods"]);
        Assert.Equal("getSingleplayerServer,V", properties["getSingleplayerServerMethods"]);
        Assert.Equal("setScreen,a", properties["setScreenMethods"]);
        Assert.Equal("updateTitle,d", properties["updateTitleMethods"]);
        Assert.Equal("gui,l", properties["minecraftGuiFields"]);
        Assert.Equal("getChat,d", properties["guiChatMethods"]);
        Assert.Equal("addMessage,a", properties["chatAddMessageMethods"]);
        Assert.Equal("getSuccessMessage,a", properties["publishSuccessMethods"]);
        Assert.Contains(
            configuration.Targets,
            target => target.ClassName == "net/minecraft/client/gui/screens/ShareToLanScreen");
        Assert.DoesNotContain(
            configuration.Targets,
            target => target.ClassName.Contains("NetworkServerEntry", StringComparison.Ordinal));

        // Both places authlib has ever kept the rule about which hosts a skin
        // may come from, and both names it has gone by. Read out of all
        // eighteen published authlib jars, 1.5.21 to 9.0.75: the rule sat on
        // the session service as isWhitelistedDomain through 2.1.28, as
        // isAllowedTextureDomain from 2.3.31 to 3.16.29, and moved to
        // TextureUrlChecker at 3.18.38. Naming only the last of the three is
        // what left every pack before Minecraft 1.19.4 - All The Fabric 3 among
        // them - unable to show a skin at all: the class the launcher went
        // looking for is not in those versions. None of this comes from the
        // runtime's mappings, because com.mojang.authlib is never obfuscated,
        // which is why this one patch is the same on every loader.
        Assert.Equal(
            "com/mojang/authlib/yggdrasil/TextureUrlChecker," +
            "com/mojang/authlib/yggdrasil/YggdrasilMinecraftSessionService",
            properties["textureUrlCheckerClasses"]);
        Assert.Equal("isAllowedTextureDomain,isWhitelistedDomain", properties["textureUrlCheckerMethods"]);
        Assert.Equal("(Ljava/lang/String;)Z", properties["textureUrlCheckerDescriptors"]);

        // The removed teleport patches must stay dormant: their properties are
        // still emitted, pinned off, and nothing is targeted for them.
        Assert.Equal("false", properties["ftbTeleportEnabled"]);
        Assert.DoesNotContain(
            configuration.Targets,
            target => target.ClassName.StartsWith("dev/ftb/mods/ftbchunks/", StringComparison.Ordinal));
    }
}
