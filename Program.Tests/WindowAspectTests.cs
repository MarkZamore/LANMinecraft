using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The window keeps the shape of the canvas behind its Viewbox: any other shape
/// is empty bands down one side. The sizing hook is a Win32 message handler, but
/// the arithmetic under it is a plain function, and that is what is checked here.
/// </summary>
public sealed class WindowAspectTests
{
    private const int Left = 1;
    private const int Right = 2;
    private const int Top = 3;
    private const int Bottom = 6;
    private const int BottomRight = 8;

    private const int ChromeWidth = 16;
    private const int ChromeHeight = 39;

    [Theory]
    [InlineData(Left)]
    [InlineData(Right)]
    public void ASideEdge_SetsTheHeightFromTheWidth(int edge)
    {
        // 848 outer - 16 chrome = 832 client; at 1.6 that is 520, plus chrome.
        var fitted = WindowPlacementService.FitOuterSize(848, 999, edge, 1.6d, ChromeWidth, ChromeHeight);

        Assert.Equal(848, fitted.Width);
        Assert.Equal(520 + ChromeHeight, fitted.Height);
    }

    [Theory]
    [InlineData(Top)]
    [InlineData(Bottom)]
    public void ATopOrBottomEdge_SetsTheWidthFromTheHeight(int edge)
    {
        var fitted = WindowPlacementService.FitOuterSize(999, 520 + ChromeHeight, edge, 1.6d, ChromeWidth, ChromeHeight);

        Assert.Equal(520 + ChromeHeight, fitted.Height);
        Assert.Equal(832 + ChromeWidth, fitted.Width);
    }

    /// <summary>A corner could follow either side; it follows the width, always.</summary>
    [Fact]
    public void ACorner_FollowsTheWidth()
    {
        var fitted = WindowPlacementService.FitOuterSize(416 + ChromeWidth, 9999, BottomRight, 1.6d, ChromeWidth, ChromeHeight);

        Assert.Equal(416 + ChromeWidth, fitted.Width);
        Assert.Equal(260 + ChromeHeight, fitted.Height);
    }

    /// <summary>
    /// The ratio is of the client area, so the title bar and borders are taken
    /// off first and put back after - otherwise the canvas is letterboxed by
    /// exactly the height of the chrome.
    /// </summary>
    [Fact]
    public void TheChrome_IsOutsideTheRatio()
    {
        var square = WindowPlacementService.FitOuterSize(400 + ChromeWidth, 0, Right, 1d, ChromeWidth, ChromeHeight);

        Assert.Equal(400 + ChromeHeight, square.Height);
    }

    /// <summary>A window that is already the right shape is left exactly as it is.</summary>
    [Fact]
    public void AFittingSize_IsUnchanged()
    {
        var fitted = WindowPlacementService.FitOuterSize(
            832 + ChromeWidth, 520 + ChromeHeight, BottomRight, 1.6d, ChromeWidth, ChromeHeight);

        Assert.Equal((832 + ChromeWidth, 520 + ChromeHeight), fitted);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1.5d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ANonsenseRatio_LeavesTheSizeAlone(double aspect)
    {
        var fitted = WindowPlacementService.FitOuterSize(700, 300, Right, aspect, ChromeWidth, ChromeHeight);

        Assert.Equal((700, 300), fitted);
    }

    /// <summary>
    /// Dragging an edge past the chrome must not ask for a negative client area;
    /// the result stays a size a window can actually have.
    /// </summary>
    [Fact]
    public void ASizeSmallerThanTheChrome_StaysPositive()
    {
        var fitted = WindowPlacementService.FitOuterSize(4, 4, Right, 1.6d, ChromeWidth, ChromeHeight);

        Assert.True(fitted.Height > 0, $"height {fitted.Height}");
    }
}
