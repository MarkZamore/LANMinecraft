namespace Minecraft.Tests;

public sealed class GameProcessTreeTests
{
    [Fact]
    public void OwnershipFollowsParentsAndRejectsUnrelatedMissingAndCyclicAncestry()
    {
        var parents = new Dictionary<int, int> { [20] = 10, [30] = 20, [40] = 41, [41] = 40, [50] = 99 };
        Assert.True(GameProcessTree.BelongsTo(10, 10, parents));
        Assert.True(GameProcessTree.BelongsTo(10, 20, parents));
        Assert.True(GameProcessTree.BelongsTo(10, 30, parents));
        Assert.False(GameProcessTree.BelongsTo(10, 40, parents));
        Assert.False(GameProcessTree.BelongsTo(10, 50, parents));
        Assert.False(GameProcessTree.BelongsTo(10, 999, parents));
        Assert.False(GameProcessTree.BelongsTo(0, 10, parents));
        Assert.False(GameProcessTree.BelongsTo(10, 0, parents));
    }
}
