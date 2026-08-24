using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The list of worlds a bug report carries.
///
/// Every build shares one Worlds folder, and the build recorded inside a world
/// is the only thing that decides which pack's list offers it - with a world
/// that has none offered by all of them, on purpose. A player asking why one
/// world shows up under two builds cannot be answered from any log on their
/// machine, because no log writes that label down. The report does.
/// </summary>
public sealed class SupportReportWorldListTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-report-worlds-{Guid.NewGuid():N}");

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
    public void AWorldWithABuild_IsListedUnderIt()
    {
        var paths = Prepare();
        MakeWorld(paths, "Chebupeli", build: "LL8 Extended", owner: "anuvenn", holder: "MarkZamore");

        var world = Assert.Single(SupportDiagnosticSnapshotBuilder.ReadWorlds(paths));

        Assert.Equal("Chebupeli", world.Name);
        Assert.Equal("LL8 Extended", world.BuildName);
        Assert.Equal("LL8 Extended", world.BuildRelativePath);
        Assert.Equal("anuvenn", world.OwnerName);
        Assert.Equal("MarkZamore", world.HolderName);
    }

    /// <summary>
    /// The state that answers the question. A world nobody stamped is shown in
    /// every build's list, so the report has to say "not recorded" in words -
    /// an empty field reads as a gap in the report instead of as the answer.
    /// </summary>
    [Fact]
    public void AWorldWithNoBuild_SaysSoRatherThanNothing()
    {
        var paths = Prepare();
        MakeWorld(paths, "прикол", build: null, owner: "anuvenn", holder: "anuvenn");

        var world = Assert.Single(SupportDiagnosticSnapshotBuilder.ReadWorlds(paths));

        Assert.Equal("не записана", world.BuildName);
        Assert.Equal("не записана", world.BuildRelativePath);
    }

    /// <summary>A world with no metadata file at all is still listed.</summary>
    [Fact]
    public void AWorldWithNoMetadataAtAll_IsStillListed()
    {
        var paths = Prepare();
        var world = Path.Combine(paths.Worlds, "hand-dropped");
        Directory.CreateDirectory(world);
        File.WriteAllBytes(Path.Combine(world, "level.dat"), new byte[16]);

        var listed = Assert.Single(SupportDiagnosticSnapshotBuilder.ReadWorlds(paths));

        Assert.Equal("hand-dropped", listed.Name);
        Assert.Equal("не записана", listed.BuildRelativePath);
    }

    [Fact]
    public void WorldsAreListedInOneOrder_WhateverTheFolderReturns()
    {
        var paths = Prepare();
        foreach (var name in new[] { "zulu", "alpha", "Mike" })
        {
            MakeWorld(paths, name, build: "LL8 Extended", owner: "anuvenn", holder: "anuvenn");
        }

        var worlds = SupportDiagnosticSnapshotBuilder.ReadWorlds(paths);

        Assert.Equal(["alpha", "Mike", "zulu"], worlds.Select(world => world.Name));
    }

    /// <summary>A folder that is not a world is not one in the report either.</summary>
    [Fact]
    public void AFolderWithNoLevelDat_IsNotAWorld()
    {
        var paths = Prepare();
        Directory.CreateDirectory(Path.Combine(paths.Worlds, "not-a-world"));

        Assert.Empty(SupportDiagnosticSnapshotBuilder.ReadWorlds(paths));
    }

    [Fact]
    public void NoWorldsFolder_IsAnEmptyListRatherThanAThrow()
    {
        var paths = new AppPaths(_root);
        Assert.Empty(SupportDiagnosticSnapshotBuilder.ReadWorlds(paths));
    }

    private AppPaths Prepare()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        return paths;
    }

    private static void MakeWorld(AppPaths paths, string name, string? build, string owner, string holder)
    {
        var world = Path.Combine(paths.Worlds, name);
        Directory.CreateDirectory(world);
        File.WriteAllBytes(Path.Combine(world, "level.dat"), new byte[16]);
        var metadata = new WorldMetadata
        {
            WorldId = Guid.NewGuid().ToString("D"),
            BuildName = build ?? string.Empty,
            BuildRelativePath = build ?? string.Empty,
            OwnerIdentityName = owner,
            CurrentHolderIdentityName = holder
        };
        File.WriteAllText(
            Path.Combine(world, WorldMetadataService.MetadataFileName),
            System.Text.Json.JsonSerializer.Serialize(
                metadata, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
    }
}
