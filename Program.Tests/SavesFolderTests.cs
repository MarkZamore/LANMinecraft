using System.IO;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Which worlds the game is allowed to see.
///
/// Every build shares one Worlds folder. The launcher lists all of it, because
/// that list is what a world is handed over from and a world can only be handed
/// over by whoever holds it. The game must not: opening a world under a build
/// that lacks its mods is how the blocks of every missing mod are lost. So the
/// instance's saves folder holds one junction per world this build may open,
/// and an empty folder holding the name of every world it may not.
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
    public void OnlyTheWorldsOfThisBuild_AreLinkedIn()
    {
        MakeWorld("Chebupeli", "LL8 Extended");
        MakeWorld("Sky Factory", "ATM10");
        MakeWorld("hand-dropped", build: null);

        var changes = new SavesFolderService().Prepare(Worlds, Instance, "LL8 Extended");

        Assert.Equal(2, changes.Shown);
        Assert.True(Directory.Exists(Path.Combine(Saves, "Chebupeli")));
        // Withdrawn: there is no world there to open. Its name is held all the
        // same, so the game cannot hand it to a second world.
        Assert.False(File.Exists(Path.Combine(Saves, "Sky Factory", "level.dat")));
        // A world nobody stamped is shown to every build: hiding it would be
        // losing it, and playing it is what gives it a build.
        Assert.True(Directory.Exists(Path.Combine(Saves, "hand-dropped")));
    }

    /// <summary>
    /// The name of a world this build may not open is held by an empty folder,
    /// so the game never offers that name to a new world.
    /// </summary>
    /// <remarks>
    /// The game does not read its own world list to decide whether a name is
    /// free. It calls Files.createDirectory on the name and catches
    /// FileAlreadyExistsException - verified in the bytecode of 1.18.2, 1.20.1
    /// and 1.21.1, where the method is instruction for instruction the same, and
    /// on the runtime this launcher ships, where an empty directory throws just
    /// as a full one does. So an empty folder is answer enough, and no level.dat
    /// is needed to make it one. What the game does instead is what it always
    /// does with a taken name: it appends " (1)".
    /// </remarks>
    [Fact]
    public void AWithdrawnWorldsName_IsHeldByAnEmptyFolder()
    {
        MakeWorld("New World", "ATM10");

        new SavesFolderService().Prepare(Worlds, Instance, "LL8 Extended");

        var held = Path.Combine(Saves, "New World");
        Assert.True(Directory.Exists(held));
        Assert.Null(new DirectoryInfo(held).LinkTarget);
        Assert.Empty(Directory.EnumerateFileSystemEntries(held));
        // And the world itself is untouched where it lives.
        Assert.True(File.Exists(Path.Combine(Worlds, "New World", "level.dat")));
    }

    /// <summary>
    /// A held name is given back the moment the world behind it goes away.
    /// Otherwise the folder would go on holding a name against every world made
    /// here afterwards, which is the very thing it was put there to prevent.
    /// </summary>
    [Fact]
    public void APlaceholderWhoseWorldHasGone_IsTakenAway()
    {
        MakeWorld("New World", "ATM10");
        var service = new SavesFolderService();
        service.Prepare(Worlds, Instance, "LL8 Extended");
        Assert.True(Directory.Exists(Path.Combine(Saves, "New World")));

        Directory.Delete(Path.Combine(Worlds, "New World"), recursive: true);
        service.Prepare(Worlds, Instance, "LL8 Extended");

        Assert.False(Directory.Exists(Path.Combine(Saves, "New World")));
    }

    /// <summary>
    /// A placeholder is not a world and is never moved into the shared folder.
    /// Adoption asks for a level.dat, which is what a world is; a placeholder
    /// has none, and a phantom world beside the real ones would be worse than
    /// the collision it was standing in the way of.
    /// </summary>
    [Fact]
    public void APlaceholder_IsNeverAdoptedAsAWorld()
    {
        MakeWorld("New World", "ATM10");
        var service = new SavesFolderService();
        service.Prepare(Worlds, Instance, "LL8 Extended");

        Assert.Equal(0, service.Adopt(Worlds, Instance));
        Assert.Single(Directory.EnumerateDirectories(Worlds));
    }

    /// <summary>
    /// And the whole point, end to end: the build that cannot see the other
    /// build's "New World" makes its own, the game gives it the name it gives a
    /// taken one, and both worlds end up beside each other under names that
    /// tell them apart.
    /// </summary>
    [Fact]
    public void TwoBuildsBothMakingANewWorld_EndUpWithTwoWorlds()
    {
        MakeWorld("New World", "ATM10");
        var service = new SavesFolderService();
        service.Prepare(Worlds, Instance, "LL8 Extended");

        // What the game does when it finds the name held: it counts on. The
        // launcher does not choose this name, the game does.
        MakeInstanceWorld("New World (1)");
        service.Adopt(Worlds, Instance);

        Assert.True(File.Exists(Path.Combine(Worlds, "New World", "level.dat")));
        Assert.True(File.Exists(Path.Combine(Worlds, "New World (1)", "level.dat")));
    }

    /// <summary>
    /// A world the game still has open is left where it is.
    /// </summary>
    /// <remarks>
    /// This is what lets the launcher move a world the moment the player leaves
    /// it instead of waiting for the game to close. Windows renames a folder out
    /// from under a held session.lock without complaint - measured - but not one
    /// whose region files are open, and a world being played has both. So the
    /// question asked is the one that answers both: does anybody hold the lock.
    /// </remarks>
    [Fact]
    public void AWorldTheGameStillHasOpen_IsNotMovedYet()
    {
        // Adoption moves into the shared folder; laying it out is Prepare's job.
        Directory.CreateDirectory(Worlds);
        var world = MakeInstanceWorld("New World");
        var lockPath = Path.Combine(world, "session.lock");
        File.WriteAllText(lockPath, "held");

        using (File.Open(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(SavesFolderService.IsWorldOpen(world));
            Assert.Equal(0, new SavesFolderService().Adopt(Worlds, Instance));
            Assert.True(File.Exists(Path.Combine(world, "level.dat")));
            Assert.Empty(Directory.EnumerateDirectories(Worlds));
        }

        // The player leaves the world. The file stays behind - Minecraft lets go
        // of it rather than deleting it - so what changed is only who holds it.
        Assert.False(SavesFolderService.IsWorldOpen(world));
        Assert.Equal(1, new SavesFolderService().Adopt(Worlds, Instance));
        Assert.True(File.Exists(Path.Combine(Worlds, "New World", "level.dat")));
    }

    /// <summary>A world that was never opened has no lock, and is not open.</summary>
    [Fact]
    public void AWorldWithNoLockFile_CountsAsClosed()
    {
        Directory.CreateDirectory(Worlds);
        var world = MakeInstanceWorld("New World");

        Assert.False(SavesFolderService.IsWorldOpen(world));
        Assert.Equal(1, new SavesFolderService().Adopt(Worlds, Instance));
    }

    /// <summary>A junction is a way in, not a copy: writing through it lands in the world.</summary>
    [Fact]
    public void WhatTheGameWritesThroughTheLink_LandsInTheWorld()
    {
        MakeWorld("Chebupeli", "LL8 Extended");
        new SavesFolderService().Prepare(Worlds, Instance, "LL8 Extended");

        File.WriteAllText(Path.Combine(Saves, "Chebupeli", "session.lock"), "held");

        Assert.True(File.Exists(Path.Combine(Worlds, "Chebupeli", "session.lock")));
    }

    /// <summary>
    /// Switching builds withdraws what the other build may not open, and brings
    /// back what it may - without touching a single world.
    /// </summary>
    [Fact]
    public void SwitchingBuilds_WithdrawsAndRestores_WithoutMovingAnything()
    {
        MakeWorld("Chebupeli", "LL8 Extended");
        MakeWorld("Sky Factory", "ATM10");
        var service = new SavesFolderService();

        service.Prepare(Worlds, Instance, "LL8 Extended");
        var withdrawn = service.Prepare(Worlds, Instance, "ATM10");

        Assert.Equal(1, withdrawn.Shown);
        Assert.Equal(1, withdrawn.Hidden);
        Assert.False(File.Exists(Path.Combine(Saves, "Chebupeli", "level.dat")));
        Assert.True(Directory.Exists(Path.Combine(Saves, "Sky Factory")));
        Assert.True(File.Exists(Path.Combine(Worlds, "Chebupeli", "level.dat")));
        Assert.True(File.Exists(Path.Combine(Worlds, "Sky Factory", "level.dat")));
    }

    /// <summary>Running it twice changes nothing the second time.</summary>
    [Fact]
    public void PreparingTwice_IsQuietTheSecondTime()
    {
        MakeWorld("Chebupeli", "LL8 Extended");
        var service = new SavesFolderService();

        service.Prepare(Worlds, Instance, "LL8 Extended");
        var again = service.Prepare(Worlds, Instance, "LL8 Extended");

        Assert.Equal(0, again.Shown);
        Assert.Equal(0, again.Hidden);
        Assert.Equal(0, again.Adopted);
    }

    /// <summary>
    /// The one thing a per-world junction costs: a world the player makes lands
    /// in the instance, because the game makes a real folder for it. It has to
    /// be moved beside the others, or the next build to run would not see it and
    /// nothing would ever give it a build.
    /// </summary>
    [Fact]
    public void AWorldTheGameMade_MovesBesideTheOthers()
    {
        MakeWorld("Chebupeli", "LL8 Extended");
        var service = new SavesFolderService();
        service.Prepare(Worlds, Instance, "LL8 Extended");

        // The game makes a new world inside the instance.
        var made = Path.Combine(Saves, "New World");
        Directory.CreateDirectory(made);
        File.WriteAllBytes(Path.Combine(made, "level.dat"), new byte[16]);

        Assert.Equal(1, service.Adopt(Worlds, Instance));

        Assert.True(File.Exists(Path.Combine(Worlds, "New World", "level.dat")));
        // And it is still reachable from the instance, through a link now.
        Assert.True(File.Exists(Path.Combine(Saves, "New World", "level.dat")));
        Assert.NotNull(new DirectoryInfo(Path.Combine(Saves, "New World")).LinkTarget);
    }

    /// <summary>
    /// Two different worlds under one name are kept apart rather than one of
    /// them being left behind. Neither is touched, neither is renamed in the
    /// game, and the world the other build made is not disturbed at all.
    /// </summary>
    [Fact]
    public void AMadeWorldWhoseNameIsTaken_MovesOverBeside()
    {
        MakeWorld("New World", "ATM10");
        var service = new SavesFolderService();
        service.Prepare(Worlds, Instance, "LL8 Extended");

        var made = Path.Combine(Saves, "New World");
        Directory.CreateDirectory(made);
        File.WriteAllText(Path.Combine(made, "level.dat"), "mine");

        Assert.Equal(1, service.Adopt(Worlds, Instance));

        // Mine went over under a name of its own; theirs is exactly as it was.
        Assert.Equal("mine", File.ReadAllText(Path.Combine(Worlds, "New World (1)", "level.dat")));
        Assert.NotEqual("mine", File.ReadAllText(Path.Combine(Worlds, "New World", "level.dat")));
    }

    /// <summary>
    /// The releases before this one made saves a single link to the whole Worlds
    /// folder. Taking that link away leaves every world exactly where it was.
    /// </summary>
    [Fact]
    public void TheOldWholeFolderLink_IsReplacedWithoutLosingWorlds()
    {
        MakeWorld("Chebupeli", "LL8 Extended");
        MakeWorld("Sky Factory", "ATM10");
        Directory.CreateDirectory(Instance);
        SavesFolderService.CreateJunction(Saves, Worlds);
        Assert.True(File.Exists(Path.Combine(Saves, "Sky Factory", "level.dat")));

        new SavesFolderService().Prepare(Worlds, Instance, "LL8 Extended");

        Assert.False(File.Exists(Path.Combine(Saves, "Sky Factory", "level.dat")));
        Assert.True(File.Exists(Path.Combine(Worlds, "Sky Factory", "level.dat")));
        Assert.True(File.Exists(Path.Combine(Saves, "Chebupeli", "level.dat")));
    }

    /// <summary>A world that left takes its way in with it.</summary>
    [Fact]
    public void AWorldThatWentAway_LeavesNoDeadLink()
    {
        MakeWorld("Chebupeli", "LL8 Extended");
        var service = new SavesFolderService();
        service.Prepare(Worlds, Instance, "LL8 Extended");

        Directory.Delete(Path.Combine(Worlds, "Chebupeli"), recursive: true);
        var changes = service.Prepare(Worlds, Instance, "LL8 Extended");

        Assert.Equal(1, changes.Hidden);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Saves));
    }

    /// <summary>
    /// An empty folder is not a world, and must not stand in front of one.
    /// </summary>
    /// <remarks>
    /// A crash that made a world and never wrote it, or a world handed away,
    /// can leave a bare folder in the shared store. Adoption asked only whether
    /// a folder of that name existed - one line after establishing that a world
    /// is a folder with a level.dat in it - so the husk blocked every world of
    /// that name from every build, permanently. The name it blocked was "New
    /// World", which is the one Minecraft offers by default, so two packs and
    /// two real worlds were invisible at once.
    /// </remarks>
    [Fact]
    public void AnEmptyFolderInTheStore_DoesNotBlockARealWorld()
    {
        Directory.CreateDirectory(Path.Combine(Worlds, "New World"));
        MakeInstanceWorld("New World");

        var adopted = new SavesFolderService().Adopt(Worlds, Instance);

        Assert.Equal(1, adopted);
        Assert.True(File.Exists(Path.Combine(Worlds, "New World", "level.dat")));
        Assert.Single(Directory.GetDirectories(Worlds));
    }

    /// <summary>
    /// A real world of the same name does not stop it either - it moves over
    /// under a free folder name, the way Minecraft itself names around a clash.
    /// </summary>
    /// <remarks>
    /// Two packs both offered "New World" and the player took it both times,
    /// which is not a corner case: it is the name the game suggests. Leaving
    /// the second one behind hid 88 MB of played world from the transfer list
    /// with no way to get it out. Nothing is renamed for the player - the name
    /// they see is inside level.dat and is not touched. The folder is only how
    /// the launcher tells two worlds apart.
    /// </remarks>
    [Fact]
    public void ARealWorldOfTheSameName_MovesOverUnderAFreeName()
    {
        MakeWorld("New World", build: null);
        MakeInstanceWorld("New World");

        var adopted = new SavesFolderService().Adopt(Worlds, Instance);

        Assert.Equal(1, adopted);
        // Both are in the store, and the first one was not disturbed.
        Assert.True(File.Exists(Path.Combine(Worlds, "New World", "level.dat")));
        Assert.True(File.Exists(Path.Combine(Worlds, "New World (1)", "level.dat")));
        // And the instance links it under the name it was stored as, so the
        // linking pass does not add a second junction to the same world.
        Assert.True(Directory.Exists(Path.Combine(Saves, "New World (1)")));
    }

    /// <summary>A third of the same name keeps counting.</summary>
    [Fact]
    public void AThirdWorldOfTheSameName_CountsOn()
    {
        MakeWorld("New World", build: null);
        Directory.CreateDirectory(Path.Combine(Worlds, "New World (1)"));
        File.WriteAllBytes(Path.Combine(Worlds, "New World (1)", "level.dat"), new byte[16]);
        MakeInstanceWorld("New World");

        Assert.Equal(1, new SavesFolderService().Adopt(Worlds, Instance));
        Assert.True(File.Exists(Path.Combine(Worlds, "New World (2)", "level.dat")));
    }

    /// <summary>
    /// An empty folder where the junction belongs is taken back too, for the
    /// same reason: nothing is lost, and leaving it hides a world.
    /// </summary>
    [Fact]
    public void AnEmptyFolderInTheInstance_DoesNotBlockTheJunction()
    {
        MakeWorld("Chebupeli", "LL8 Extended");
        Directory.CreateDirectory(Path.Combine(Saves, "Chebupeli"));

        var changes = new SavesFolderService().Prepare(Worlds, Instance, "LL8 Extended");

        Assert.Equal(1, changes.Shown);
        Assert.True(File.Exists(Path.Combine(Saves, "Chebupeli", "level.dat")));
    }

    /// <summary>
    /// A folder with anything in it is somebody's, empty of worlds or not: the
    /// world goes in beside it rather than through it.
    /// </summary>
    [Fact]
    public void AFolderWithFilesInIt_IsNeverTakenBack()
    {
        var stray = Path.Combine(Worlds, "New World");
        Directory.CreateDirectory(stray);
        File.WriteAllText(Path.Combine(stray, "notes.txt"), "mine");
        MakeInstanceWorld("New World");

        Assert.Equal(1, new SavesFolderService().Adopt(Worlds, Instance));

        Assert.Equal("mine", File.ReadAllText(Path.Combine(stray, "notes.txt")));
        Assert.False(File.Exists(Path.Combine(stray, "level.dat")));
        Assert.True(File.Exists(Path.Combine(Worlds, "New World (1)", "level.dat")));
    }

    private string MakeInstanceWorld(string name)
    {
        var world = Path.Combine(Saves, name);
        Directory.CreateDirectory(world);
        File.WriteAllBytes(Path.Combine(world, "level.dat"), new byte[16]);
        return world;
    }

    /// <summary>
    /// Deleting a world in the game empties the folder it lived in; nothing
    /// then empties the folder.
    /// </summary>
    [Fact]
    public void TheShellOfAWorldDeletedInTheGame_IsSweptUp()
    {
        MakeWorld("Chebupeli", "LL8 Extended");
        new SavesFolderService().Prepare(Worlds, Instance, "LL8 Extended");

        // What the game does: the files go through the junction, and the
        // junction goes with them. Only the shared folder is left.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Worlds, "Chebupeli")))
        {
            File.Delete(file);
        }
        Directory.Delete(Path.Combine(Saves, "Chebupeli"));

        PackInstanceService.CleanupEmptyWorldPlaceholders(Worlds);

        Assert.False(Directory.Exists(Path.Combine(Worlds, "Chebupeli")));
    }

    /// <summary>The placeholders go first, and then the world they were left in.</summary>
    [Fact]
    public void AShellHoldingOnlyEmptyPlaceholders_IsSweptUp()
    {
        var world = Path.Combine(Worlds, "New World");
        Directory.CreateDirectory(Path.Combine(world, "datapacks"));
        Directory.CreateDirectory(Path.Combine(world, "EnderStorage"));

        PackInstanceService.CleanupEmptyWorldPlaceholders(Worlds);

        Assert.False(Directory.Exists(world));
    }

    /// <summary>A world is a world while one file of it is left.</summary>
    [Fact]
    public void AWorldWithAnythingInIt_IsNeverSweptUp()
    {
        MakeWorld("Chebupeli", "LL8 Extended");
        Directory.CreateDirectory(Path.Combine(Worlds, "Chebupeli", "datapacks"));

        PackInstanceService.CleanupEmptyWorldPlaceholders(Worlds);

        Assert.True(File.Exists(Path.Combine(Worlds, "Chebupeli", "level.dat")));
    }

    private void MakeWorld(string name, string? build)
    {
        var world = Path.Combine(Worlds, name);
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
