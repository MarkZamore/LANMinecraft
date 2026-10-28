using System.Text.Json;
using System.Text.Json.Nodes;
using Minecraft;

namespace Minecraft.Tests;

public sealed class TerraFirmaPackMigrationTests : IDisposable
{
    private const string OldName = PortablePackSyncService.LegacyTerraFirmaRelativePath;
    private const string NewName = PortablePackSyncService.TerraFirmaRelativePath;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "terrafirma-rename-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public TerraFirmaPackMigrationTests()
    {
        _paths = new AppPaths(_root);
        _paths.Ensure();
    }

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void InstalledPackKeepsWorldsPersonalFilesAndSettingsAndCanRunAgain()
    {
        var oldPack = _paths.CombineUnderPacks(OldName);
        var oldInstance = _paths.CombineUnderInstances(OldName);
        var oldWorld = Path.Combine(_paths.Worlds, OldName, "Мой мир");
        Write(Path.Combine(oldPack, "mods", "same.jar"), "same jar bytes");
        Write(Path.Combine(oldPack, "portable-pack-source.json"), """{"owner":"MarkZamore","repo":"TerraFirma-Rebirth","tag":"pack-latest","extra":"keep"}""");
        Write(Path.Combine(oldInstance, "options.txt"), "personal keys");
        Write(Path.Combine(oldInstance, "screenshots", "personal.png"), "personal image");
        Write(Path.Combine(oldInstance, ".portable-instance.json"), """{"schemaVersion":1,"packRelativePath":"TerraFirma Rebirth","files":{"config/file.txt":{"sha256":"abc"}},"extra":123}""");
        Write(Path.Combine(oldWorld, "level.dat"), "world data");
        Write(Path.Combine(oldWorld, "region", "r.0.0.mca"), "region bytes");
        var worldId = Guid.NewGuid().ToString();
        Write(Path.Combine(oldWorld, WorldMetadataService.MetadataFileName), JsonSerializer.Serialize(new
        {
            buildRelativePath = OldName, buildName = OldName, worldId, packHash = "same pack hash",
            ownerIdentityId = "owner", currentHolderIdentityName = "holder", extra = new { keep = 7 }
        }));
        SavesFolderService.CreateJunction(Path.Combine(oldInstance, "saves"), Path.Combine(_paths.Worlds, OldName));
        Write(Path.Combine(_paths.Runtimes, OldName, ".portable-runtime.json"), """{"clientJarRelativePath":"Runtimes/TerraFirma Rebirth/versions/client.jar","files":{"Runtimes/TerraFirma Rebirth/client.jar":{"sha256":"abc"},"Shared/keep.jar":{"sha256":"def"}}}""");
        Write(Path.Combine(_paths.Runtimes, OldName, "versions", "client.jar"), "runtime bytes");
        var settings = """{"clientRelativePath":"TerraFirma Rebirth","addedPackRelativePaths":["All The Mods 10","TerraFirma Rebirth"],"hiddenPackRelativePaths":["TerraFirma Rebirth"],"memoryByPack":{"TerraFirma Rebirth":13,"Other":5},"selectedWorldRelativePath":"TerraFirma Rebirth/Мой мир","unknown":{"keep":true}}""";
        Write(_paths.SettingsFile, settings);

        Assert.True(Migration().Migrate());

        Assert.False(Directory.Exists(oldPack));
        Assert.False(Directory.Exists(oldInstance));
        Assert.False(Directory.Exists(Path.Combine(_paths.Worlds, OldName)));
        Assert.Equal("same jar bytes", File.ReadAllText(Path.Combine(_paths.Packs, NewName, "mods", "same.jar")));
        Assert.Equal("personal keys", File.ReadAllText(Path.Combine(_paths.Instances, NewName, "options.txt")));
        Assert.Equal("personal image", File.ReadAllText(Path.Combine(_paths.Instances, NewName, "screenshots", "personal.png")));
        var newWorld = Path.Combine(_paths.Worlds, NewName, "Мой мир");
        Assert.Equal("world data", File.ReadAllText(Path.Combine(newWorld, "level.dat")));
        Assert.Equal("region bytes", File.ReadAllText(Path.Combine(newWorld, "region", "r.0.0.mca")));
        var metadata = Read(Path.Combine(newWorld, WorldMetadataService.MetadataFileName));
        Assert.Equal(NewName, metadata["buildRelativePath"]!.GetValue<string>());
        Assert.Equal(NewName, metadata["buildName"]!.GetValue<string>());
        Assert.Equal(worldId, metadata["worldId"]!.GetValue<string>());
        Assert.Equal("same pack hash", metadata["packHash"]!.GetValue<string>());
        Assert.Equal("owner", metadata["ownerIdentityId"]!.GetValue<string>());
        Assert.Equal(7, metadata["extra"]!["keep"]!.GetValue<int>());
        Assert.True(WorldMetadataService.BelongsToBuild(metadata["buildRelativePath"]!.GetValue<string>(), NewName));
        Assert.True(WorldMetadataService.BelongsToBuild(OldName, NewName));
        Assert.True(WorldMetadataService.BelongsToBuild(NewName, OldName));
        Assert.False(WorldMetadataService.BelongsToBuild(OldName, "Other Pack"));
        Assert.Equal(Path.Combine(_paths.Worlds, NewName), WorldLocations.ForBuild(_paths.Worlds, OldName));
        var saves = Path.Combine(_paths.Instances, NewName, "saves");
        Assert.Equal(Path.Combine(_paths.Worlds, NewName), new DirectoryInfo(saves).LinkTarget);
        Assert.Equal("world data", File.ReadAllText(Path.Combine(saves, "Мой мир", "level.dat")));
        var migrated = Read(_paths.SettingsFile);
        Assert.Equal(NewName, migrated["clientRelativePath"]!.GetValue<string>());
        Assert.Equal(NewName, migrated["addedPackRelativePaths"]![1]!.GetValue<string>());
        Assert.Equal(NewName, migrated["hiddenPackRelativePaths"]![0]!.GetValue<string>());
        Assert.Equal(13, migrated["memoryByPack"]![NewName]!.GetValue<int>());
        Assert.Null(migrated["memoryByPack"]![OldName]);
        Assert.Equal(NewName + "/Мой мир", migrated["selectedWorldRelativePath"]!.GetValue<string>());
        Assert.True(migrated["unknown"]!["keep"]!.GetValue<bool>());
        var instanceState = Read(Path.Combine(_paths.Instances, NewName, ".portable-instance.json"));
        Assert.Equal(NewName, instanceState["packRelativePath"]!.GetValue<string>());
        Assert.Equal("abc", instanceState["files"]!["config/file.txt"]!["sha256"]!.GetValue<string>());
        var source = Read(Path.Combine(_paths.Packs, NewName, "portable-pack-source.json"));
        Assert.Equal("TerraFirma-Rebirth-2", source["repo"]!.GetValue<string>());
        Assert.Equal("keep", source["extra"]!.GetValue<string>());
        var runtime = Read(Path.Combine(_paths.Runtimes, NewName, ".portable-runtime.json"));
        Assert.Equal("Runtimes/" + NewName + "/versions/client.jar", runtime["clientJarRelativePath"]!.GetValue<string>());
        Assert.NotNull(runtime["files"]!["Runtimes/" + NewName + "/client.jar"]);
        Assert.NotNull(runtime["files"]!["Shared/keep.jar"]);
        var backup = Path.Combine(_paths.Personal, "Backups", "PackRenames", NewName, "Personal", "settings.json");
        Assert.Equal(settings, File.ReadAllText(backup));
        var beforeSecond = File.ReadAllBytes(_paths.SettingsFile);
        var metadataBeforeSecond = File.ReadAllBytes(Path.Combine(newWorld, WorldMetadataService.MetadataFileName));
        Assert.False(Migration().Migrate());
        Assert.Equal(beforeSecond, File.ReadAllBytes(_paths.SettingsFile));
        Assert.Equal(metadataBeforeSecond, File.ReadAllBytes(Path.Combine(newWorld, WorldMetadataService.MetadataFileName)));
    }

    [Fact]
    public void ArchivedWorldImportedAfterRenameUsesNewFolderAndOnlyTheSamePack()
    {
        Write(Path.Combine(_paths.Packs, OldName, "keep.txt"), "pack");
        Assert.True(Migration().Migrate());
        var imported = Path.Combine(_paths.Worlds, "Imported world");
        Write(Path.Combine(imported, "level.dat"), "archived world data");
        Write(Path.Combine(imported, "region", "r.0.0.mca"), "archived region data");
        const string metadata = """{"buildRelativePath":"TerraFirma Rebirth","buildName":"TerraFirma Rebirth","worldId":"unchanged-world-id","packHash":"unchanged-pack-hash","ownerIdentityId":"unchanged-owner"}""";
        Write(Path.Combine(imported, WorldMetadataService.MetadataFileName), metadata);
        // The one-time installed-pack migration has no work at this point.
        Assert.False(Migration().Migrate());

        Assert.Equal(1, WorldLocations.Migrate(_paths.Worlds, new WorldMetadataService()));

        var routed = Path.Combine(_paths.Worlds, NewName, "Imported world");
        Assert.False(Directory.Exists(imported));
        Assert.False(Directory.Exists(Path.Combine(_paths.Worlds, OldName)));
        Assert.Equal("archived world data", File.ReadAllText(Path.Combine(routed, "level.dat")));
        Assert.Equal("archived region data", File.ReadAllText(Path.Combine(routed, "region", "r.0.0.mca")));
        Assert.Equal(metadata, File.ReadAllText(Path.Combine(routed, WorldMetadataService.MetadataFileName)));
        var recorded = new WorldMetadataService().Read(routed)!;
        Assert.True(WorldMetadataService.BelongsToBuild(recorded.BuildRelativePath, NewName));
        Assert.False(WorldMetadataService.BelongsToBuild(recorded.BuildRelativePath, "Other Pack"));
        Assert.False(WorldMetadataService.BelongsToBuild("Other Pack", NewName));
        Assert.Equal(Path.Combine(_paths.Worlds, "Other Pack"), WorldLocations.ForBuild(_paths.Worlds, "Other Pack"));
        var instance = _paths.CombineUnderInstances(NewName);
        new SavesFolderService().Prepare(_paths.Worlds, instance, NewName);
        Assert.Equal("archived world data", File.ReadAllText(Path.Combine(instance, "saves", "Imported world", "level.dat")));
    }

    [Theory]
    [InlineData("packs")]
    [InlineData("instances")]
    [InlineData("worlds")]
    [InlineData("runtimes")]
    public void DestinationConflictStopsBeforeAnyMoveOrSettingsWrite(string location)
    {
        var root = location switch
        {
            "packs" => _paths.Packs, "instances" => _paths.Instances, "worlds" => _paths.Worlds, _ => _paths.Runtimes
        };
        Write(Path.Combine(_paths.Packs, OldName, "keep.txt"), "old pack");
        Write(Path.Combine(root, OldName, "original.txt"), "original");
        Write(Path.Combine(root, NewName, "unrelated.txt"), "unrelated");
        var settings = """{"clientRelativePath":"TerraFirma Rebirth"}""";
        Write(_paths.SettingsFile, settings);
        Assert.Throws<IOException>(() => Migration().Migrate());
        Assert.Equal("old pack", File.ReadAllText(Path.Combine(_paths.Packs, OldName, "keep.txt")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(root, OldName, "original.txt")));
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(root, NewName, "unrelated.txt")));
        Assert.Equal(settings, File.ReadAllText(_paths.SettingsFile));
    }

    [Fact]
    public void RunningGameOrLockedWorldStopsBeforeMoving()
    {
        Write(Path.Combine(_paths.Packs, OldName, "keep.txt"), "pack");
        var world = Path.Combine(_paths.Worlds, OldName, "World");
        Write(Path.Combine(world, "level.dat"), "world");
        Write(Path.Combine(world, "session.lock"), "lock");
        Assert.Throws<IOException>(() => new TerraFirmaPackMigrationService(_paths, null, () => true).Migrate());
        using (File.Open(Path.Combine(world, "session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => Migration().Migrate());
        Assert.True(Directory.Exists(_paths.CombineUnderPacks(OldName)));
        Assert.True(Directory.Exists(world));
        Assert.False(Directory.Exists(_paths.CombineUnderPacks(NewName)));
    }

    [Fact]
    public void InterruptedMovesResumeAndRepairDanglingSavesLink()
    {
        var oldWorlds = Path.Combine(_paths.Worlds, OldName);
        var oldInstance = _paths.CombineUnderInstances(OldName);
        Write(Path.Combine(oldWorlds, "World", "level.dat"), "world");
        Directory.CreateDirectory(oldInstance);
        SavesFolderService.CreateJunction(Path.Combine(oldInstance, "saves"), oldWorlds);
        Directory.Move(oldWorlds, Path.Combine(_paths.Worlds, NewName));
        Write(Path.Combine(_paths.Packs, NewName, "already-moved.txt"), "pack");
        Assert.True(Migration().Migrate());
        Assert.Equal("world", File.ReadAllText(Path.Combine(_paths.Instances, NewName, "saves", "World", "level.dat")));
        Assert.Equal("pack", File.ReadAllText(Path.Combine(_paths.Packs, NewName, "already-moved.txt")));
        Assert.False(Migration().Migrate());
    }

    [Fact]
    public void OlderPerWorldLinksAreRepairedAndRealInstanceWorldsAreKept()
    {
        var worlds = Path.Combine(_paths.Worlds, OldName);
        var saves = Path.Combine(_paths.Instances, OldName, "saves");
        Write(Path.Combine(worlds, "Shared", "level.dat"), "shared world");
        Write(Path.Combine(saves, "Local", "level.dat"), "instance world");
        Write(Path.Combine(saves, "Local", WorldMetadataService.MetadataFileName), """{"buildRelativePath":"TerraFirma Rebirth","worldId":"same"}""");
        SavesFolderService.CreateJunction(Path.Combine(saves, "Shared"), Path.Combine(worlds, "Shared"));

        Assert.True(Migration().Migrate());

        var renamedSaves = Path.Combine(_paths.Instances, NewName, "saves");
        Assert.Equal("shared world", File.ReadAllText(Path.Combine(renamedSaves, "Shared", "level.dat")));
        Assert.Equal("instance world", File.ReadAllText(Path.Combine(renamedSaves, "Local", "level.dat")));
        Assert.Equal(NewName, Read(Path.Combine(renamedSaves, "Local", WorldMetadataService.MetadataFileName))["buildRelativePath"]!.GetValue<string>());
    }

    [Fact]
    public void UnrelatedCorruptSettingsAndCompletedNewNameCachesKeepTheirNormalRecovery()
    {
        Write(_paths.SettingsFile, "{");
        Assert.False(Migration().Migrate());
        var defaults = new SettingsService(_paths).Load();
        Assert.Contains(PortablePackSyncService.DefaultPackRelativePath, defaults.AddedPackRelativePaths!);
        Write(Path.Combine(_paths.Runtimes, NewName, ".portable-runtime.json"), "{");
        Write(Path.Combine(_paths.Worlds, NewName, "World", "level.dat"), "world");
        Write(Path.Combine(_paths.Worlds, NewName, "World", WorldMetadataService.MetadataFileName), "{");
        Write(_paths.SettingsFile, """{"clientRelativePath":"TerraFirma Rebirth 2"}""");
        Assert.False(Migration().Migrate());
    }

    [Fact]
    public void CorruptUnrelatedFlatWorldDoesNotBlockRename()
    {
        Write(Path.Combine(_paths.Packs, OldName, "keep.txt"), "pack");
        var flat = Path.Combine(_paths.Worlds, "Unrelated");
        Write(Path.Combine(flat, "level.dat"), "world");
        Write(Path.Combine(flat, WorldMetadataService.MetadataFileName), "{");
        Assert.True(Migration().Migrate());
        Assert.Equal("{", File.ReadAllText(Path.Combine(flat, WorldMetadataService.MetadataFileName)));
        Assert.Equal("world", File.ReadAllText(Path.Combine(flat, "level.dat")));
    }

    [Fact]
    public void JournalResumesAfterEveryDirectoryWasAlreadyMoved()
    {
        var oldWorlds = Path.Combine(_paths.Worlds, OldName);
        var newWorlds = Path.Combine(_paths.Worlds, NewName);
        Write(Path.Combine(newWorlds, "World", "level.dat"), "world");
        Write(Path.Combine(newWorlds, "World", WorldMetadataService.MetadataFileName), """{"buildRelativePath":"TerraFirma Rebirth","worldId":"same"}""");
        var instance = _paths.CombineUnderInstances(NewName);
        Directory.CreateDirectory(instance);
        SavesFolderService.CreateJunction(Path.Combine(instance, "saves"), oldWorlds);
        Write(Path.Combine(_paths.Personal, ".terrafirma-rename.pending"), NewName);

        Assert.True(Migration().Migrate());

        Assert.Equal("world", File.ReadAllText(Path.Combine(instance, "saves", "World", "level.dat")));
        Assert.Equal(NewName, Read(Path.Combine(newWorlds, "World", WorldMetadataService.MetadataFileName))["buildRelativePath"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(_paths.Personal, ".terrafirma-rename.pending")));
        Assert.False(Migration().Migrate());
    }

    [Fact]
    public void CustomSourceAndOtherWorldMetadataStayUntouched()
    {
        var marker = Path.Combine(_paths.Packs, OldName, "portable-pack-source.json");
        const string custom = """{"owner":"OtherOwner","repo":"TerraFirma-Rebirth","tag":"pack-latest"}""";
        Write(marker, custom);
        var other = Path.Combine(_paths.Worlds, "Other", "World");
        Write(Path.Combine(other, "level.dat"), "other world");
        const string metadata = """{"buildRelativePath":"Other","buildName":"Other","worldId":"keep"}""";
        Write(Path.Combine(other, WorldMetadataService.MetadataFileName), metadata);
        Assert.True(Migration().Migrate());
        Assert.Equal(custom, File.ReadAllText(Path.Combine(_paths.Packs, NewName, "portable-pack-source.json")));
        Assert.Equal(metadata, File.ReadAllText(Path.Combine(other, WorldMetadataService.MetadataFileName)));
    }

    [Fact]
    public void MalformedMetadataAndLinkedPackDoNotMoveOrChangeFiles()
    {
        var oldPack = _paths.CombineUnderPacks(OldName);
        Write(Path.Combine(oldPack, "keep.txt"), "pack");
        Write(_paths.SettingsFile, "{");
        Assert.ThrowsAny<JsonException>(() => Migration().Migrate());
        Assert.True(Directory.Exists(oldPack));
        Assert.Equal("{", File.ReadAllText(_paths.SettingsFile));
        File.Delete(_paths.SettingsFile);
        var external = Path.Combine(_root, "external");
        Directory.Move(oldPack, external);
        SavesFolderService.CreateJunction(oldPack, external);
        Assert.Throws<IOException>(() => Migration().Migrate());
        Assert.Equal("pack", File.ReadAllText(Path.Combine(external, "keep.txt")));
        Assert.False(Directory.Exists(_paths.CombineUnderPacks(NewName)));
    }

    [Fact]
    public void SettingsLoadMigratesSelectionButConflictDoesNotFallBackAndOverwriteSettings()
    {
        Write(_paths.SettingsFile, JsonSerializer.Serialize(new AppSettings
        {
            ClientRelativePath = OldName, AddedPackRelativePaths = [OldName], MemoryByPack = new() { [OldName] = 8 },
            MemorySettingIsTheHeap = true
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var loaded = new SettingsService(_paths).Load();
        Assert.Equal(NewName, loaded.ClientRelativePath);
        Assert.Contains(NewName, loaded.AddedPackRelativePaths!);
        Assert.Equal(8, loaded.MemoryByPack[NewName]);
        Write(Path.Combine(_paths.Packs, OldName, "old.txt"), "old");
        Write(Path.Combine(_paths.Packs, NewName, "new.txt"), "new");
        var before = File.ReadAllBytes(_paths.SettingsFile);
        Assert.Throws<IOException>(() => new SettingsService(_paths).Load());
        Assert.Equal(before, File.ReadAllBytes(_paths.SettingsFile));
    }

    private TerraFirmaPackMigrationService Migration() => new(_paths, null, () => false);
    private static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
