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
    /// A name is a name, and nothing forgives a former one. There used to be a
    /// table of every name the built-in pack had been called, so that a world
    /// stamped with an old one was still offered under the new; it is gone, and
    /// with it the idea that the launcher should remember renames at all. A
    /// pack that is renamed leaves the worlds of its old name behind.
    /// </summary>
    [Theory]
    [InlineData("Infinity")]
    [InlineData("LL8")]
    [InlineData("ATM10")]
    public void AWorldOfAFormerName_BelongsToNobodyButThatName(string former)
    {
        Assert.False(WorldMetadataService.BelongsToBuild(former, Current));
        Assert.True(WorldMetadataService.BelongsToBuild(former, former));
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
