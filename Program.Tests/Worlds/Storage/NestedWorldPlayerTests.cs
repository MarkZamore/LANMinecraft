using System.IO;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class NestedWorldPlayerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly LocalIdentityContext Alice = TestIdentity.CreateContext("Alice", 76561198000000001);
    private static readonly LocalIdentityContext Bob = TestIdentity.CreateContext("Bob", 76561198000000002);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nested-world-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TempTree.Delete(_root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HandoverChangesOnlyHostPointerAndManifest(bool recipientHasProfile)
    {
        var paths = CreatePaths();
        var world = CreateWorld(paths, Alice.PlayerUuid);
        WriteProfile(world, Alice.PlayerUuid, "alice");
        if (recipientHasProfile) WriteProfile(world, Bob.PlayerUuid, "bob");
        var files = Directory.GetFiles(Path.Combine(world, "players"), "*", SearchOption.AllDirectories);
        var unchanged = files.Append(WorldPlayerStorage.WorldGenSettingsPath(world)).ToDictionary(path => path, File.ReadAllBytes);
        var profiles = new WorldPlayerProfileService(paths);

        profiles.PrepareWorldForOutgoingTransfer(world, Alice);
        Assert.Null(profiles.ReadLevelPlayerUuid(world));
        var outgoing = profiles.ValidatePlayerManifest(world);
        Assert.Equal(WorldPlayerStorage.NestedUuid, outgoing.PlayerStorage);
        Assert.Equal(files.Length, outgoing.Players.Sum(player => player.Files.Count));

        profiles.PrepareReceivedWorldForIdentity(world, Bob);

        Assert.Equal(recipientHasProfile ? Bob.PlayerUuid : (Guid?)null, profiles.ReadLevelPlayerUuid(world));
        Assert.Equal(Bob.MinecraftUuid, profiles.ValidatePlayerManifest(world).CurrentHolderUuid);
        foreach (var (path, bytes) in unchanged) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Null(NbtFile.Read(Path.Combine(world, "level.dat")).Root.GetCompound("Data")!.GetCompound("Player"));
        Assert.False(Directory.Exists(Path.Combine(world, "playerdata")));
    }

    [Fact]
    public void AVerifiedPlayerBackupCanBeSelectedWithoutRewritingIt()
    {
        var paths = CreatePaths();
        var world = CreateWorld(paths, Alice.PlayerUuid);
        var backup = WriteProfile(world, Bob.PlayerUuid, "backup", backupOnly: true);
        var before = File.ReadAllBytes(backup);
        var profiles = new WorldPlayerProfileService(paths);

        profiles.PrepareReceivedWorldForIdentity(world, Bob);

        Assert.Equal(Bob.PlayerUuid, profiles.ReadLevelPlayerUuid(world));
        Assert.Equal(before, File.ReadAllBytes(backup));
        Assert.False(File.Exists(backup[..^4]));
    }

    [Fact]
    public void ManifestChecksNestedStatsAndRejectsAnAliasedPath()
    {
        var paths = CreatePaths();
        var world = CreateWorld(paths, Alice.PlayerUuid);
        WriteProfile(world, Alice.PlayerUuid, "alice");
        var profiles = new WorldPlayerProfileService(paths);
        profiles.PrepareWorldForOutgoingTransfer(world, Alice);
        var manifest = profiles.ValidatePlayerManifest(world);
        var stats = Assert.Single(Assert.Single(manifest.Players).Files, file => file.Path.StartsWith("players/stats/"));
        var path = Path.Combine(world, stats.Path);
        File.AppendAllText(path, " ");
        Assert.Throws<InvalidDataException>(() => profiles.ValidatePlayerManifest(world));
        File.WriteAllText(path, "alice");
        stats.Path = stats.Path.Replace("players/stats/", "players/data/../stats/", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(world, WorldPlayerManifestService.ManifestFileName), JsonSerializer.Serialize(manifest, Json));

        Assert.Throws<InvalidDataException>(() => profiles.ValidatePlayerManifest(world));
    }

    [Fact]
    public void AnotherPlayersMisnamedProfileRollsBackThePointerAndItsBackup()
    {
        var paths = CreatePaths();
        var world = CreateWorld(paths, Alice.PlayerUuid);
        WriteProfile(world, Alice.PlayerUuid, "alice");
        var wrongPath = WriteProfile(world, Bob.PlayerUuid, "bob");
        var bad = NbtFile.Read(wrongPath);
        bad.Root.SetUuid(Alice.PlayerUuid);
        bad.Write(wrongPath);
        var levelPath = Path.Combine(world, "level.dat");
        var launcherBackup = Path.Combine(world, NbtFile.LauncherBackupFileName);
        File.WriteAllText(launcherBackup, "previous launcher backup");
        var before = File.ReadAllBytes(levelPath);

        Assert.Throws<InvalidDataException>(() => new WorldPlayerProfileService(paths).PrepareWorldForOutgoingTransfer(world, Alice));

        Assert.Equal(before, File.ReadAllBytes(levelPath));
        Assert.Equal("previous launcher backup", File.ReadAllText(launcherBackup));
        Assert.False(File.Exists(Path.Combine(world, WorldPlayerManifestService.ManifestFileName)));
    }

    [Fact]
    public void APreviouslyStampedJava26WorldUsesTheSharedNewLayout()
    {
        var paths = CreatePaths();
        var world = CreateWorld(paths, Alice.PlayerUuid);
        WriteProfile(world, Alice.PlayerUuid, "alice");
        var metadataPath = Path.Combine(world, WorldMetadataService.MetadataFileName);
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(new WorldMetadata
        {
            WorldId = Guid.NewGuid().ToString("D"), BuildRelativePath = "26-series", MinecraftVersion = "26.2",
            PlayerStorage = WorldPlayerStorage.Uuid
        }, Json));
        var descriptor = new PackRuntimeDescriptor(1, "26.3", new(PackLoaderKind.NeoForge, "26.3.0.99"), null, "test");
        var profiles = new WorldPlayerProfileService(paths);

        profiles.PrepareWorldsForLaunch(paths.Worlds, Alice, "26-series", descriptor);

        var metadata = new WorldMetadataService().Read(world)!;
        Assert.Equal(WorldPlayerStorage.NestedUuid, metadata.PlayerStorage);
        Assert.Equal("26.3", metadata.MinecraftVersion);
        Assert.Equal(Alice.PlayerUuid, profiles.ReadLevelPlayerUuid(world));
    }

    [Fact]
    public void MissingNamespacedWorldGenSettingsDoesNotSpendBackups()
    {
        var paths = CreatePaths();
        var world = CreateWorld(paths, Alice.PlayerUuid);
        File.Delete(WorldPlayerStorage.WorldGenSettingsPath(world));
        File.WriteAllText(Path.Combine(world, WorldMetadataService.MetadataFileName), JsonSerializer.Serialize(new WorldMetadata
        {
            WorldId = Guid.NewGuid().ToString("D"), BuildRelativePath = "26-series", MinecraftVersion = "26.3",
            PlayerStorage = WorldPlayerStorage.NestedUuid
        }, Json));
        var levelPath = Path.Combine(world, "level.dat");
        var before = File.ReadAllBytes(levelPath);

        Assert.Throws<InvalidDataException>(() => new WorldPlayerProfileService(paths).PrepareReceivedWorldForIdentity(world, Bob));

        Assert.Equal(before, File.ReadAllBytes(levelPath));
        Assert.False(File.Exists(Path.Combine(world, NbtFile.LauncherBackupFileName)));
    }

    [Fact]
    public void AProfileDirectoryLinkCannotReachOutsideTheWorld()
    {
        var paths = CreatePaths();
        var world = CreateWorld(paths, Alice.PlayerUuid);
        var outside = Path.Combine(paths.Root, "unrelated");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, $"{Bob.PlayerUuid:D}.dat"), "untouched");
        Directory.CreateDirectory(Path.Combine(world, "players"));
        var junction = Path.Combine(world, "players", "data");
        SavesFolderService.CreateJunction(junction, outside);
        try
        {
            Assert.Throws<InvalidDataException>(() => new WorldPlayerProfileService(paths).PrepareReceivedWorldForIdentity(world, Bob));
            Assert.Equal("untouched", File.ReadAllText(Path.Combine(outside, $"{Bob.PlayerUuid:D}.dat")));
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public void AWorldLockPreventsHandoverUntilItIsReleased()
    {
        var paths = CreatePaths();
        var world = CreateWorld(paths, Alice.PlayerUuid);
        File.WriteAllBytes(Path.Combine(world, "session.lock"), [0x26, 0x03]);
        var profiles = new WorldPlayerProfileService(paths);

        using (WorldAccessGuard.LockClosed(world))
        {
            Assert.Throws<InvalidOperationException>(() => profiles.PrepareReceivedWorldForIdentity(world, Bob));
            Assert.Equal(Alice.PlayerUuid, profiles.ReadLevelPlayerUuid(world));
        }

        profiles.PrepareReceivedWorldForIdentity(world, Bob);
        Assert.Null(profiles.ReadLevelPlayerUuid(world));
    }

    private AppPaths CreatePaths()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        return paths;
    }

    private static string CreateWorld(AppPaths paths, Guid host)
    {
        // Minecraft 26.1+ and Horizon 26.3 use an IntArray pointer and a separate settings file.
        var world = Path.Combine(paths.Worlds, "26-series", "test");
        var data = new NbtCompoundTag();
        data.Set("singleplayer_uuid", NbtIntArrayTag.FromUuid(host));
        var level = new NbtCompoundTag();
        level.Set("Data", data);
        new NbtFile("", level).Write(Path.Combine(world, "level.dat"));
        var settings = new NbtCompoundTag();
        settings.Set("seed", new NbtLongTag(8743751));
        var settingsRoot = new NbtCompoundTag();
        settingsRoot.Set("data", settings);
        new NbtFile("", settingsRoot).Write(WorldPlayerStorage.WorldGenSettingsPath(world));
        return world;
    }

    private static string WriteProfile(string world, Guid uuid, string value, bool backupOnly = false)
    {
        var player = new NbtCompoundTag();
        player.SetUuid(uuid);
        player.Set("InventoryFixture", new NbtStringTag(value));
        var path = Path.Combine(world, "players", "data", $"{uuid:D}.dat" + (backupOnly ? "_old" : ""));
        new NbtFile("", player).Write(path);
        foreach (var subfolder in new[] { "stats", "advancements" })
        {
            var directory = Path.Combine(world, "players", subfolder);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"{uuid:D}.json"), value);
        }
        return path;
    }
}
