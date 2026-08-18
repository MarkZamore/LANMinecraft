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
    public void ACleanPathGrowsByAQuarter()
    {
        Assert.Equal(10 * MiB, SendRateGovernor.Decide(8 * MiB, 1f, previousQuality: 1f, holding: false));
        Assert.Equal(5 * MiB, SendRateGovernor.Decide(4 * MiB, 0.99f, previousQuality: 0.99f, holding: false));
    }

    /// <summary>
    /// A relayed connection lives in the high nineties and dips constantly.
    /// 0.95 has to count as room to spare, or a rate that fell once never
    /// climbs: in a real 338 MiB transfer nine readings in ten sat under
    /// 2 MiB/s after a single relay change, because 0.98 almost never came.
    /// </summary>
    [Fact]
    public void AGoodRelayCountsAsCleanAndGrows()
    {
        Assert.True(SendRateGovernor.Decide(8 * MiB, 0.95f, previousQuality: 1f, holding: false) > 8 * MiB);
    }

    /// <summary>Between the two thresholds the rate simply stays put.</summary>
    [Fact]
    public void MildLossHoldsSteady()
    {
        Assert.Equal(8 * MiB, SendRateGovernor.Decide(8 * MiB, 0.93f, previousQuality: 1f, holding: false));
    }

    [Fact]
    public void TheHoldStopsRepeatCutsFromAStaleAverage()
    {
        // The figure is still low but the cut has already been made: no second cut,
        // and no growth either until the hold is over.
        Assert.Equal(8 * MiB, SendRateGovernor.Decide(8 * MiB, 0.6f, previousQuality: 0.64f, holding: true));
        Assert.Equal(8 * MiB, SendRateGovernor.Decide(8 * MiB, 1f, previousQuality: 0.64f, holding: true));
    }

    [Fact]
    public void ARecoveringFigureIsNotANewEvent()
    {
        // 0.64 -> 0.80 is loss draining out of the average, not fresh loss.
        Assert.Equal(8 * MiB, SendRateGovernor.Decide(8 * MiB, 0.80f, previousQuality: 0.64f, holding: false));
        // 0.80 -> 0.64 is fresh loss.
        Assert.True(SendRateGovernor.Decide(8 * MiB, 0.64f, previousQuality: 0.80f, holding: false) < 8 * MiB);
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

    /// <summary>
    /// The floor is a working transfer: a world of a few hundred megabytes
    /// still moves in minutes there, where the old floor of 256 KiB/s turned
    /// it into twenty minutes and stayed.
    /// </summary>
    [Fact]
    public void TheFloorIsStillATransfer()
    {
        Assert.Equal(2 * MiB, SendRateGovernor.MinimumBytesPerSecond);
        Assert.Equal(
            SendRateGovernor.MinimumBytesPerSecond,
            SendRateGovernor.Decide(SendRateGovernor.MinimumBytesPerSecond, 0.1f, previousQuality: 1f, holding: false));
    }

    /// <summary>
    /// What the bad transfer needed and did not have: after a relay change
    /// knocks the rate to the floor, a clean path has to bring it back inside
    /// a minute. Readings come every three seconds.
    /// </summary>
    [Fact]
    public void AfterACutACleanPathIsBackInsideAMinute()
    {
        var rate = SendRateGovernor.MinimumBytesPerSecond;
        var readings = 0;
        while (rate < 16 * MiB && readings < 20)
        {
            rate = SendRateGovernor.Decide(rate, 1f, previousQuality: 1f, holding: false);
            readings++;
        }

        Assert.True(rate >= 16 * MiB, $"the rate only reached {rate / MiB} MiB/s");
        Assert.True(readings <= 20, $"it took {readings} readings, {readings * 3} seconds");
    }
}
