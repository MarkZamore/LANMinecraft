using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Who gets to decide which build a world belongs to.
///
/// Only playing it does. Listing worlds used to write the selected build into
/// every world that had none, and the filter that hides another build's worlds
/// then compared that fresh label against the build that had just written it -
/// so it always matched, every world showed in every build, and whichever build
/// opened its list first owned the world from then on. That is why a world from
/// LL8 Extended turned up under ATM10.
/// </summary>
public sealed class WorldBuildClaimTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-world-claim-{Guid.NewGuid():N}");

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
    public void LookingAtAWorld_DoesNotClaimItForTheBuildLooking()
    {
        var service = new WorldMetadataService();
        var world = CreateWorld("Chebupeli");

        var metadata = service.EnsureMetadata(world, Context("ATM10"), claimBuild: false);

        Assert.NotNull(metadata);
        Assert.Equal(string.Empty, metadata!.BuildRelativePath);
        // Unattributed means shown everywhere, which is the deliberate fallback -
        // hiding a world nobody can place would be losing it.
        Assert.True(WorldMetadataService.BelongsToBuild(metadata.BuildRelativePath, "ATM10"));
        Assert.True(WorldMetadataService.BelongsToBuild(metadata.BuildRelativePath, "LL8 Extended"));
    }

    /// <summary>
    /// The owner is still recorded, because that part of listing was never the
    /// problem: it says who made the world, not where it may be opened.
    /// </summary>
    [Fact]
    public void LookingAtAWorld_StillRecordsWhoOwnsIt()
    {
        var service = new WorldMetadataService();
        var world = CreateWorld("Chebupeli");

        var metadata = service.EnsureMetadata(world, Context("ATM10"), claimBuild: false);

        Assert.Equal("anuvenn", metadata!.OwnerIdentityName);
        Assert.False(string.IsNullOrWhiteSpace(metadata.WorldId));
    }

    /// <summary>
    /// Playing it does claim it - and then the other build stops offering it,
    /// which is the whole point of the filter.
    /// </summary>
    [Fact]
    public void PlayingAWorld_ClaimsItAndTheOtherBuildStopsShowingIt()
    {
        var service = new WorldMetadataService();
        var started = DateTimeOffset.UtcNow;
        var world = CreateWorld("Chebupeli", playedAt: started.AddMinutes(1));
        service.EnsureMetadata(world, Context("ATM10"), claimBuild: false);

        var stamped = service.StampPlayedWorlds(_root, Context("LL8 Extended"), started);

        Assert.Equal(["Chebupeli"], stamped);
        var recorded = service.Read(world)!.BuildRelativePath;
        Assert.Equal("LL8 Extended", recorded);
        Assert.True(WorldMetadataService.BelongsToBuild(recorded, "LL8 Extended"));
        Assert.False(WorldMetadataService.BelongsToBuild(recorded, "ATM10"));
    }

    /// <summary>
    /// And once claimed, listing it from the other build must not take it back.
    /// </summary>
    [Fact]
    public void AClaimedWorld_IsNotReclaimedByLookingFromElsewhere()
    {
        var service = new WorldMetadataService();
        var started = DateTimeOffset.UtcNow;
        var world = CreateWorld("Chebupeli", playedAt: started.AddMinutes(1));
        service.StampPlayedWorlds(_root, Context("LL8 Extended"), started);

        service.EnsureMetadata(world, Context("ATM10"), claimBuild: false);
        service.EnsureMetadata(world, Context("ATM10"));

        Assert.Equal("LL8 Extended", service.Read(world)!.BuildRelativePath);
    }

    private string CreateWorld(string name, DateTimeOffset? playedAt = null)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "level.dat"), "not really nbt");
        var lockFile = Path.Combine(path, "session.lock");
        File.WriteAllText(lockFile, "x");
        if (playedAt is { } moment) File.SetLastWriteTimeUtc(lockFile, moment.UtcDateTime);
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
