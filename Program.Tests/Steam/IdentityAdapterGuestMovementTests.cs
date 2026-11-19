using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Letting a guest keep the ground he says he is standing on.
///
/// Minecraft refuses a movement packet that steps further than a player could
/// have, and puts him back where he was. That check stops a flying cheat on a
/// public server; on a world two friends opened to each other it stops nothing
/// and turns half a second of a bad relay into a visible jerk backwards. The
/// game already exempts whoever opened the world, and this widens the exemption
/// to everybody in one - in the two movement handlers only, because the same
/// question also decides who may change the world's difficulty.
/// </summary>
public sealed class IdentityAdapterGuestMovementTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-guest-movement-{Guid.NewGuid():N}");

    public void Dispose()
    {
        TempTree.Delete(_root);
    }

    /// <summary>
    /// 1.20.1, where the question is answered by the listener itself. Two
    /// handlers of one obfuscated name told apart by the packet they take.
    /// </summary>
    private static readonly string OnTheListenerMappings = string.Join('\n', new[]
    {
        "tsrg2 obf mojmap",
        "aiy net/minecraft/server/network/ServerGamePacketListenerImpl",
        "\tg ()Z isSingleplayerOwner",
        "\ta (Lvz;)V handleMovePlayer",
        "\ta (Lwa;)V handleMoveVehicle",
        // A third method of the same name, so a descriptor is what tells them
        // apart and a name alone is proved not to be enough.
        "\ta (Lwb;)V handleChangeDifficulty",
        "vz net/minecraft/network/protocol/game/ServerboundMovePlayerPacket",
        "wa net/minecraft/network/protocol/game/ServerboundMoveVehiclePacket",
        "wb net/minecraft/network/protocol/game/ServerboundChangeDifficultyPacket"
    });

    /// <summary>
    /// 1.21.1, where 1.20.2 moved the question onto a parent shared with the
    /// configuration phase. The handlers stay on the listener.
    /// </summary>
    private static readonly string OnTheParentMappings = string.Join('\n', new[]
    {
        "tsrg2 obf mojmap",
        "aru net/minecraft/server/network/ServerGamePacketListenerImpl",
        "\ta (Lacj;)V handleMovePlayer",
        "\ta (Lack;)V handleMoveVehicle",
        "arr net/minecraft/server/network/ServerCommonPacketListenerImpl",
        "\th ()Z isSingleplayerOwner",
        "acj net/minecraft/network/protocol/game/ServerboundMovePlayerPacket",
        "ack net/minecraft/network/protocol/game/ServerboundMoveVehiclePacket"
    });

    [Fact]
    public void WhereTheListenerAnswersItself_BothHandlersAreNamed()
    {
        var (properties, trusted) = Build(OnTheListenerMappings);

        Assert.True(trusted);
        Assert.Equal("true", properties["guestMovementEnabled"]);
        Assert.Equal(
            "net/minecraft/server/network/ServerGamePacketListenerImpl,aiy",
            properties["movementListenerClasses"]);
        Assert.Equal("handleMovePlayer,a", properties["handleMovePlayerMethods"]);
        Assert.Equal("handleMoveVehicle,a", properties["handleMoveVehicleMethods"]);
        Assert.Equal("isSingleplayerOwner,g", properties["singleplayerOwnerMethods"]);
        // A name alone would answer three methods on this class, one of them
        // the difficulty of the world.
        Assert.Equal(
            "(Lnet/minecraft/network/protocol/game/ServerboundMovePlayerPacket;)V,(Lvz;)V",
            properties["handleMovePlayerDescriptors"]);
        Assert.Equal(
            "(Lnet/minecraft/network/protocol/game/ServerboundMoveVehiclePacket;)V,(Lwa;)V",
            properties["handleMoveVehicleDescriptors"]);
    }

    /// <summary>
    /// A call to an inherited method carries the name of the class it was
    /// called on, not the one that declares it. Naming only the parent left the
    /// instruction unmatched, the class unchanged, and the preflight refusing a
    /// target it had been given - which takes the whole adapter down.
    /// </summary>
    [Fact]
    public void WhereAParentAnswers_BothClassesAreNamedAsTheOwner()
    {
        var (properties, trusted) = Build(OnTheParentMappings);

        Assert.True(trusted);
        Assert.Equal("isSingleplayerOwner,h", properties["singleplayerOwnerMethods"]);
        Assert.Equal(
            "net/minecraft/server/network/ServerGamePacketListenerImpl,aru," +
            "net/minecraft/server/network/ServerCommonPacketListenerImpl,arr",
            properties["singleplayerOwnerClasses"]);
    }

    /// <summary>
    /// A runtime missing any one of the names switches the whole thing off and
    /// leaves nothing behind: a target the transformer will not change fails
    /// the preflight on byte equality, and the adapter is lost with it.
    /// </summary>
    [Fact]
    public void AMinecraftMissingTheQuestion_IsLeftAlone()
    {
        var without = OnTheListenerMappings.Replace(
            "\tg ()Z isSingleplayerOwner\n", "", StringComparison.Ordinal);

        var (properties, trusted) = Build(without);

        Assert.False(trusted);
        Assert.Equal("false", properties["guestMovementEnabled"]);
    }

    /// <summary>
    /// The preflight throws on a list it was not given, and the throw costs the
    /// skins too. Every one of these is written before anything can return.
    /// </summary>
    [Theory]
    [InlineData("guestMovementEnabled")]
    [InlineData("movementListenerClasses")]
    [InlineData("handleMovePlayerMethods")]
    [InlineData("handleMovePlayerDescriptors")]
    [InlineData("handleMoveVehicleMethods")]
    [InlineData("handleMoveVehicleDescriptors")]
    [InlineData("singleplayerOwnerMethods")]
    [InlineData("singleplayerOwnerClasses")]
    public void EveryListIsNamed_EvenWithNothingToReadThemFrom(string property)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        Trust(null, properties);

        Assert.False(string.IsNullOrWhiteSpace(properties.GetValueOrDefault(property)), property);
    }

    private const System.Reflection.BindingFlags Hidden =
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic |
        System.Reflection.BindingFlags.Public;

    private (Dictionary<string, string> Properties, bool Trusted) Build(string mappings)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        return (properties, Trust(Read(mappings), properties));
    }

    private static bool Trust(object? book, Dictionary<string, string> properties) =>
        (bool)typeof(IdentityAdapterMappingService)
            .GetMethod("AddGuestMovementProperties", Hidden)!
            .Invoke(null, [book, properties])!;

    private object? Read(string mappings)
    {
        var mappingDirectory = Path.Combine(_root, "runtime", "libraries", "neoform");
        Directory.CreateDirectory(mappingDirectory);
        var mappingPath = Path.Combine(mappingDirectory, "neoform-test-mappings-merged.txt");
        File.WriteAllText(mappingPath, mappings);

        var names = typeof(IdentityAdapterMappingService).GetNestedType("RuntimeNames", Hidden)!;
        return names.GetMethod("Read", Hidden)!.Invoke(null, [mappingPath, null]);
    }
}
