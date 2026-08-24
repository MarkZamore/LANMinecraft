using System.IO;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// A world whose level.dat has lost Data.WorldGenSettings is one the game will
/// not open. It happened to a real world: the game left the compound out in
/// silence, and then two launches of the launcher wrote over both copies of the
/// file, taking the last good one with them. So the launcher keeps its own
/// backup slot, puts the settings back when a copy beside the file still has
/// them, and touches nothing when none does.
/// </summary>
public sealed class WorldGenSettingsGuardTests
{
    [Fact]
    public void TheWriter_LeavesTheGameOwnPreviousSaveAlone()
    {
        var world = NewWorld();
        var levelPath = Path.Combine(world, "level.dat");
        var gameBackup = Path.Combine(world, "level.dat_old");
        try
        {
            WriteLevel(levelPath, withSettings: true, spawn: 1);
            File.WriteAllText(gameBackup, "the game's own previous save");

            WriteLevel(levelPath, withSettings: true, spawn: 2);

            Assert.Equal("the game's own previous save", File.ReadAllText(gameBackup));
            var ours = Path.Combine(world, NbtFile.LauncherBackupFileName);
            Assert.True(File.Exists(ours), "the launcher should keep the copy it replaced under its own name");
            Assert.Equal(1, NbtFile.Read(ours).Root.GetCompound("Data")!.GetInt("SpawnX"));
            Assert.Equal(2, NbtFile.Read(levelPath).Root.GetCompound("Data")!.GetInt("SpawnX"));
        }
        finally
        {
            TempTree.Delete(world);
        }
    }

    [Fact]
    public void ALostSetting_ComesBackFromTheCopyBesideIt()
    {
        var world = NewWorld();
        var levelPath = Path.Combine(world, "level.dat");
        try
        {
            WriteLevel(levelPath, withSettings: false, spawn: 7);
            WriteLevel(Path.Combine(world, "level.dat_old"), withSettings: true, spawn: 6);
            var data = NbtFile.Read(levelPath).Root.GetCompound("Data")!;

            var proceed = WorldPlayerProfileService.RestoreWorldGenSettings(world, levelPath, data, logger: null);

            Assert.True(proceed, "with the settings back the world is safe to prepare");
            var settings = data.GetCompound("WorldGenSettings");
            Assert.NotNull(settings);
            Assert.Equal("minecraft:overworld", settings!.GetCompound("dimensions")!.GetCompound("minecraft:overworld")!.GetString("type"));
            Assert.Equal(7, data.GetInt("SpawnX"));
        }
        finally
        {
            TempTree.Delete(world);
        }
    }

    [Fact]
    public void WithNoCopyToTakeThemFrom_TheWorldIsLeftAlone()
    {
        var world = NewWorld();
        var levelPath = Path.Combine(world, "level.dat");
        try
        {
            WriteLevel(levelPath, withSettings: false, spawn: 7);
            var before = File.ReadAllBytes(levelPath);
            var data = NbtFile.Read(levelPath).Root.GetCompound("Data")!;

            var proceed = WorldPlayerProfileService.RestoreWorldGenSettings(world, levelPath, data, logger: null);

            Assert.False(proceed, "preparing this world would spend the copies it could be recovered from");
            Assert.Equal(before, File.ReadAllBytes(levelPath));
        }
        finally
        {
            TempTree.Delete(world);
        }
    }

    private static string NewWorld()
    {
        var path = Path.Combine(Path.GetTempPath(), "ll8-world-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteLevel(string path, bool withSettings, int spawn)
    {
        var data = new NbtCompoundTag();
        data.Set("SpawnX", new NbtIntTag(spawn));
        data.Set("LevelName", new NbtStringTag("Chebupeli"));
        if (withSettings)
        {
            var overworld = new NbtCompoundTag();
            overworld.Set("type", new NbtStringTag("minecraft:overworld"));
            var dimensions = new NbtCompoundTag();
            dimensions.Set("minecraft:overworld", overworld);
            var settings = new NbtCompoundTag();
            settings.Set("seed", new NbtLongTag(-5787005912516991315L));
            settings.Set("dimensions", dimensions);
            data.Set("WorldGenSettings", settings);
        }
        var root = new NbtCompoundTag();
        root.Set("Data", data);
        new NbtFile("", root).Write(path);
    }
}
