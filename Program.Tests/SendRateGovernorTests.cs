using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The control law in isolation: Steam is not needed to check that the floor
/// goes down when packets are lost and up when they are not.
/// </summary>
public sealed class SendRateGovernorTests
{
    private const int MiB = 1024 * 1024;

    [Fact]
    public void ALossEventBacksOffByAThird()
    {
        Assert.Equal(6 * MiB - 2 * MiB, SendRateGovernor.Decide(6 * MiB, 0.15f, previousQuality: -1f, holding: false));
        Assert.Equal(2 * MiB, SendRateGovernor.Decide(3 * MiB, 0.5f, previousQuality: 1f, holding: false));
    }

    [Fact]
    public void ACleanPathGrowsByAnEighth()
    {
        Assert.Equal(9 * MiB, SendRateGovernor.Decide(8 * MiB, 1f, previousQuality: 1f, holding: false));
        Assert.Equal(MiB + MiB / 8, SendRateGovernor.Decide(MiB, 0.99f, previousQuality: 0.99f, holding: false));
    }

    [Fact]
    public void MildLossHoldsSteady()
    {
        Assert.Equal(8 * MiB, SendRateGovernor.Decide(8 * MiB, 0.95f, previousQuality: 1f, holding: false));
    }

    [Fact]
    public void TheHoldStopsRepeatCutsFromAStaleAverage()
    {
        // The figure is still low but the cut has already been made: no second cut,
        // and no growth either until the hold is over.
        Assert.Equal(2 * MiB, SendRateGovernor.Decide(2 * MiB, 0.6f, previousQuality: 0.64f, holding: true));
        Assert.Equal(2 * MiB, SendRateGovernor.Decide(2 * MiB, 1f, previousQuality: 0.64f, holding: true));
    }

    [Fact]
    public void ARecoveringFigureIsNotANewEvent()
    {
        // 0.64 -> 0.80 is loss draining out of the average, not fresh loss.
        Assert.Equal(2 * MiB, SendRateGovernor.Decide(2 * MiB, 0.80f, previousQuality: 0.64f, holding: false));
        // 0.80 -> 0.64 is fresh loss.
        Assert.True(SendRateGovernor.Decide(2 * MiB, 0.64f, previousQuality: 0.80f, holding: false) < 2 * MiB);
    }

    [Fact]
    public void RateStaysInsideSteamsLimits()
    {
        Assert.Equal(SendRateGovernor.MinimumBytesPerSecond,
            SendRateGovernor.Decide(SendRateGovernor.MinimumBytesPerSecond, 0f, -1f, false));
        Assert.Equal(SendRateGovernor.MaximumBytesPerSecond,
            SendRateGovernor.Decide(SendRateGovernor.MaximumBytesPerSecond, 1f, 1f, false));
        Assert.Equal(SendRateGovernor.MaximumBytesPerSecond,
            SendRateGovernor.Decide(SendRateGovernor.MaximumBytesPerSecond - 1, 1f, 1f, false));
    }

    [Fact]
    public void RecoversFromTheFloorInSteps()
    {
        var rate = SendRateGovernor.MinimumBytesPerSecond;
        rate = SendRateGovernor.Decide(rate, 1f, 1f, false);
        Assert.Equal(SendRateGovernor.MinimumBytesPerSecond + 128 * 1024, rate);
    }
}
