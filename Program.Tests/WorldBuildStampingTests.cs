using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// How a world that nobody stamped gets a build.
///
/// The list offers such a world in every build, because hiding one the launcher
/// cannot place would be losing it. That is right for the listing and wrong
/// forever after: a world older than this metadata, or copied into the folder
/// by hand, would stay ambiguous, and opening it under the wrong build is how
/// the blocks of every mod that build lacks are lost.
///
/// The launcher cannot know which world the game will open - it prepares all of
/// them and the player chooses inside the game. It can know which one was
/// opened, because the game writes that world's session.lock. So the build is
/// written afterwards, from what happened.
/// </summary>
public sealed class WorldBuildStampingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-world-stamp-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void TheWorldThatWasPlayed_GetsTheBuildItWasPlayedOn()
    {
        var service = new WorldMetadataService();
        var started = DateTimeOffset.UtcNow;
        var played = CreateWorld("Chebupeli", openedAt: started.AddMinutes(1));

        var stamped = service.StampPlayedWorlds(_root, Context("LL8 Extended"), started);

        Assert.Equal(["Chebupeli"], stamped);
        var metadata = service.Read(played);
        Assert.NotNull(metadata);
        Assert.Equal("LL8 Extended", metadata!.BuildRelativePath);
        Assert.True(WorldMetadataService.BelongsToBuild(metadata.BuildRelativePath, "LL8 Extended"));
        Assert.False(WorldMetadataService.BelongsToBuild(metadata.BuildRelativePath, "ATM10"));
    }

    /// <summary>
    /// Every world is prepared before a launch, whichever one is opened. Only
    /// the one that was actually opened may be claimed.
    /// </summary>
    [Fact]
    public void AWorldThatWasOnlyPreparedForLaunch_IsLeftAlone()
    {
        var service = new WorldMetadataService();
        var started = DateTimeOffset.UtcNow;
        var untouched = CreateWorld("Elsewhere", openedAt: started.AddMinutes(-30));

        var stamped = service.StampPlayedWorlds(_root, Context("LL8 Extended"), started);

        Assert.Empty(stamped);
        Assert.Null(service.Read(untouched));
    }

    /// <summary>
    /// A world that already says where it belongs is never re-labelled, even if
    /// it was somehow opened somewhere else.
    /// </summary>
    [Fact]
    public void AWorldThatAlreadyHasABuild_KeepsIt()
    {
        var service = new WorldMetadataService();
        var started = DateTimeOffset.UtcNow;
        var world = CreateWorld("Chebupeli", openedAt: started.AddMinutes(1));
        service.EnsureMetadata(world, Context("LL8 Extended"));

        var stamped = service.StampPlayedWorlds(_root, Context("ATM10"), started);

        Assert.Empty(stamped);
        Assert.Equal("LL8 Extended", service.Read(world)!.BuildRelativePath);
    }

    /// <summary>
    /// Nothing to stamp with, nothing stamped: a launcher that does not know
    /// which build it is running must not put its guess on a world.
    /// </summary>
    [Fact]
    public void WithoutABuild_NothingIsClaimed()
    {
        var service = new WorldMetadataService();
        var started = DateTimeOffset.UtcNow;
        CreateWorld("Chebupeli", openedAt: started.AddMinutes(1));

        Assert.Empty(service.StampPlayedWorlds(_root, Context(""), started));
    }

    /// <summary>A folder without a level.dat is not a world.</summary>
    [Fact]
    public void AFolderThatIsNotAWorld_IsIgnored()
    {
        var service = new WorldMetadataService();
        var started = DateTimeOffset.UtcNow;
        var path = Path.Combine(_root, "not-a-world");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "session.lock"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(path, "session.lock"), started.AddMinutes(1).UtcDateTime);

        Assert.Empty(service.StampPlayedWorlds(_root, Context("LL8 Extended"), started));
    }

    private string CreateWorld(string name, DateTimeOffset openedAt)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "level.dat"), "not really nbt");
        var lockFile = Path.Combine(path, "session.lock");
        File.WriteAllText(lockFile, "☃");
        File.SetLastWriteTimeUtc(lockFile, openedAt.UtcDateTime);
        return path;
    }

    private static WorldMetadataContext Context(string build) => new()
    {
        BuildName = build,
        BuildRelativePath = build,
        PackHash = new string('a', 64),
        OwnerIdentityId = Guid.NewGuid().ToString("D"),
        OwnerIdentityName = "anuvenn"
    };
}
