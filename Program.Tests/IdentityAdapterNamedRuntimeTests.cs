using System.IO.Compression;
using System.Text;
using Minecraft;

namespace Minecraft.Tests;

public sealed class IdentityAdapterNamedRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "named-runtime-" + Guid.NewGuid().ToString("N"));
    private const string Screen = "net/minecraft/client/gui/screens/Screen";
    private const string Scope = "net/minecraft/server/MinecraftServer$MultiplayerScope";
    private const string Authlib = "com/mojang/authlib/";

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void NamedClientWithoutMappingDownloads_UsesActualScopedPublishAndSkinLookup()
    {
        var (service, runtime, game) = Fixture();
        var configuration = service.Build(runtime, game);
        var properties = configuration.Properties;

        Assert.Equal(runtime.ClientJarPath, configuration.MappingPath);
        Assert.Equal("true", properties["lanPublishEnabled"]);
        Assert.Equal("net/minecraft/client/gui/screens/MultiplayerOptionsScreen", properties["lanShareScreenClasses"]);
        Assert.Equal("init", properties["lanShareInitMethods"]);
        Assert.Equal($"(L{Scope};I)Z", properties["publishServerDescriptors"]);
        Assert.Equal("LAN", properties["lanMultiplayerScopeFields"]);
        Assert.Equal("true", properties["setScreenOnGui"]);
        Assert.Equal("hud", properties["guiHudFields"]);
        Assert.Equal("addClientSystemMessage", properties["chatAddMessageMethods"]);
        Assert.Equal("schedule", properties["minecraftScheduleMethods"]);
        Assert.Equal("true", properties["modernSkinLookupEnabled"]);
        Assert.Equal("createLookup", properties["skinManagerLookupMethods"]);
        Assert.Contains(configuration.Targets, target => target.ClassName == "net/minecraft/client/resources/SkinManager");
        Assert.Equal("true", properties["pauseReportingEnabled"]);
        Assert.Equal("createPauseMenu", properties["pauseCreateMenuMethods"]);
        Assert.Equal("MULTIPLAYER_OPTIONS", properties["pauseMultiplayerFields"]);
        Assert.Equal("PLAYER_REPORTING", properties["pausePlayerReportingFields"]);
        Assert.Equal("openScreenButton", properties["pauseOpenScreenButtonMethods"]);
        Assert.Equal("net.minecraft.client.gui.screens.social.SocialInteractionsScreen", properties["reportingScreenClasses"]);
        Assert.Contains(configuration.Targets, target => target.ClassName == "net/minecraft/client/gui/screens/PauseScreen");
    }

    [Theory]
    [InlineData("reportingParentConstructor")]
    [InlineData("multiplayerLabel")]
    [InlineData("openScreenSignature")]
    [InlineData("createPauseMenu")]
    [InlineData("modernShareScreen")]
    public void PauseReporting_RequiresCompleteNativeModernMenuShape(string missingPart)
    {
        var (service, runtime, game) = Fixture(missingPart);
        var configuration = service.Build(runtime, game);
        Assert.Equal("false", configuration.Properties["pauseReportingEnabled"]);
        Assert.DoesNotContain(configuration.Targets, target => target.ClassName == "net/minecraft/client/gui/screens/PauseScreen");
    }

    [Fact]
    public void SharedStoreMappingsAndAuthlibFromOtherVersions_AreNotUsed()
    {
        var (service, runtime, game) = Fixture();
        var wrongMappings = Path.Combine(runtime.LibrariesRoot, "net", "minecraft", "client", "1.21.1", "mappings.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(wrongMappings)!);
        File.WriteAllText(wrongMappings, "net.minecraft.client.gui.screens.ShareToLanScreen -> wrong:\n    void init() -> a\n");
        var wrongTsrg = Path.Combine(runtime.LibrariesRoot, "neoform", "1.21.1", "mappings-merged.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(wrongTsrg)!);
        File.WriteAllText(wrongTsrg, "tsrg2 obf mojmap\nwrong net/minecraft/client/gui/screens/ShareToLanScreen\n\ta ()V init\n");

        var configuration = service.Build(runtime, game);
        Assert.Equal("true", configuration.Properties["lanPublishEnabled"]);
        var authlib = configuration.Targets.Where(target => target.ClassName.StartsWith(Authlib, StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, authlib.Length);
        Assert.All(authlib, target => Assert.EndsWith("authlib-9.0.75.jar", target.JarPath, StringComparison.Ordinal));
    }

    [Fact]
    public void Authlib10_UsesItsNativeSessionAndDiscoveryServices()
    {
        var (service, runtime, game) = Fixture(modernAuthlib: true);
        var configuration = service.Build(runtime, game);
        foreach (var name in new[] { "services/MinecraftServicesSessionService", "services/MinecraftServicesDiscoveryService", "GameProfile" })
            Assert.Contains(configuration.Targets, target => target.ClassName == Authlib + name &&
                target.JarPath.EndsWith("authlib-10.0.77.jar", StringComparison.Ordinal));
        Assert.DoesNotContain(configuration.Targets, target => target.ClassName == Authlib + "yggdrasil/TextureUrlChecker");
        Assert.Equal("true", configuration.Properties["modernSkinLookupEnabled"]);
    }

    [Fact]
    public void Authlib10WithoutNativeDomainDiscovery_IsRejected()
    {
        var (service, runtime, game) = Fixture(modernAuthlib: true, includeDiscovery: false);
        Assert.Throws<NotSupportedException>(() => service.Build(runtime, game));
    }

    [Fact]
    public void WorldOptionsRuntime_RequiresExplicitLanRequestAndKeepsNativeSettingsEntry()
    {
        var (service, runtime, game) = Fixture(worldOptionsRuntime: true);
        var configuration = service.Build(runtime, game);
        var properties = configuration.Properties;
        Assert.Equal("true", properties["lanPublishEnabled"]);
        Assert.Equal("true", properties["lanPublishExplicitRequestOnly"]);
        Assert.Equal("true", properties["pauseLanRowEnabled"]);
        Assert.Equal("false", properties["pauseReportingEnabled"]);
        Assert.Equal("net/minecraft/client/gui/screens/WorldOptionsScreen", properties["lanShareScreenClasses"]);
        Assert.Equal("net.minecraft.client.gui.screens.WorldOptionsScreen", properties["worldOptionsScreenClasses"]);
        Assert.Equal("addChild", properties["pauseRowAddMethods"]);
        Assert.Equal("getWidth", properties["widgetGetWidthMethods"]);
        Assert.Equal("net/minecraft/client/gui/components/AbstractWidget", properties["widgetClasses"]);
        Assert.Equal("setWidth", properties["widgetSetWidthMethods"]);
        Assert.Equal("level", properties["minecraftLevelFields"]);
        Assert.Equal("translatable", properties["componentTranslatableMethods"]);
        Assert.Equal("false", properties["lanRedirectNativeInitEnabled"]);
        Assert.DoesNotContain("pauseMultiplayerFields", properties.Keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LanRedirectMapping_RejectsMissingOrModifiedSelectedTransport(bool modified)
    {
        var (service, runtime, game) = Fixture(worldOptionsRuntime: true);
        if (modified)
        {
            var pin = SteamTransportCatalog.Find(runtime.Descriptor)!;
            var mods = Path.Combine(game, "mods");
            Directory.CreateDirectory(mods);
            File.WriteAllBytes(Path.Combine(mods, pin.FileName), [1, 2, 3]);
        }
        var properties = service.Build(runtime, game).Properties;
        Assert.Equal("false", properties["lanRedirectNativeInitEnabled"]);
        Assert.DoesNotContain("lanRedirectTransportSha256", properties.Keys);
        Assert.DoesNotContain("lanRedirectMixinOrigin", properties.Keys);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeTargets_UseOnlyExactNeoForgePatchedClient(bool installedCurrent)
    {
        var (service, runtime, game) = Fixture(worldOptionsRuntime: true);
        var expected = "";
        foreach (var version in installedCurrent ? new[] { "26.3.0.98", "26.3.0.99" } : new[] { "26.3.0.98" })
        {
            var jar = Path.Combine(runtime.LibrariesRoot, "net", "neoforged", "minecraft-client-patched", version,
                "minecraft-client-patched-" + version + ".jar");
            Directory.CreateDirectory(Path.GetDirectoryName(jar)!);
            using var archive = ZipFile.Open(jar, ZipArchiveMode.Create);
            archive.CreateEntry("net/minecraft/client/gui/screens/WorldOptionsScreen.class");
            if (version == "26.3.0.99") expected = jar;
        }
        var target = Assert.Single(service.Build(runtime, game).Targets,
            candidate => candidate.ClassName == "net/minecraft/client/gui/screens/WorldOptionsScreen");
        Assert.Equal(installedCurrent ? expected : runtime.ClientJarPath, target.JarPath);
    }

    [Theory]
    [InlineData("worldParentConstructor")]
    [InlineData("rowAddSignature")]
    [InlineData("minecraftLevel")]
    public void WorldOptionsPauseRow_RequiresCompleteNativeRequestShape(string missingPart)
    {
        var (service, runtime, game) = Fixture(missingPart, worldOptionsRuntime: true);
        var configuration = service.Build(runtime, game);
        Assert.Equal("false", configuration.Properties["pauseLanRowEnabled"]);
        Assert.DoesNotContain(configuration.Targets, target => target.ClassName == "net/minecraft/client/gui/screens/PauseScreen");
    }

    private (IdentityAdapterMappingService, PreparedRuntime, string) Fixture(
        string? missingPausePart = null, bool modernAuthlib = false, bool includeDiscovery = true,
        bool worldOptionsRuntime = false)
    {
        var paths = new AppPaths(_root);
        var libraries = SharedRuntimeStore.Libraries(paths);
        var versions = SharedRuntimeStore.Versions(paths);
        var minecraftVersion = worldOptionsRuntime ? "26.3" : "26.2";
        var client = Path.Combine(versions, minecraftVersion, minecraftVersion + ".jar");
        Directory.CreateDirectory(Path.GetDirectoryName(client)!);
        using (var archive = ZipFile.Open(client, ZipArchiveMode.Create))
        {
            Add(archive, Screen);
            if (!worldOptionsRuntime && missingPausePart != "modernShareScreen")
                Add(archive, "net/minecraft/client/gui/screens/MultiplayerOptionsScreen", ("init", "()V"));
            if (worldOptionsRuntime)
            {
                Add(archive, "net/minecraft/client/gui/screens/WorldOptionsScreen", ("init", "()V"),
                    ("<init>", missingPausePart == "worldParentConstructor" ? "()V" :
                        $"(L{Screen};Lnet/minecraft/world/level/Level;)V"));
                Add(archive, "net/minecraft/client/gui/layouts/GridLayout$RowHelper", ("addChild",
                    missingPausePart == "rowAddSignature" ? "()V" :
                        "(Lnet/minecraft/client/gui/layouts/LayoutElement;I)Lnet/minecraft/client/gui/layouts/LayoutElement;"));
                Add(archive, "net/minecraft/client/gui/layouts/LayoutElement");
                Add(archive, "net/minecraft/client/gui/components/AbstractWidget", ("getWidth", "()I"), ("setWidth", "(I)V"));
                Add(archive, "net/minecraft/world/level/Level");
            }
            Add(archive, "net/minecraft/client/gui/components/Button");
            Add(archive, "net/minecraft/client/gui/screens/social/SocialInteractionsScreen",
                ("<init>", missingPausePart == "reportingParentConstructor" ? "()V" : $"(L{Screen};)V"));
            Add(archive, "net/minecraft/client/gui/screens/PauseScreen",
                (missingPausePart == "multiplayerLabel" ? "OTHER_LABEL" : worldOptionsRuntime ? "WORLD_OPTIONS" : "MULTIPLAYER_OPTIONS", "Lnet/minecraft/network/chat/Component;"),
                ("PLAYER_REPORTING", "Lnet/minecraft/network/chat/Component;"),
                (missingPausePart == "createPauseMenu" ? "otherMenu" : "createPauseMenu", "()V"),
                ("openScreenButton", missingPausePart == "openScreenSignature" ? "()V" :
                    "(Lnet/minecraft/network/chat/Component;Ljava/util/function/Supplier;)Lnet/minecraft/client/gui/components/Button;"));
            Add(archive, Scope, ("LAN", "L" + Scope + ";"));
            Add(archive, "net/minecraft/client/server/IntegratedServer", ("publishServer", $"(L{Scope};I)Z"));
            Add(archive, "net/minecraft/client/Minecraft", ("gui", "Lnet/minecraft/client/gui/Gui;"),
                ("getInstance", "()Lnet/minecraft/client/Minecraft;"),
                ("getSingleplayerServer", "()Lnet/minecraft/client/server/IntegratedServer;"), ("updateTitle", "()V"),
                ("createTitle", "()Ljava/lang/String;"), ("getConnection", "()Lnet/minecraft/client/multiplayer/ClientPacketListener;"),
                (missingPausePart == "minecraftLevel" ? "otherLevel" : "level", "Lnet/minecraft/client/multiplayer/ClientLevel;"));
            Add(archive, "net/minecraft/client/multiplayer/ClientPacketListener");
            Add(archive, "net/minecraft/client/gui/Gui", ("hud", "Lnet/minecraft/client/gui/Hud;"), ("setScreen", $"(L{Screen};)V"));
            Add(archive, "net/minecraft/client/gui/Hud", ("getChat", "()Lnet/minecraft/client/gui/components/ChatComponent;"));
            Add(archive, "net/minecraft/client/gui/components/ChatComponent", ("addClientSystemMessage", "(Lnet/minecraft/network/chat/Component;)V"));
            Add(archive, "net/minecraft/util/thread/BlockableEventLoop", ("schedule", "(Ljava/lang/Runnable;)V"));
            Add(archive, "net/minecraft/util/HttpUtil", ("getAvailablePort", "()I"));
            Add(archive, "net/minecraft/world/level/GameType");
            Add(archive, "net/minecraft/server/commands/PublishCommand");
            Add(archive, "net/minecraft/world/level/storage/WorldData", ("isAllowCommands", "()Z"));
            Add(archive, "net/minecraft/network/chat/Component", ("translatable", "(Ljava/lang/String;)Lnet/minecraft/network/chat/MutableComponent;"));
            Add(archive, "net/minecraft/server/players/PlayerList");
            Add(archive, "net/minecraft/server/MinecraftServer", ("getDefaultGameType", "()Lnet/minecraft/world/level/GameType;"),
                ("getWorldData", "()Lnet/minecraft/world/level/storage/WorldData;"), ("isPublished", "()Z"),
                ("getPlayerList", "()Lnet/minecraft/server/players/PlayerList;"));
            Add(archive, "net/minecraft/client/resources/SkinManager", ("createLookup", "(Lcom/mojang/authlib/GameProfile;Z)Ljava/util/function/Supplier;"));
        }
        var selectedAuthlib = modernAuthlib || worldOptionsRuntime ? "10.0.77" : "9.0.75";
        foreach (var version in new[] { "3.3.39", selectedAuthlib })
        {
            var jar = Path.Combine(libraries, "com", "mojang", "authlib", version, "authlib-" + version + ".jar");
            Directory.CreateDirectory(Path.GetDirectoryName(jar)!);
            using var archive = ZipFile.Open(jar, ZipArchiveMode.Create);
            var classes = version == "10.0.77"
                ? new[] { "GameProfile", "services/MinecraftServicesSessionService", "services/MinecraftServicesDiscoveryService" }
                : new[] { "GameProfile", "yggdrasil/YggdrasilMinecraftSessionService", "yggdrasil/TextureUrlChecker" };
            foreach (var name in classes.Where(name => includeDiscovery || name != "services/MinecraftServicesDiscoveryService"))
                archive.CreateEntry(Authlib + name + ".class");
        }
        File.WriteAllText(Path.Combine(versions, minecraftVersion, minecraftVersion + ".json"), $$"""
            {"id":"{{minecraftVersion}}","libraries":[{"name":"com.mojang:authlib:{{selectedAuthlib}}"}]}
            """);
        var game = Path.Combine(paths.Instances, "Test");
        Directory.CreateDirectory(game);
        var runtime = new PreparedRuntime(Path.Combine(paths.Runtimes, "Test"), minecraftVersion, "java.exe", client,
            new PackRuntimeDescriptor(1, minecraftVersion, new PackLoaderDescriptor(PackLoaderKind.NeoForge,
                worldOptionsRuntime ? "26.3.0.99" : "26.2.0.84"), "", "test"))
            { LibrariesRoot = libraries };
        return (new IdentityAdapterMappingService(paths), runtime, game);
    }

    // Minimal class-file structure for the mapping reader. These fixtures test
    // names and descriptors, not executable JVM methods or transformer output.
    private static void Add(ZipArchive archive, string name, params (string Name, string Descriptor)[] members)
    {
        using var writer = new BinaryWriter(archive.CreateEntry(name + ".class").Open());
        void U2(int value) { writer.Write((byte)(value >> 8)); writer.Write((byte)value); }
        writer.Write(new byte[] { 0xca, 0xfe, 0xba, 0xbe });
        U2(0); U2(69); U2(1 + members.Length * 2);
        foreach (var (member, descriptor) in members)
            foreach (var value in new[] { member, descriptor })
            {
                writer.Write((byte)1);
                var bytes = Encoding.UTF8.GetBytes(value);
                U2(bytes.Length); writer.Write(bytes);
            }
        U2(1); U2(0); U2(0); U2(0);
        foreach (var methods in new[] { false, true })
        {
            U2(members.Count(member => member.Descriptor.StartsWith('(') == methods));
            for (var index = 0; index < members.Length; index++)
            {
                if (members[index].Descriptor.StartsWith('(') != methods) continue;
                U2(1); U2(index * 2 + 1); U2(index * 2 + 2); U2(0);
            }
        }
        U2(0);
    }
}
