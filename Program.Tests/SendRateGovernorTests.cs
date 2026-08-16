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
    public void LossHalvesTheRate()
    {
        Assert.Equal(4 * MiB, SendRateGovernor.Decide(8 * MiB, 0.15f));
        Assert.Equal(2 * MiB, SendRateGovernor.Decide(4 * MiB, 0.5f));
    }

    [Fact]
    public void ACleanPathRaisesTheRateByAQuarter()
    {
        Assert.Equal(10 * MiB, SendRateGovernor.Decide(8 * MiB, 1f));
        Assert.Equal(5 * MiB, SendRateGovernor.Decide(4 * MiB, 0.99f));
    }

    [Fact]
    public void MildLossHoldsSteady()
    {
        Assert.Equal(8 * MiB, SendRateGovernor.Decide(8 * MiB, 0.95f));
    }

    [Fact]
    public void RateStaysInsideSteamsLimits()
    {
        Assert.Equal(SendRateGovernor.MinimumBytesPerSecond,
            SendRateGovernor.Decide(SendRateGovernor.MinimumBytesPerSecond, 0f));
        Assert.Equal(SendRateGovernor.MaximumBytesPerSecond,
            SendRateGovernor.Decide(SendRateGovernor.MaximumBytesPerSecond, 1f));
        // Just under the ceiling must not overflow int on the way up.
        Assert.Equal(SendRateGovernor.MaximumBytesPerSecond,
            SendRateGovernor.Decide(SendRateGovernor.MaximumBytesPerSecond - 1, 1f));
    }

    [Fact]
    public void RecoversFromTheFloorInSteps()
    {
        // From the floor a clean path climbs back; the fixed step matters when
        // a quarter of the rate would be a rounding error.
        var rate = SendRateGovernor.MinimumBytesPerSecond;
        rate = SendRateGovernor.Decide(rate, 1f);
        Assert.Equal(2 * SendRateGovernor.MinimumBytesPerSecond, rate);
        rate = SendRateGovernor.Decide(rate, 1f);
        Assert.Equal(3 * SendRateGovernor.MinimumBytesPerSecond, rate);
    }
}
