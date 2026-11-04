using System.IO;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class LegacyWorldPlayerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "legacy-world-profiles-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static LocalIdentityContext PlayerA(string name = "Alice") => TestIdentity.CreateContext(name, 76561198000000001);
    private static LocalIdentityContext PlayerB(string name = "Bob") => TestIdentity.CreateContext(name, 76561198000000002);
    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void LegacyHostNicknameAndHostSwap_PreserveBothCharactersAndNameOwnedData()
    {
        var pathsA = Paths("a");
        var pathsB = Paths("b");
        var a = PlayerA();
        var b = PlayerB();
        var world = LegacyWorld(pathsA, "shared", a);
        WriteLevel(world, Character(17, "soul-forged-gear"));
        File.WriteAllText(Path.Combine(world, "pet-owner.fixture"), LegacyPlayerIdentity.NativeName(a.SteamId64));
        var profilesA = new WorldPlayerProfileService(pathsA);
        profilesA.PrepareWorldForLaunch(world, a);
        profilesA.PrepareWorldForLaunch(world, PlayerA("RenamedAlice"));
        Assert.Equal(17, LevelPlayer(world)!.GetInt("XpLevel"));
        Assert.Null(LevelPlayer(world)!.GetUuid());
        Assert.False(Directory.Exists(Path.Combine(world, "playerdata")));

        var outgoingA = CopyWorld(world, Path.Combine(pathsA.Root, "staging-a"));
        profilesA.PrepareWorldForOutgoingTransfer(outgoingA, a);
        Assert.Null(LevelPlayer(outgoingA));
        // Outgoing preparation is intentionally applied to the snapshot, not the live source.
        Assert.Equal(17, LevelPlayer(world)!.GetInt("XpLevel"));
        var receivedB = CopyWorld(outgoingA, Path.Combine(pathsB.Worlds, "legacy", "shared"));
        var profilesB = new WorldPlayerProfileService(pathsB);
        profilesB.PrepareReceivedWorldForIdentity(receivedB, b);
        Assert.Null(LevelPlayer(receivedB));
        Assert.Equal(17, NbtFile.Read(Profile(receivedB, a)).Root.GetInt("XpLevel"));
        WriteLevel(receivedB, Character(31, "nightmare-relic"));

        var outgoingB = CopyWorld(receivedB, Path.Combine(pathsB.Root, "staging-b"));
        profilesB.PrepareWorldForOutgoingTransfer(outgoingB, b);
        Assert.Equal(31, NbtFile.Read(Profile(outgoingB, b)).Root.GetInt("XpLevel"));
        var returnedA = CopyWorld(outgoingB, Path.Combine(pathsA.Worlds, "legacy", "returned"));
        profilesA.PrepareReceivedWorldForIdentity(returnedA, PlayerA("AliceAgain"));
        Assert.Equal(17, LevelPlayer(returnedA)!.GetInt("XpLevel"));
        Assert.Equal("soul-forged-gear", LevelPlayer(returnedA)!.GetCompound("InventoryFixture")!.GetString("id"));
        Assert.Equal(31, NbtFile.Read(Profile(returnedA, b)).Root.GetInt("XpLevel"));
        Assert.Equal(LegacyPlayerIdentity.NativeName(a.SteamId64), File.ReadAllText(Path.Combine(returnedA, "pet-owner.fixture")));
        Assert.Equal(2, profilesA.ValidatePlayerManifest(returnedA).Players.Count);
    }

    [Fact]
    public void UnknownLegacyHostAndOldNamedProfile_ArePreservedWithoutAssigningTheirInventory()
    {
        var paths = Paths("unknown");
        var a = PlayerA();
        var world = LegacyWorld(paths, "imported", holder: null);
        WriteLevel(world, Character(52, "unknown-owner"));
        Directory.CreateDirectory(Path.Combine(world, "players"));
        new NbtFile("", Character(61, "old-nickname")).Write(Path.Combine(world, "players", "OldNick.dat"));
        var before = File.ReadAllBytes(Path.Combine(world, "players", "OldNick.dat"));
        var service = new WorldPlayerProfileService(paths);
        service.PrepareWorldForLaunch(world, a);

        Assert.Null(LevelPlayer(world));
        Assert.False(File.Exists(Profile(world, a)));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(world, "players", "OldNick.dat")));
        var manifest = service.ValidatePlayerManifest(world);
        Assert.Empty(manifest.Players);
        Assert.Equal(2, manifest.UnassignedFiles.Count);
        Assert.Contains(manifest.UnassignedFiles, file => file.Path.StartsWith("players/unassigned-", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingUuidWithoutLegacyPlatformEvidence_IsStillRejectedAsCorruption()
    {
        var paths = Paths("modern");
        var world = Path.Combine(paths.Worlds, "damaged-modern");
        Directory.CreateDirectory(world);
        WriteLevel(world, Character(9, "untouched"));
        var before = File.ReadAllBytes(Path.Combine(world, "level.dat"));

        Assert.Throws<InvalidDataException>(() => new WorldPlayerProfileService(paths).PrepareWorldForLaunch(world, PlayerA()));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(world, "level.dat")));
    }

    [Fact]
    public void ForwardingAReceivedWorldBeforePlaying_ExportsTheImportedHolderRatherThanThePreviousHost()
    {
        var paths = Paths("forward");
        var a = PlayerA();
        var b = PlayerB();
        var world = LegacyWorld(paths, "world", a);
        WriteLevel(world, Character(17, "a"));
        Directory.CreateDirectory(Path.Combine(world, "players"));
        new NbtFile("", Character(39, "b")).Write(Profile(world, b));
        var profiles = new WorldPlayerProfileService(paths);
        profiles.PrepareWorldForOutgoingTransfer(world, a);
        profiles.PrepareReceivedWorldForIdentity(world, b);
        Assert.Equal(39, LevelPlayer(world)!.GetInt("XpLevel"));
        Assert.Equal(b.SteamId64.ToString(), new WorldMetadataService().Read(world)!.LegacyHostSteamId64);

        profiles.PrepareWorldForOutgoingTransfer(world, b);

        Assert.Equal(17, NbtFile.Read(Profile(world, a)).Root.GetInt("XpLevel"));
        Assert.Equal(39, NbtFile.Read(Profile(world, b)).Root.GetInt("XpLevel"));
        Assert.Null(LevelPlayer(world));
    }

    [Fact]
    public void StartingAnotherBuild_DoesNotValidateOrRewriteTheLegacyWorld()
    {
        var paths = Paths("scope");
        var legacy = LegacyWorld(paths, "legacy-world", PlayerA());
        WriteLevel(legacy, Character(12, "legacy"));
        var before = File.ReadAllBytes(Path.Combine(legacy, "level.dat"));
        var modern = Path.Combine(WorldLocations.ForBuild(paths.Worlds, "Modern"), "world");
        Directory.CreateDirectory(modern);
        var player = Character(23, "modern");
        player.SetUuid(PlayerA().PlayerUuid);
        WriteLevel(modern, player, withModernSettings: true);
        var descriptor = new PackRuntimeDescriptor(1, "1.21.1", new(PackLoaderKind.Fabric, "0.19.5"), "client.jar", "test");

        new WorldPlayerProfileService(paths).PrepareWorldsForLaunch(paths.Worlds, PlayerA(), "Modern", descriptor);

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(legacy, "level.dat")));
        Assert.False(File.Exists(Path.Combine(legacy, WorldPlayerManifestService.ManifestFileName)));
        Assert.Equal(PlayerA().PlayerUuid, LevelPlayer(modern)!.GetUuid());
    }

    [Fact]
    public void ManifestCannotAssignAnotherSteamAccountsLegacyFileToTheCurrentPlayer()
    {
        var paths = Paths("tamper");
        var world = LegacyWorld(paths, "world", PlayerA());
        WriteLevel(world, Character(5, "a"));
        var service = new WorldPlayerProfileService(paths);
        service.PrepareWorldForLaunch(world, PlayerA());
        var manifest = service.ValidatePlayerManifest(world);
        var entry = Assert.Single(manifest.Players);
        entry.SteamId64 = PlayerB().SteamId64.ToString();
        entry.PortableUuid = PlayerB().MinecraftUuid;
        entry.MinecraftUuid = PlayerB().MinecraftUuid;
        WriteManifest(world, manifest);

        Assert.Throws<InvalidDataException>(() => service.ValidatePlayerManifest(world));
    }

    [Fact]
    public void LegacyTransferManifestCoversUnknownFilesAndRejectsTraversalOrChangedBytes()
    {
        var paths = Paths("coverage");
        var world = LegacyWorld(paths, "world", PlayerA());
        WriteLevel(world, Character(5, "a"));
        Directory.CreateDirectory(Path.Combine(world, "stats"));
        File.WriteAllText(Path.Combine(world, "stats", "OldNick.json"), "old statistics");
        var service = new WorldPlayerProfileService(paths);
        service.PrepareWorldForLaunch(world, PlayerA());
        var manifest = service.ValidatePlayerManifest(world);
        Assert.Single(manifest.UnassignedFiles);
        manifest.UnassignedFiles[0].Path = "../outside";
        WriteManifest(world, manifest);
        Assert.Throws<InvalidDataException>(() => service.ValidatePlayerManifest(world));
        new WorldPlayerManifestService().Write(world, PlayerA());
        File.WriteAllText(Path.Combine(world, "stats", "OldNick.json"), "changed statistics");
        Assert.Throws<InvalidDataException>(() => service.ValidatePlayerManifest(world));
    }

    [Fact]
    public void AStatsPrefixCannotAssignAnotherAccountsProfileThroughPathNormalization()
    {
        var paths = Paths("owner-path-alias");
        var world = LegacyWorld(paths, "world", PlayerA());
        WriteLevel(world, Character(5, "a"));
        var service = new WorldPlayerProfileService(paths);
        service.PrepareWorldForLaunch(world, PlayerA());
        new NbtFile("", Character(23, "b")).Write(Profile(world, PlayerB()));
        var manifest = new WorldPlayerManifestService().Write(world, PlayerA());
        var a = manifest.Players.Single(player => player.SteamId64 == PlayerA().SteamId64.ToString());
        var b = manifest.Players.Single(player => player.SteamId64 == PlayerB().SteamId64.ToString());
        var stolen = Assert.Single(b.Files);
        stolen.Path = $"stats/{a.StorageName}.x/../../players/{b.StorageName}.dat";
        a.Files.Add(stolen);
        manifest.Players.Remove(b);
        WriteManifest(world, manifest);

        Assert.Throws<InvalidDataException>(() => service.ValidatePlayerManifest(world));
        Assert.Throws<InvalidDataException>(() => new WorldPlayerManifestService().Read(world));
        Assert.Equal(23, NbtFile.Read(Profile(world, PlayerB())).Root.GetInt("XpLevel"));
    }

    [Theory]
    [InlineData("stats/../stats/OldNick.json")]
    [InlineData("stats/./OldNick.json")]
    [InlineData("stats//OldNick.json")]
    [InlineData("stats\\OldNick.json")]
    [InlineData("stats/OldNick.json.")]
    public void UnassignedLegacyFilesRequireCanonicalPaths(string alias)
    {
        var paths = Paths("unassigned-path-alias");
        var world = LegacyWorld(paths, "world", PlayerA());
        WriteLevel(world, Character(5, "a"));
        Directory.CreateDirectory(Path.Combine(world, "stats"));
        File.WriteAllText(Path.Combine(world, "stats", "OldNick.json"), "old statistics");
        var service = new WorldPlayerProfileService(paths);
        service.PrepareWorldForLaunch(world, PlayerA());
        var manifest = service.ValidatePlayerManifest(world);
        Assert.Single(manifest.UnassignedFiles).Path = alias;
        WriteManifest(world, manifest);

        Assert.Throws<InvalidDataException>(() => service.ValidatePlayerManifest(world));
    }

    [Fact]
    public void RecordedMinecraftVersionCannotBeReinterpretedByAnotherRuntime()
    {
        var paths = Paths("mismatch");
        var world = LegacyWorld(paths, "world", PlayerA());
        var metadataPath = Path.Combine(world, WorldMetadataService.MetadataFileName);
        var metadata = new WorldMetadataService().Read(world)!;
        metadata.PlayerStorage = "";
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, Json));
        var modern = new PackRuntimeDescriptor(1, "1.21.1", new(PackLoaderKind.Fabric, "0.19.5"), "client.jar", "test");

        Assert.Throws<InvalidDataException>(() => WorldPlayerStorage.Resolve(world, paths, modern));
    }

    [Theory]
    [InlineData("players")]
    [InlineData("stats")]
    public void ProfileDirectoryJunctionsAreRejectedBeforeReadingOrChangingAnything(string directory)
    {
        var paths = Paths("linked-profile-" + directory);
        var world = LegacyWorld(paths, "world", PlayerA());
        WriteLevel(world, Character(42, "new local character"));
        var outside = Path.Combine(_root, "outside-" + directory);
        Directory.CreateDirectory(outside);
        var profileName = LegacyPlayerIdentity.NativeName(PlayerA().SteamId64) + ".dat";
        var sentinel = Path.Combine(outside, profileName);
        new NbtFile("", Character(7, "external character")).Write(sentinel);
        var before = File.ReadAllBytes(sentinel);
        var levelBefore = File.ReadAllBytes(Path.Combine(world, "level.dat"));
        var metadataBefore = File.ReadAllBytes(Path.Combine(world, WorldMetadataService.MetadataFileName));
        var junction = Path.Combine(world, directory);
        SavesFolderService.CreateJunction(junction, outside);
        try
        {
            var service = new WorldPlayerProfileService(paths);
            Assert.Throws<InvalidDataException>(() => service.PrepareWorldForLaunch(world, PlayerA()));
            Assert.Throws<InvalidDataException>(() => service.PrepareWorldForOutgoingTransfer(world, PlayerA()));
            Assert.Equal(before, File.ReadAllBytes(sentinel));
            Assert.Equal(levelBefore, File.ReadAllBytes(Path.Combine(world, "level.dat")));
            Assert.Equal(metadataBefore, File.ReadAllBytes(Path.Combine(world, WorldMetadataService.MetadataFileName)));
            Assert.False(File.Exists(Path.Combine(world, WorldPlayerManifestService.ManifestFileName)));
            Assert.Single(Directory.EnumerateFiles(outside));
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public void DamagedPlatformMetadataIsNotReplacedWhilePreparingACharacter()
    {
        var paths = Paths("bad-metadata");
        var world = LegacyWorld(paths, "world", PlayerA());
        WriteLevel(world, Character(25, "untouched"));
        var metadataPath = Path.Combine(world, WorldMetadataService.MetadataFileName);
        File.WriteAllText(metadataPath, "{invalid-json");
        var before = File.ReadAllBytes(Path.Combine(world, "level.dat"));

        Assert.Throws<InvalidDataException>(() => new WorldPlayerProfileService(paths).PrepareWorldForLaunch(world, PlayerA()));

        Assert.Equal("{invalid-json", File.ReadAllText(metadataPath));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(world, "level.dat")));
    }

    [Fact]
    public void PlayedPlatformStampRecordsTheActualHostBeforeTheFirstTransfer()
    {
        var paths = Paths("first-save");
        var a = PlayerA();
        var world = Path.Combine(WorldLocations.ForBuild(paths.Worlds, "legacy"), "new");
        Directory.CreateDirectory(world);
        WriteLevel(world, Character(42, "first-save"));
        File.WriteAllText(Path.Combine(world, "session.lock"), "closed-fixture");
        new WorldMetadataService().StampPlayedWorlds(paths.Worlds, new WorldMetadataContext
        {
            BuildName = "Legacy", BuildRelativePath = "legacy", PackHash = "pack", MinecraftVersion = "1.6.4",
            OwnerIdentityId = a.MinecraftUuid, OwnerIdentityName = a.IdentityName, OwnerSteamId64 = a.SteamId64.ToString()
        }, DateTimeOffset.UtcNow.AddMinutes(-1));
        new WorldPlayerProfileService(paths).PrepareWorldForOutgoingTransfer(world, a);

        Assert.Equal(42, NbtFile.Read(Profile(world, a)).Root.GetInt("XpLevel"));
        Assert.Equal("legacy-name-v1", new WorldMetadataService().Read(world)!.PlayerStorage);
    }

    private AppPaths Paths(string name)
    {
        var paths = new AppPaths(Path.Combine(_root, name));
        paths.Ensure();
        return paths;
    }

    private static string LegacyWorld(AppPaths paths, string name, LocalIdentityContext? holder)
    {
        var world = Path.Combine(WorldLocations.ForBuild(paths.Worlds, "legacy"), name);
        Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, WorldMetadataService.MetadataFileName), JsonSerializer.Serialize(new WorldMetadata
        {
            WorldId = Guid.NewGuid().ToString("D"), BuildRelativePath = "legacy", MinecraftVersion = "1.6.4",
            PlayerStorage = "legacy-name-v1", LegacyHostSteamId64 = holder?.SteamId64.ToString() ?? ""
        }, Json));
        return world;
    }

    private static NbtCompoundTag Character(int level, string item)
    {
        var player = new NbtCompoundTag();
        player.Set("XpLevel", new NbtIntTag(level));
        player.Set("Health", new NbtFloatTag(17.5f));
        var inventory = new NbtCompoundTag();
        inventory.Set("id", new NbtStringTag(item));
        inventory.Set("nested", new NbtLongTag(123456789));
        player.Set("InventoryFixture", inventory);
        return player;
    }

    private static void WriteLevel(string world, NbtCompoundTag player, bool withModernSettings = false)
    {
        var data = new NbtCompoundTag();
        data.Set("Player", player);
        if (withModernSettings) data.Set("WorldGenSettings", new NbtCompoundTag());
        var root = new NbtCompoundTag();
        root.Set("Data", data);
        new NbtFile("", root).Write(Path.Combine(world, "level.dat"));
    }

    private static NbtCompoundTag? LevelPlayer(string world) => NbtFile.Read(Path.Combine(world, "level.dat")).Root.GetCompound("Data")?.GetCompound("Player");
    private static string Profile(string world, LocalIdentityContext player) => Path.Combine(world, "players", LegacyPlayerIdentity.NativeName(player.SteamId64) + ".dat");
    private static void WriteManifest(string world, WorldPlayersManifest manifest) => File.WriteAllText(Path.Combine(world, WorldPlayerManifestService.ManifestFileName), JsonSerializer.Serialize(manifest, Json));

    private static string CopyWorld(string source, string destination)
    {
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target);
        }
        return destination;
    }
}
