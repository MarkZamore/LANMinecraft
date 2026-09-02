namespace Minecraft.Tests;

/// <summary>
/// One skin has to serve every version the launcher can run, and the width is
/// where they disagree.
/// </summary>
/// <remarks>
/// Read out of the clients rather than remembered. 1.7 draws a skin into a hard
/// 64x32 canvas and 1.8 into a 64x64 one, keeping the top-left corner of
/// anything larger; 1.13 through 1.16.5 hand the image to the GPU at whatever
/// size it arrived, which is the only window where a wider file renders as
/// itself; and from 1.17 the client measures it and throws away anything that
/// is not 64 wide, leaving the default character and a line in a log the player
/// will not read.
///
/// e4steam, which is what carries this launcher's LAN play, reaches back to
/// 1.7. So the ceiling is not a compromise between the versions - it is the one
/// width all of them read.
/// </remarks>
public class SkinWidthCeilingTests
{
    [Fact]
    public void TheCeiling_IsTheWidthEveryVersionReads()
    {
        Assert.Equal(64, SkinService.MaxSkinWidth);
    }

    /// <summary>
    /// And the oldest version the launcher supports is the one the ceiling is
    /// taken from, so if e4steam ever reaches further back this has to be
    /// looked at again.
    /// </summary>
    [Fact]
    public void TheOldestSupportedMinecraft_IsStill1_7()
    {
        var oldest = SteamTransportCatalog.Builds
            .Select(build => build.MinimumMinecraftVersion)
            .OrderBy(version => version, Comparer<IReadOnlyList<int>>.Create(SteamTransportCatalog.Compare))
            .First();

        Assert.Equal([1, 7], oldest);
    }

    /// <summary>
    /// The button's own text may not promise a width the game will not take.
    /// It promised "до 4096" while the launcher accepted it and every modern
    /// client refused it.
    /// </summary>
    [Fact]
    public void TheSkinButtonsText_DoesNotPromiseAWidthTheGameRefuses()
    {
        var text = SkinCompatibility.Describe("1.21.1", null);

        Assert.Contains("64", text, StringComparison.Ordinal);
        foreach (var refused in new[] { "4096", "256", "128" })
        {
            Assert.DoesNotContain(refused, text, StringComparison.Ordinal);
        }
    }
}
