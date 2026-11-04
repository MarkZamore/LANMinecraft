using System.IO;
using Minecraft;

namespace Minecraft.Tests;

public sealed class WorldLaunchSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "world-launch-session-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void AccountAndBuildChangesDuringPlay_DoNotAttributeTheSavedHostToTheCurrentUiAccount()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var a = TestIdentity.CreateContext("Alice", 76561198000000001);
        var b = TestIdentity.CreateContext("Bob", 76561198000000002);
        var identityUsedForLaunch = a;
        var settings = new AppSettings { ClientRelativePath = "Legacy A", PlayerName = "Alice" };
        var runtime = new PackRuntimeDescriptor(1, "1.6.4", new(PackLoaderKind.LegacyFabric, "0.18.2"), "client.jar", "original-runtime");
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        var session = WorldLaunchSession.Capture(paths.CombineUnderPacks(settings.ClientRelativePath),
            settings.ClientRelativePath, paths.CombineUnderInstances(settings.ClientRelativePath), "pack-a", runtime,
            identityUsedForLaunch, started);

        // The UI can bind another Steam account while the first JVM remains alive.
        settings.ClientRelativePath = "Modern B";
        settings.PlayerName = "Bob";
        identityUsedForLaunch.IdentityName = b.IdentityName;
        identityUsedForLaunch.MinecraftUuid = b.MinecraftUuid;
        identityUsedForLaunch.SteamId64 = b.SteamId64;
        identityUsedForLaunch.PlayerUuid = b.PlayerUuid;
        runtime = runtime with { MinecraftVersion = "1.21.1" };

        var world = Path.Combine(WorldLocations.ForBuild(paths.Worlds, "Legacy A"), "saved-by-a");
        Directory.CreateDirectory(Path.Combine(world, "players"));
        WriteLevel(world, 17);
        WritePlayer(Profile(world, b), 39);
        File.WriteAllText(Path.Combine(world, "session.lock"), "closed-session");

        var stamped = WorldLaunchSession.StampClosedWorlds(paths, session);

        Assert.Single(stamped);
        var metadata = new WorldMetadataService().Read(world)!;
        var originalA = TestIdentity.CreateContext("Alice", 76561198000000001);
        Assert.Equal(originalA.SteamId64.ToString(), metadata.LegacyHostSteamId64);
        Assert.Equal(originalA.MinecraftUuid, metadata.OwnerIdentityId);
        Assert.Equal("Alice", metadata.OwnerIdentityName);
        Assert.Equal("Legacy A", metadata.BuildRelativePath);
        Assert.Equal("1.6.4", metadata.MinecraftVersion);
        Assert.Equal("pack-a", metadata.PackHash);

        // A subsequent transfer requested by the now-current B account must
        // export A's Data.Player under A, leaving B's existing character alone.
        new WorldPlayerProfileService(paths).PrepareWorldForOutgoingTransfer(world, b);
        Assert.Equal(17, NbtFile.Read(Profile(world, originalA)).Root.GetInt("XpLevel"));
        Assert.Equal(39, NbtFile.Read(Profile(world, b)).Root.GetInt("XpLevel"));
    }

    [Fact]
    public void AnAdoptedProcessWithoutLaunchIdentity_DoesNotAssignItsHostToTheCurrentAccount()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var world = Path.Combine(WorldLocations.ForBuild(paths.Worlds, "Legacy"), "adopted");
        Directory.CreateDirectory(world);
        WriteLevel(world, 23);
        File.WriteAllText(Path.Combine(world, "session.lock"), "closed-session");
        var before = File.ReadAllBytes(Path.Combine(world, "level.dat"));

        var stamped = WorldLaunchSession.StampClosedWorlds(paths, session: null);

        Assert.Empty(stamped);
        Assert.False(File.Exists(Path.Combine(world, WorldMetadataService.MetadataFileName)));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(world, "level.dat")));
    }

    private static string Profile(string world, LocalIdentityContext player) =>
        Path.Combine(world, "players", LegacyPlayerIdentity.NativeName(player.SteamId64) + ".dat");

    private static void WritePlayer(string path, int level)
    {
        var player = new NbtCompoundTag();
        player.Set("XpLevel", new NbtIntTag(level));
        new NbtFile("", player).Write(path);
    }

    private static void WriteLevel(string world, int level)
    {
        var player = new NbtCompoundTag();
        player.Set("XpLevel", new NbtIntTag(level));
        var data = new NbtCompoundTag();
        data.Set("Player", player);
        var root = new NbtCompoundTag();
        root.Set("Data", data);
        new NbtFile("", root).Write(Path.Combine(world, "level.dat"));
    }
}
