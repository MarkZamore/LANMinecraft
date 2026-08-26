using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The two notations mods declare a Minecraft version in, as they are actually
/// written in the wild.
/// </summary>
/// <remarks>
/// Every string here was taken from a real jar in one of the five packs, spaces
/// and hyphens and all. That is the point of the test: a parser that only
/// handles the tidy forms silently discards the jars that wrote the untidy
/// ones, and there are enough of those to change which version wins.
/// </remarks>
public sealed class VersionRangeTests
{
    [Theory]
    // Maven, as Forge and NeoForge write it.
    [InlineData("[1.21.1]", "1.21.1", true)]
    [InlineData("[1.21.1]", "1.21", false)]
    [InlineData("[1.21,)", "1.21.1", true)]
    [InlineData("[1.21,)", "1.20.1", false)]
    [InlineData("[1.21,1.22)", "1.21.1", true)]
    [InlineData("[1.21,1.22)", "1.22", false)]
    // The space is real: fifty jars in Limitless 8 write it this way.
    [InlineData("[1.21.1, 1.22)", "1.21.1", true)]
    [InlineData("[47,)", "47.3.0", true)]
    // The half-open range whose author meant "1.21.x" and excluded 1.21.1.
    // It has to be read as written - that is what makes voting necessary.
    [InlineData("[1.21,1.21.1)", "1.21.1", false)]
    [InlineData("[1.21,1.21.1)", "1.21", true)]
    // Fabric's comparison form, where a space means "and".
    [InlineData(">=1.18.2", "1.18.2", true)]
    [InlineData(">=1.18.2", "1.18.1", false)]
    [InlineData(">=1.18.2 <1.19", "1.18.2", true)]
    [InlineData(">=1.18.2 <1.19", "1.19", false)]
    [InlineData("1.18.x", "1.18.2", true)]
    [InlineData("1.18.x", "1.19", false)]
    [InlineData("~1.18.2", "1.18.9", true)]
    [InlineData("~1.18.2", "1.19", false)]
    // A real string from All The Fabric 3, trailing hyphen included.
    [InlineData("~1.18.2-", "1.18.2", true)]
    [InlineData("*", "1.21.1", true)]
    [InlineData("1.18.2", "1.18.2", true)]
    [InlineData("1.18.2", "1.18.1", false)]
    [InlineData(">=1.16.2", "1.21.1", true)]
    // Nothing satisfies nothing.
    [InlineData("", "1.21.1", false)]
    [InlineData("${minecraft_version_range}", "1.21.1", false)]
    public void ARangeIsReadAsItsAuthorWroteIt(string range, string version, bool accepted) =>
        Assert.Equal(accepted, VersionRange.Accepts(range, version));

    /// <summary>Versions are ordered by their numbers, so 1.10 follows 1.9.</summary>
    [Fact]
    public void VersionsAreOrderedByNumber()
    {
        Assert.True(VersionOrder.CompareVersions("1.10", "1.9") > 0);
        Assert.True(VersionOrder.CompareVersions("1.21.1", "1.21") > 0);
        Assert.Equal(0, VersionOrder.CompareVersions("1.21", "1.21.0"));
        Assert.True(VersionOrder.CompareVersions("26.2", "1.21.1") > 0);
        // A pre-release is the version it precedes as far as ordering goes;
        // nothing in a pack ever names one, and reading the numbers off the
        // front beats refusing the string.
        Assert.Equal(0, VersionOrder.CompareVersions("1.18.2-pre1", "1.18.2"));
    }
}
