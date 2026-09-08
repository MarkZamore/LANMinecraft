using System.IO;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Which worlds the game is allowed to see, and where they live.
///
/// Worlds are kept in <c>Worlds/&lt;build&gt;/&lt;world&gt;</c>, and a build's
/// saves folder is one junction to its own folder there. The launcher lists
/// every world whatever build it belongs to, because that list is what a world
/// is handed over from; the game must not, because opening a world under a
/// build that lacks its mods is how the blocks of every missing mod are lost.
///
/// The link is on the folder rather than on each world inside it because
/// Minecraft 1.20.1 checks a world folder in a way a Windows junction cannot
/// pass. With the link one level up, every world the game opens is a plain
/// directory on every version.
/// </summary>
public sealed class SavesFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-saves-{Guid.NewGuid():N}");

    private string Worlds => Path.Combine(_root, "Worlds");
    private string Instance => Path.Combine(_root, "instance");
    private string Saves => Path.Combine(Instance, "saves");

    public void Dispose()
    {
        try
        {
            TempTree.Delete(_root);
        }
        catch
        {
        }
    }

    [Fact]
    public void TheSavesFolder_BecomesOneLinkToThisBuildsWorlds()
    {
        MakeWorldIn("Build A", "Chebupeli");

        new SavesFolderService().Prepare(Worlds, Instance, "Build A");

        Assert.True(IsLink(Saves));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Worlds, "Build A")),
            Path.GetFullPath(new DirectoryInfo(Saves).LinkTarget!));
        Assert.True(File.Exists(Path.Combine(Saves, "Chebupeli", "level.dat")));
    }

    /// <summary>
    /// And what the game opens is a real directory, which is the whole point:
    /// 1.20.1 refuses a world folder that is a junction.
    /// </summary>
    [Fact]
    public void EveryWorldTheGameOpens_IsARealDirectory()
    {
        MakeWorldIn("Build A", "Chebupeli");

        new SavesFolderService().Prepare(Worlds, Instance, "Build A");

        Assert.False(IsLink(Path.Combine(Saves, "Chebupeli")));
    }

    [Fact]
    public void OnlyTheWorldsOfThisBuild_AreVisible()
    {
        MakeWorldIn("Build A", "Chebupeli");
        MakeWorldIn("Build B", "Sky Factory");

        new SavesFolderService().Prepare(Worlds, Instance, "Build A");

        Assert.True(Directory.Exists(Path.Combine(Saves, "Chebupeli")));
        Assert.False(Directory.Exists(Path.Combine(Saves, "Sky Factory")));
        // And the other build's world is untouched where it lives.
        Assert.True(File.Exists(Path.Combine(Worlds, "Build B", "Sky Factory", "level.dat")));
    }

    [Fact]
    public void AWorldLyingFlat_MovesIntoItsBuildsFolder()
    {
        MakeFlatWorld("Chebupeli", "Build A");

        new SavesFolderService().Prepare(Worlds, Instance, "Build A");

        Assert.True(File.Exists(Path.Combine(Worlds, "Build A", "Chebupeli", "level.dat")));
        Assert.False(Directory.Exists(Path.Combine(Worlds, "Chebupeli")));
    }

    /// <summary>
    /// A world that says nowhere which build made it stays in the root of
    /// Worlds, beside the build folders rather than inside one. Putting it
    /// under mods it does not know would strip out every block those mods
    /// lack, and no guess is worth that.
    /// </summary>
    [Fact]
    public void AWorldNobodyStamped_StaysInTheRoot()
    {
        MakeFlatWorld("hand-dropped", build: null);

        new SavesFolderService().Prepare(Worlds, Instance, "Build A");

        var loose = Path.Combine(Worlds, "hand-dropped");
        Assert.True(File.Exists(Path.Combine(loose, "level.dat")));
        // No build lists it, and the launcher still does - so it can be handed
        // on, or played somewhere that will stamp it.
        Assert.False(Directory.Exists(Path.Combine(Saves, "hand-dropped")));
        Assert.Contains(
            Path.GetFullPath(loose),
            WorldLocations.Enumerate(Worlds).Select(Path.GetFullPath));
    }

    /// <summary>
    /// The layout this replaces: a real saves folder holding a junction per
    /// world, empty folders holding withdrawn names, and any world the game
    /// made in there. Only the last is worth anything, and it must survive.
    /// </summary>
    [Fact]
    public void TheOldPerWorldLayout_IsReplacedWithoutLosingWorlds()
    {
        MakeWorldIn("Build A", "Chebupeli");
        Directory.CreateDirectory(Saves);
        SavesFolderService.CreateJunction(
            Path.Combine(Saves, "Chebupeli"), Path.Combine(Worlds, "Build A", "Chebupeli"));
        Directory.CreateDirectory(Path.Combine(Saves, "Sky Factory"));
        MakeInstanceWorld("Made Here");

        new SavesFolderService().Prepare(Worlds, Instance, "Build A");

        Assert.True(IsLink(Saves));
        Assert.True(File.Exists(Path.Combine(Worlds, "Build A", "Made Here", "level.dat")));
        Assert.True(File.Exists(Path.Combine(Worlds, "Build A", "Chebupeli", "level.dat")));
        // The withdrawn name held nothing and is not carried over.
        Assert.False(Directory.Exists(Path.Combine(Worlds, "Build A", "Sky Factory")));
    }

    [Fact]
    public void AWorldTheGameMadeHere_MovesBesideTheOthers()
    {
        MakeInstanceWorld("New World");

        var changes = new SavesFolderService().Prepare(Worlds, Instance, "Build A");

        Assert.Equal(1, changes.Adopted);
        Assert.True(File.Exists(Path.Combine(Worlds, "Build A", "New World", "level.dat")));
        Assert.True(File.Exists(Path.Combine(Saves, "New World", "level.dat")));
    }

    /// <summary>
    /// Two builds both offering "New World" is not a corner case, it is the
    /// name Minecraft suggests. They no longer collide at all: each build has
    /// its own folder.
    /// </summary>
    [Fact]
    public void TwoBuildsBothMakingANewWorld_KeepTheirOwn()
    {
        MakeInstanceWorld("New World");
        new SavesFolderService().Prepare(Worlds, Instance, "Build A");

        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(Path.Combine(other, "saves", "New World"));
        File.WriteAllBytes(Path.Combine(other, "saves", "New World", "level.dat"), new byte[16]);
        new SavesFolderService().Prepare(Worlds, other, "Build B");

        Assert.True(File.Exists(Path.Combine(Worlds, "Build A", "New World", "level.dat")));
        Assert.True(File.Exists(Path.Combine(Worlds, "Build B", "New World", "level.dat")));
    }

    /// <summary>
    /// Not while the game has it open: Windows will rename a folder out from
    /// under a held session.lock, but not one whose region files are open, and
    /// a world being played has both.
    /// </summary>
    [Fact]
    public void AWorldTheGameStillHasOpen_IsNotMovedYet()
    {
        var world = MakeInstanceWorld("New World");
        var lockPath = Path.Combine(world, "session.lock");
        File.WriteAllBytes(lockPath, new byte[1]);
        using var held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var changes = new SavesFolderService().Prepare(Worlds, Instance, "Build A");

        Assert.Equal(0, changes.Adopted);
        Assert.True(File.Exists(Path.Combine(world, "level.dat")));
    }

    [Fact]
    public void AWorldWithNoLockFile_CountsAsClosed()
    {
        var world = MakeInstanceWorld("New World");

        Assert.False(SavesFolderService.IsWorldOpen(world));
    }

    [Fact]
    public void WhatTheGameWritesThroughTheLink_LandsInTheWorld()
    {
        MakeWorldIn("Build A", "Chebupeli");
        new SavesFolderService().Prepare(Worlds, Instance, "Build A");

        File.WriteAllText(Path.Combine(Saves, "Chebupeli", "written.txt"), "through");

        Assert.Equal(
            "through",
            File.ReadAllText(Path.Combine(Worlds, "Build A", "Chebupeli", "written.txt")));
    }

    [Fact]
    public void PreparingTwice_IsQuietTheSecondTime()
    {
        MakeWorldIn("Build A", "Chebupeli");
        var service = new SavesFolderService();
        service.Prepare(Worlds, Instance, "Build A");

        var again = service.Prepare(Worlds, Instance, "Build A");

        Assert.Equal(new SavesFolderService.SavesChanges(0, 0, 0), again);
        Assert.True(File.Exists(Path.Combine(Saves, "Chebupeli", "level.dat")));
    }

    [Fact]
    public void SwitchingBuilds_PointsTheLinkSomewhereElse_WithoutMovingAnything()
    {
        MakeWorldIn("Build A", "Chebupeli");
        MakeWorldIn("Build B", "Sky Factory");
        var service = new SavesFolderService();
        service.Prepare(Worlds, Instance, "Build A");

        service.Prepare(Worlds, Instance, "Build B");

        Assert.True(File.Exists(Path.Combine(Saves, "Sky Factory", "level.dat")));
        Assert.False(Directory.Exists(Path.Combine(Saves, "Chebupeli")));
        Assert.True(File.Exists(Path.Combine(Worlds, "Build A", "Chebupeli", "level.dat")));
    }

    /// <summary>
    /// Something real standing where the link belongs is not this launcher's to
    /// remove, whatever it is.
    /// </summary>
    [Fact]
    public void AFolderWithFilesInIt_IsNeverTakenBack()
    {
        Directory.CreateDirectory(Saves);
        File.WriteAllText(Path.Combine(Saves, "not-a-world.txt"), "mine");

        new SavesFolderService().Prepare(Worlds, Instance, "Build A");

        Assert.False(IsLink(Saves));
        Assert.True(File.Exists(Path.Combine(Saves, "not-a-world.txt")));
    }

    [Fact]
    public void Enumerate_FindsWorldsInBothLayouts()
    {
        MakeWorldIn("Build A", "Chebupeli");
        MakeFlatWorld("older", build: null);
        Directory.CreateDirectory(Path.Combine(Worlds, "Build A", "not-a-world"));

        var found = WorldLocations.Enumerate(Worlds).Select(Path.GetFileName).OrderBy(n => n).ToList();

        Assert.Equal(["Chebupeli", "older"], found);
    }

    [Fact]
    public void TheShellOfAWorldDeletedInTheGame_IsSweptUp()
    {
        MakeWorldIn("Build A", "Chebupeli");
        var shell = Path.Combine(Worlds, "Build A", "gone");
        Directory.CreateDirectory(Path.Combine(shell, "region"));

        PackInstanceService.CleanupEmptyWorldPlaceholders(Worlds);

        Assert.False(Directory.Exists(shell));
        Assert.True(File.Exists(Path.Combine(Worlds, "Build A", "Chebupeli", "level.dat")));
    }

    [Fact]
    public void AWorldWithAnythingInIt_IsNeverSweptUp()
    {
        var kept = Path.Combine(Worlds, "Build A", "kept");
        Directory.CreateDirectory(kept);
        File.WriteAllText(Path.Combine(kept, "something.txt"), "x");

        PackInstanceService.CleanupEmptyWorldPlaceholders(Worlds);

        Assert.True(Directory.Exists(kept));
    }

    private static bool IsLink(string path) =>
        Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private string MakeInstanceWorld(string name)
    {
        var world = Path.Combine(Saves, name);
        Directory.CreateDirectory(world);
        File.WriteAllBytes(Path.Combine(world, "level.dat"), new byte[16]);
        return world;
    }

    private void MakeWorldIn(string build, string name) =>
        Write(Path.Combine(Worlds, build, name), build);

    private void MakeFlatWorld(string name, string? build) =>
        Write(Path.Combine(Worlds, name), build);

    private static void Write(string world, string? build)
    {
        Directory.CreateDirectory(world);
        File.WriteAllBytes(Path.Combine(world, "level.dat"), new byte[16]);
        if (build is null) return;
        var metadata = new WorldMetadata
        {
            WorldId = Guid.NewGuid().ToString("D"),
            BuildName = build,
            BuildRelativePath = build
        };
        File.WriteAllText(
            Path.Combine(world, WorldMetadataService.MetadataFileName),
            System.Text.Json.JsonSerializer.Serialize(
                metadata,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
    }
}
