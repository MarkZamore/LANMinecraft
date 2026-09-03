using System.IO.Compression;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Fabric and Quilt name the game a third way.
///
/// What they ship is intermediary - class_3218 where Mojang wrote ServerLevel -
/// and it says nothing about official names at all, so it is only half an
/// answer; the other half is Mojang's own mappings, which the launcher fetches
/// for a runtime that carries none. Three things here are true of no other
/// loader: a class Mojang never obfuscated has no line at all rather than a
/// line saying it is unchanged, the descriptors are written in obfuscated
/// names, and a method that overrides another is named once, at the class that
/// declares it - so an override has no line of its own and is found by asking
/// the superclass.
/// </summary>
public sealed class IdentityAdapterIntermediaryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-intermediary-{Guid.NewGuid():N}");

    public void Dispose()
    {
        TempTree.Delete(_root);
    }

    [Fact]
    public void AFabricRuntime_IsReadThroughMojangsOwnMappings()
    {
        var (service, runtime, gameDirectory) = CreateFabricFixture();

        var properties = service.Build(runtime, gameDirectory).Properties;

        Assert.Equal("true", properties["lanPublishEnabled"]);
        // A class is loaded under its intermediary name, with the obfuscated
        // one beside it for the jar on disk.
        Assert.Equal("net/minecraft/class_1000,foe", properties["lanShareScreenClasses"]);
        Assert.Equal("net.minecraft.class_1001,fgo", properties["minecraftClasses"]);
        // A class Mojang never obfuscated has no line in the file, and that
        // means the name is already right rather than missing.
        Assert.Equal("net/minecraft/server/MinecraftServer", properties["serverClasses"]);
        // And a method is loaded under the name intermediary gave it.
        Assert.Equal("method_2000,a", properties["publishServerMethods"]);
    }

    /// <summary>
    /// ShareToLanScreen.init overrides Screen.init, so intermediary names it
    /// once, under Screen. Looking only at the class that was asked about finds
    /// nothing and would leave the hook unpatched.
    /// </summary>
    [Fact]
    public void AMethodNamedAtTheClassThatDeclaresIt_IsFoundFromTheOneThatOverridesIt()
    {
        var (service, runtime, gameDirectory) = CreateFabricFixture();

        var properties = service.Build(runtime, gameDirectory).Properties;

        Assert.Equal("method_2001,aT_", properties["lanShareInitMethods"]);
    }

    /// <summary>
    /// The library store is shared by every build on the machine, and a
    /// NeoForge pack leaves its own mappings in it. Reading a Fabric runtime
    /// through them is how All The Fabric 3 came to believe its 1.18.2 was
    /// 1.21.1: it then asked for the class 1.21.1 calls "arw", was handed the
    /// 1.18.2 class of that name - a datafixer - and the preflight refused it.
    /// The whole adapter was dropped, and the player lost their skin.
    /// </summary>
    [Fact]
    public void ANeoForgeMappingInTheSharedStore_IsNotReadAsThisRuntimesOwn()
    {
        var (service, runtime, gameDirectory) = CreateFabricFixture();
        var intruder = Path.Combine(
            runtime.LibrariesRoot, "net", "neoforged", "neoform", "1.21.1-20240808.144430");
        Directory.CreateDirectory(intruder);
        File.WriteAllText(
            Path.Combine(intruder, "neoform-1.21.1-20240808.144430-mappings-merged.txt"),
            """
            tsrg2 obf srg
            net/minecraft/server/network/ServerLoginPacketListenerImpl arw
            """);

        var properties = service.Build(runtime, gameDirectory).Properties;

        // Still read through intermediary, as if the intruder were not there.
        Assert.Equal("net.minecraft.class_1001,fgo", properties["minecraftClasses"]);
        Assert.Equal("method_2000,a", properties["publishServerMethods"]);
        // And nothing in it may name a class out of somebody else's Minecraft.
        Assert.DoesNotContain("arw", string.Join("|", properties.Values), StringComparison.Ordinal);
    }

    /// <summary>
    /// Without Mojang's file the intermediary names stand for nothing, and what
    /// needs no mappings at all is kept rather than guessed at.
    /// </summary>
    [Fact]
    public void AFabricRuntimeWithoutMojangsMappings_KeepsTheSkinsAndNothingElse()
    {
        var (service, runtime, gameDirectory) = CreateFabricFixture(withMojangMappings: false);

        var properties = service.Build(runtime, gameDirectory).Properties;

        Assert.Equal("false", properties["lanPublishEnabled"]);
        Assert.Equal("false", properties["identityHooksEnabled"]);
        Assert.Equal("getTextures,getPackedTextures", properties["skinReaderMethods"]);
    }

    // Just the LAN publish group, which is what a Fabric runtime in e4steam's
    // range can actually have: the UUID hooks need 1.20.2 and the range stops
    // below 1.19.
    private static readonly (string Official, string Obf, string? Intermediary)[] Classes =
    [
        ("net/minecraft/client/gui/screens/ShareToLanScreen", "foe", "net/minecraft/class_1000"),
        ("net/minecraft/client/Minecraft", "fgo", "net/minecraft/class_1001"),
        ("net/minecraft/client/server/IntegratedServer", "guo", "net/minecraft/class_1002"),
        ("net/minecraft/client/gui/Gui", "fhy", "net/minecraft/class_1003"),
        ("net/minecraft/client/gui/components/ChatComponent", "fin", "net/minecraft/class_1004"),
        ("net/minecraft/util/HttpUtil", "ayf", "net/minecraft/class_1005"),
        ("net/minecraft/world/level/GameType", "dct", "net/minecraft/class_1006"),
        ("net/minecraft/server/commands/PublishCommand", "ans", "net/minecraft/class_1007"),
        ("net/minecraft/world/level/storage/WorldData", "erl", "net/minecraft/class_1008"),
        ("net/minecraft/client/gui/screens/Screen", "fod", "net/minecraft/class_1009"),
        ("net/minecraft/network/chat/Component", "wz", "net/minecraft/class_1010"),
        ("net/minecraft/server/players/PlayerList", "aur", "net/minecraft/class_1011"),
        // Mojang leaves this one alone, so intermediary has no line for it.
        ("net/minecraft/server/MinecraftServer", "net/minecraft/server/MinecraftServer", null)
    ];

    // owner, official name, obfuscated name, java signature, intermediary name.
    // The version modelled is 1.18.2: getAllowCommands rather than
    // isAllowCommands, and no PublishCommand.getSuccessMessage at all.
    private static readonly (string Owner, string Official, string Obf, string Signature, string Intermediary)[]
        Methods =
        [
            ("net/minecraft/client/server/IntegratedServer", "publishServer", "a",
                "boolean publishServer(net.minecraft.world.level.GameType,boolean,int)", "method_2000"),
            // Declared on Screen, overridden by ShareToLanScreen: the line
            // belongs to Screen and the override has none.
            ("net/minecraft/client/gui/screens/Screen", "init", "aT_", "void init()", "method_2001"),
            ("net/minecraft/server/MinecraftServer", "isPublished", "r", "boolean isPublished()", "method_2002"),
            ("net/minecraft/server/MinecraftServer", "getPlayerList", "ah",
                "net.minecraft.server.players.PlayerList getPlayerList()", "method_2003"),
            ("net/minecraft/server/MinecraftServer", "getDefaultGameType", "u_",
                "net.minecraft.world.level.GameType getDefaultGameType()", "method_2004"),
            ("net/minecraft/server/MinecraftServer", "getWorldData", "bb",
                "net.minecraft.world.level.storage.WorldData getWorldData()", "method_2005"),
            ("net/minecraft/world/level/storage/WorldData", "getAllowCommands", "m",
                "boolean getAllowCommands()", "method_2006"),
            ("net/minecraft/util/HttpUtil", "getAvailablePort", "a", "int getAvailablePort()", "method_2007"),
            ("net/minecraft/client/Minecraft", "getInstance", "Q",
                "net.minecraft.client.Minecraft getInstance()", "method_2008"),
            ("net/minecraft/client/Minecraft", "getSingleplayerServer", "V",
                "net.minecraft.client.server.IntegratedServer getSingleplayerServer()", "method_2009"),
            ("net/minecraft/client/Minecraft", "setScreen", "a",
                "void setScreen(net.minecraft.client.gui.screens.Screen)", "method_2010"),
            ("net/minecraft/client/Minecraft", "updateTitle", "d", "void updateTitle()", "method_2011"),
            ("net/minecraft/client/gui/Gui", "getChat", "d",
                "net.minecraft.client.gui.components.ChatComponent getChat()", "method_2012"),
            ("net/minecraft/client/gui/components/ChatComponent", "addMessage", "a",
                "void addMessage(net.minecraft.network.chat.Component)", "method_2013")
        ];

    private static readonly (string Owner, string Official, string Obf, string Intermediary)[] Fields =
    [
        ("net/minecraft/client/Minecraft", "gui", "l", "field_2100")
    ];

    private (IdentityAdapterMappingService Service, PreparedRuntime Runtime, string GameDirectory)
        CreateFabricFixture(bool withMojangMappings = true)
    {
        var paths = new AppPaths(_root);
        var runtimeRoot = Path.Combine(_root, "Minecraft", "Launcher", "Runtimes", "Fabric");
        var intermediaryDirectory = Path.Combine(
            runtimeRoot, "libraries", "net", "fabricmc", "intermediary", "1.18.2");
        Directory.CreateDirectory(intermediaryDirectory);
        using (var archive = ZipFile.Open(
                   Path.Combine(intermediaryDirectory, "intermediary-1.18.2.jar"), ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("mappings/mappings.tiny");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(Tiny());
        }

        if (withMojangMappings)
        {
            var mojangDirectory = Path.Combine(runtimeRoot, "libraries", "net", "minecraft", "client", "1.18.2");
            Directory.CreateDirectory(mojangDirectory);
            File.WriteAllText(Path.Combine(mojangDirectory, "client-1.18.2-mappings.txt"), Proguard());
        }

        // The jar the game ships, which is where the class hierarchy is read
        // from and where the preflight finds the screen to patch.
        var clientJar = Path.Combine(runtimeRoot, "client.jar");
        using (var archive = ZipFile.Open(clientJar, ZipArchiveMode.Create))
        {
            foreach (var (_, obf, _) in Classes)
            {
                var super = obf == "foe" ? "fod" : "java/lang/Object";
                using var stream = archive.CreateEntry(obf + ".class").Open();
                var bytes = MinimalClass(obf, super);
                stream.Write(bytes, 0, bytes.Length);
            }
            foreach (var name in new[]
                     {
                         "com/mojang/authlib/yggdrasil/YggdrasilMinecraftSessionService",
                         "com/mojang/authlib/GameProfile"
                     })
            {
                archive.CreateEntry(name + ".class");
            }
        }

        var gameDirectory = Path.Combine(_root, "Minecraft", "Personal", "Instances", "Fabric");
        Directory.CreateDirectory(gameDirectory);
        var runtime = new PreparedRuntime(
            runtimeRoot,
            "fabric-profile",
            Path.Combine(runtimeRoot, "java.exe"),
            clientJar,
            new PackRuntimeDescriptor(
                1,
                "1.18.2",
                new PackLoaderDescriptor(PackLoaderKind.Fabric, "0.14.10"),
                "client.jar",
                "fabric-hash"));
        return (new IdentityAdapterMappingService(paths), runtime, gameDirectory);
    }

    private static string Tiny()
    {
        var lines = new List<string> { "v1\tofficial\tintermediary" };
        foreach (var (_, obf, intermediary) in Classes)
        {
            if (intermediary is not null) lines.Add($"CLASS\t{obf}\t{intermediary}");
        }
        foreach (var (owner, _, obf, signature, intermediary) in Methods)
        {
            lines.Add($"METHOD\t{ObfOf(owner)}\t{Descriptor(signature)}\t{obf}\t{intermediary}");
        }
        foreach (var (owner, _, obf, intermediary) in Fields)
        {
            lines.Add($"FIELD\t{ObfOf(owner)}\tI\t{obf}\t{intermediary}");
        }
        return string.Join('\n', lines) + "\n";
    }

    private static string Proguard()
    {
        var lines = new List<string> { "# Fixture in the shape Mojang publishes, which is not their file." };
        foreach (var (official, obf, _) in Classes)
        {
            lines.Add($"{official.Replace('/', '.')} -> {obf}:");
            foreach (var (owner, name, memberObf, signature, _) in Methods)
            {
                // The override is declared where it is overridden, which is what
                // Mojang's file records and intermediary does not.
                var declaredHere = owner == official ||
                    (name == "init" && official == "net/minecraft/client/gui/screens/ShareToLanScreen");
                if (declaredHere) lines.Add($"    12:15:{signature} -> {memberObf}");
            }
            foreach (var (owner, name, memberObf, _) in Fields)
            {
                if (owner == official) lines.Add($"    int {name} -> {memberObf}");
            }
        }
        return string.Join('\n', lines) + "\n";
    }

    private static string ObfOf(string official) =>
        Classes.First(entry => entry.Official == official).Obf;

    /// <summary>The obfuscated descriptor of a signature written Mojang's way.</summary>
    private static string Descriptor(string signature)
    {
        var space = signature.IndexOf(' ');
        var returnType = signature[..space];
        var rest = signature[(space + 1)..];
        var open = rest.IndexOf('(');
        var arguments = rest[(open + 1)..].TrimEnd(')');
        var parameters = arguments.Length == 0
            ? []
            : arguments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return "(" + string.Concat(parameters.Select(TypeOf)) + ")" + TypeOf(returnType);
    }

    private static string TypeOf(string javaType) => javaType switch
    {
        "void" => "V",
        "boolean" => "Z",
        "int" => "I",
        _ => "L" + Classes.First(entry => entry.Official == javaType.Replace('.', '/')).Obf + ";"
    };

    /// <summary>
    /// A class file with nothing in it but its own name and its superclass,
    /// which is all the hierarchy walk reads.
    /// </summary>
    private static byte[] MinimalClass(string name, string superName)
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        void U2(int value)
        {
            writer.Write((byte)(value >> 8));
            writer.Write((byte)(value & 0xFF));
        }
        void Utf8(string value)
        {
            writer.Write((byte)1);
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            U2(bytes.Length);
            writer.Write(bytes);
        }
        writer.Write(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE });
        U2(0);
        U2(52);
        U2(5);
        Utf8(name);
        writer.Write((byte)7);
        U2(1);
        Utf8(superName);
        writer.Write((byte)7);
        U2(3);
        U2(0x21);
        U2(2);
        U2(4);
        U2(0);
        U2(0);
        U2(0);
        U2(0);
        writer.Flush();
        return memory.ToArray();
    }
}
