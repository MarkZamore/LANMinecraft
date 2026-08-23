using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Which worlds a build is allowed to offer. A world carries the pack it was
/// made on, and opening it on another one is how a world loses the blocks and
/// entities of mods that pack does not have - so the list is filtered rather
/// than left to the player's memory.
/// </summary>
public sealed class WorldBuildOwnershipTests
{
    private const string Current = PortablePackSyncService.DefaultPackRelativePath;

    [Fact]
    public void AWorldOfThisBuild_IsOffered()
    {
        Assert.True(WorldMetadataService.BelongsToBuild(Current, Current));
        Assert.True(WorldMetadataService.BelongsToBuild("ATM10", "ATM10"));
    }

    [Fact]
    public void AWorldOfAnotherBuild_IsNotOffered()
    {
        Assert.False(WorldMetadataService.BelongsToBuild("ATM10", Current));
        Assert.False(WorldMetadataService.BelongsToBuild(Current, "ATM10"));
    }

    /// <summary>
    /// The rename must not hide anybody's world: a world stamped with a name the
    /// built-in pack used to have is still that pack's, and is offered under the
    /// new name until the migration rewrites the stamp.
    /// </summary>
    [Fact]
    public void AWorldOfAFormerNameOfThisPack_IsStillOffered()
    {
        foreach (var legacy in LegacyPackMigrationService.LegacyPackRelativePaths)
        {
            if (string.Equals(legacy, Current, StringComparison.OrdinalIgnoreCase)) continue;
            Assert.True(WorldMetadataService.BelongsToBuild(legacy, Current));
            // ...but only for the pack that was renamed, never for a custom one.
            Assert.False(WorldMetadataService.BelongsToBuild(legacy, "ATM10"));
        }
    }

    /// <summary>
    /// A world nobody stamped cannot be attributed, and hiding it would be the
    /// launcher losing a world rather than protecting one.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AWorldWithoutARecordedBuild_IsOfferedEverywhere(string? recorded)
    {
        Assert.True(WorldMetadataService.BelongsToBuild(recorded, Current));
        Assert.True(WorldMetadataService.BelongsToBuild(recorded, "ATM10"));
    }

    [Theory]
    [InlineData("ATM10\\")]
    [InlineData("/ATM10")]
    [InlineData(" ATM10 ")]
    [InlineData("atm10")]
    public void SlashesAndCaseDoNotDecideIt(string recorded)
    {
        Assert.True(WorldMetadataService.BelongsToBuild(recorded, "ATM10"));
        Assert.False(WorldMetadataService.BelongsToBuild(recorded, Current));
    }

    [Fact]
    public void WithNoBuildSelected_NothingIsHidden()
    {
        Assert.True(WorldMetadataService.BelongsToBuild("ATM10", null));
        Assert.True(WorldMetadataService.BelongsToBuild("ATM10", ""));
    }
}
