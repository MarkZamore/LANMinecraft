using System.IO.Compression;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Giving each guest the chunks he asked for, where the game cannot.
///
/// From 1.20.2 the server keeps ServerPlayer.requestedViewDistance and gives
/// every player the smaller of his number and the server's, so raising the
/// server's is the whole feature and the adapter must keep its hands off.
/// Before that there is one number for everybody, and the patch rewrites where
/// it is read. Which of the two a runtime is, is read out of its own mappings:
/// the presence of that method, and the name of the field, both changed in the
/// same release.
/// </summary>
public sealed class IdentityAdapterPerPlayerChunksTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-per-player-{Guid.NewGuid():N}");

    public void Dispose()
    {
        TempTree.Delete(_root);
    }

    // Excerpt of the same shape as the golden one, with the obfuscated names
    // Mojang published for 1.20.1: a version whose server keeps no per-player
    // number, so the patch belongs there.
    private static readonly string OldEnoughMappings = string.Join('\n', new[]
    {
        "tsrg2 obf mojmap",
        "ahr net/minecraft/server/level/ChunkMap",
        "\tO viewDistance",
        "\ta (Laig;)V move",
        "\ta (Laig;Z)V updatePlayerStatus",
        // The delivery those two do not cover.
        "\ta (Laig;Lorg/apache/commons/lang3/mutable/MutableObject;Ldei;)V playerLoadedChunk",
        // The setter the scale of the field is read from, and the only (I)V
        // this class has.
        "\ta (I)V setViewDistance",
        "ahr$b net/minecraft/server/level/ChunkMap$TrackedEntity",
        // Two methods of one obfuscated name and one descriptor, told apart by
        // nothing but the name Mojang published. Patch the wrong one and a
        // player stops being told about every entity he can see.
        "\ta (Laig;)V removePlayer",
        "\tb (Laig;)V updatePlayer",
        "aig net/minecraft/server/level/ServerPlayer",
        "\ta (Lzl;)V updateOptions",
        "\tx ()Laif; serverLevel",
        "zl net/minecraft/network/protocol/game/ServerboundClientInformationPacket",
        "\tc ()I viewDistance",
        "bfj net/minecraft/world/entity/Entity",
        "\tct ()Ljava/util/UUID; getUUID",
        "\tdk ()Lclt; chunkPosition",
        // What the serve distance itself needs, on the same version.
        "aif net/minecraft/server/level/ServerLevel",
        "\tk ()Laid; getChunkSource",
        "aid net/minecraft/server/level/ServerChunkCache",
        "\ta (I)V setViewDistance",
        "\ta chunkMap",
        "net/minecraft/server/MinecraftServer net/minecraft/server/MinecraftServer",
        "\tF ()Ljava/lang/Iterable; getAllLevels",
        "alk net/minecraft/server/players/PlayerList",
        "\tt ()Ljava/util/List; getPlayers",
        "\ta (Luo;)V broadcastAll",
        "uo net/minecraft/network/protocol/Packet",
        "xt net/minecraft/network/protocol/game/ClientboundSetChunkCacheRadiusPacket",
        "clt net/minecraft/world/level/ChunkPos",
        "\te x",
        "\tf z",
        "ddx net/minecraft/world/level/chunk/ChunkAccess",
        "\tf ()Lclt; getPos",
        "dei net/minecraft/world/level/chunk/LevelChunk"
    });

    // The same classes on 1.21.1, where the game does it itself: the field is
    // renamed and ServerPlayer answers requestedViewDistance.
    private static readonly string NewEnoughMappings = string.Join('\n', new[]
    {
        "tsrg2 obf mojmap",
        "aqb net/minecraft/server/level/ChunkMap",
        "\tO serverViewDistance",
        // Renamed in the same release that made the distance per player.
        "\ta (I)V setServerViewDistance",
        "\ta (Laqv;)V move",
        "\ta (Laqv;Z)V updatePlayerStatus",
        "aqb$b net/minecraft/server/level/ChunkMap$TrackedEntity",
        "\ta (Laqv;)V removePlayer",
        "\tb (Laqv;)V updatePlayer",
        "aqv net/minecraft/server/level/ServerPlayer",
        "\tF ()I requestedViewDistance",
        "\tA ()Laqu; serverLevel",
        "\ta (Laqh;)V updateOptions",
        // 1.21.1 hands updateOptions a settings object, and the packet itself
        // moved to another package in the same release that gave the server a
        // number per player. Naming both the way this version really does is
        // what makes the switch-off below mean the thing it says.
        "aqh net/minecraft/server/level/ClientInformation",
        "\tc ()I viewDistance",
        "aaa net/minecraft/network/protocol/common/ServerboundClientInformationPacket",
        "bsr net/minecraft/world/entity/Entity",
        "\tcz ()Ljava/util/UUID; getUUID",
        "\tdq ()Ldcd; chunkPosition",
        "aqu net/minecraft/server/level/ServerLevel",
        "\tl ()Laqs; getChunkSource",
        "aqs net/minecraft/server/level/ServerChunkCache",
        "\ta (I)V setViewDistance",
        "\ta chunkMap",
        "net/minecraft/server/MinecraftServer net/minecraft/server/MinecraftServer",
        "\tK ()Ljava/lang/Iterable; getAllLevels",
        "aur net/minecraft/server/players/PlayerList",
        "\tt ()Ljava/util/List; getPlayers",
        "\ta (Lzg;)V broadcastAll",
        "zg net/minecraft/network/protocol/Packet",
        "aew net/minecraft/network/protocol/game/ClientboundSetChunkCacheRadiusPacket",
        "dcd net/minecraft/world/level/ChunkPos",
        "\te x",
        "\tf z",
        "duy net/minecraft/world/level/chunk/ChunkAccess",
        "\tf ()Ldcd; getPos",
        "dvi net/minecraft/world/level/chunk/LevelChunk"
    });

    /// <summary>
    /// Two names this feature reaches for were spelled differently on the older
    /// half of its own range: the packet only became a record at 1.18, and a
    /// player's world was getLevel until 1.20.1. Both mean what they meant.
    /// </summary>
    private static readonly string BeforeTheRenamesMappings = OldEnoughMappings
        .Replace("\tc ()I viewDistance", "\tc ()I getViewDistance", StringComparison.Ordinal)
        .Replace("\tx ()Laif; serverLevel", "\tx ()Laif; getLevel", StringComparison.Ordinal);

    /// <summary>
    /// The same 1.20.1 excerpt, but answering requestedViewDistance.
    ///
    /// 1.21.1 is turned away for a second reason as well - its settings packet
    /// lives in another package - so on its own it cannot say which of the two
    /// the decision rests on. This one names everything the patch needs and is
    /// refused by the guard alone.
    /// </summary>
    private static readonly string DoesItItselfMappings = OldEnoughMappings.Replace(
        "aig net/minecraft/server/level/ServerPlayer",
        "aig net/minecraft/server/level/ServerPlayer\n\tF ()I requestedViewDistance",
        StringComparison.Ordinal);

    [Fact]
    public void AMinecraftThatCannotDoItItself_GetsThePatch()
    {
        var properties = Build(OldEnoughMappings);

        Assert.Equal("true", properties["perPlayerChunksEnabled"]);
        Assert.Equal("net/minecraft/server/level/ChunkMap,ahr", properties["chunkMapClasses"]);
        // The two methods that are handed a player and then read the number.
        Assert.Equal("updatePlayerStatus,a", properties["updatePlayerStatusMethods"]);
        Assert.Equal("move,a", properties["movePlayerMethods"]);
        Assert.Equal("viewDistance,O", properties["chunkViewDistanceFields"]);
        // And the delivery the two of them do not cover.
        Assert.Equal("playerLoadedChunk,a", properties["playerLoadedChunkMethods"]);
        Assert.Equal("getPos,f", properties["chunkGetPosMethods"]);
        // And the way back from a player to the map that tracks him.
        Assert.Equal("serverLevel,x", properties["serverLevelMethods"]);
        Assert.Equal("chunkMap,a", properties["chunkMapFields"]);
        // And the entity tracking, which caps itself with the same number.
        Assert.Equal(
            "net/minecraft/server/level/ChunkMap$TrackedEntity,ahr$b",
            properties["trackedEntityClasses"]);
        Assert.Equal("updatePlayer,b", properties["updatePlayerMethods"]);
        // And the setter, which is where the scale of the field is learnt.
        Assert.Equal("setViewDistance,a", properties["chunkSetViewDistanceMethods"]);
        Assert.Equal("(I)V", properties["chunkSetViewDistanceDescriptors"]);
        // A name alone does not say which method on an obfuscated ServerPlayer,
        // where twenty-two of them are one-argument voids called "a".
        Assert.Equal(
            "(Lnet/minecraft/server/level/ServerPlayer;Z)V,(Laig;Z)V",
            properties["updatePlayerStatusDescriptors"]);
        Assert.Equal(
            "(Lnet/minecraft/server/level/ServerPlayer;)V,(Laig;)V",
            properties["movePlayerDescriptors"]);
        Assert.Equal(
            "(Lnet/minecraft/network/protocol/game/ServerboundClientInformationPacket;)V,(Lzl;)V",
            properties["updateOptionsDescriptors"]);
    }

    /// <summary>
    /// And the serve distance comes with it: the ceiling is worked out from the
    /// numbers the adapter wrote down, because the server keeps none.
    /// </summary>
    [Fact]
    public void AMinecraftThatCannotDoItItself_IsStillServedFurther()
    {
        var properties = Build(OldEnoughMappings);

        // The ceiling is still raised, and what each player asked for is not
        // the server's to answer: there is no such method to name.
        Assert.Equal("setViewDistance,a", properties["setChunkViewDistanceMethods"]);
        Assert.Equal("getAllLevels,F", properties["getAllLevelsMethods"]);
        Assert.False(properties.ContainsKey("requestedViewDistanceMethods"));
    }

    [Fact]
    public void AMinecraftThatDoesItItself_IsLeftAlone()
    {
        var properties = Build(NewEnoughMappings);

        Assert.Equal("false", properties["perPlayerChunksEnabled"]);
        Assert.False(properties.ContainsKey("updatePlayerStatusMethods"));
        // The chunk map is still named, and in both spellings, because one seam
        // does go in here: the launcher has to hear when the host narrows the
        // world. What is not named is ServerPlayer or the entity tracker, since
        // the preflight refuses a class it was told to patch and could not.
        Assert.Equal("net/minecraft/server/level/ChunkMap,aqb", properties["chunkMapClasses"]);
    }

    [Fact]
    public void AMinecraftThatNamesEverythingAndStillDoesItItself_IsLeftAlone()
    {
        var properties = Build(DoesItItselfMappings);

        Assert.Equal("false", properties["perPlayerChunksEnabled"]);
        Assert.False(properties.ContainsKey("updatePlayerStatusMethods"));
    }

    /// <summary>
    /// The seam that watches the setter is not part of the narrowing and does
    /// not go away with it. The launcher writes the server's distance behind
    /// PlayerList's back on every version, so on every version it needs to hear
    /// when somebody else writes it instead.
    /// </summary>
    [Fact]
    public void AMinecraftThatDoesItItself_StillWatchesTheSetter()
    {
        var properties = Build(NewEnoughMappings);

        Assert.Equal("false", properties["perPlayerChunksEnabled"]);
        Assert.Equal("setServerViewDistance,a", properties["chunkSetViewDistanceMethods"]);
        Assert.Equal("(I)V", properties["chunkSetViewDistanceDescriptors"]);
        // And the class is named in the spelling the runtime loads, or the
        // transformer would never be handed it.
        Assert.Equal("net/minecraft/server/level/ChunkMap,aqb", properties["chunkMapClasses"]);
    }

    [Fact]
    public void AMinecraftFromBeforeTheRenames_IsStillUnderstood()
    {
        var properties = Build(BeforeTheRenamesMappings);

        Assert.Equal("true", properties["perPlayerChunksEnabled"]);
        Assert.Equal("getViewDistance,c", properties["clientViewDistanceMethods"]);
        // Without this the whole feature would have stayed off on every version
        // before 1.20.1 - All The Fabric 3 among them.
        Assert.Equal("getLevel,x", properties["serverLevelMethods"]);
    }

    /// <summary>
    /// The whole feature reads the mappings and nothing else; the rest of the
    /// adapter is not what this asks about, so the fixture carries only what
    /// this feature names and the builder is called directly.
    /// </summary>
    private Dictionary<string, string> Build(string mappings)
    {
        var runtimeRoot = Path.Combine(_root, "runtime");
        var mappingDirectory = Path.Combine(runtimeRoot, "libraries", "neoform");
        Directory.CreateDirectory(mappingDirectory);
        var mappingPath = Path.Combine(mappingDirectory, "neoform-test-mappings-merged.txt");
        File.WriteAllText(mappingPath, mappings);

        var type = typeof(IdentityAdapterMappingService);
        const System.Reflection.BindingFlags Hidden =
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public;
        var names = type.GetNestedType("RuntimeNames", Hidden)!;
        var book = names.GetMethod("Read", Hidden)!.Invoke(null, [mappingPath, null]);

        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        type.GetMethod("AddPerPlayerChunksProperties", Hidden)!.Invoke(null, [book, properties]);
        type.GetMethod("AddServeDistanceProperties", Hidden)!.Invoke(null, [book, properties]);
        return properties;
    }
}
