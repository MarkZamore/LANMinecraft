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
/// and nothing for the rest.
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
        Assert.False(Directory.Exists(Path.Combine(Saves, "Sky Factory")));
        // A world nobody stamped is shown to every build: hiding it would be
        // losing it, and playing it is what gives it a build.
        Assert.True(Directory.Exists(Path.Combine(Saves, "hand-dropped")));
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
        Assert.False(Directory.Exists(Path.Combine(Saves, "Chebupeli")));
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

    /// <summary>Two different worlds under one name is not resolved by guessing.</summary>
    [Fact]
    public void AMadeWorldWhoseNameIsTaken_IsLeftWhereItIs()
    {
        MakeWorld("New World", "ATM10");
        var service = new SavesFolderService();
        service.Prepare(Worlds, Instance, "LL8 Extended");

        var made = Path.Combine(Saves, "New World");
        Directory.CreateDirectory(made);
        File.WriteAllText(Path.Combine(made, "level.dat"), "mine");

        Assert.Equal(0, service.Adopt(Worlds, Instance));

        Assert.Equal("mine", File.ReadAllText(Path.Combine(made, "level.dat")));
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

        Assert.False(Directory.Exists(Path.Combine(Saves, "Sky Factory")));
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
