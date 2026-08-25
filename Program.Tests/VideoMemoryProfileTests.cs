using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The card, as the launcher reads it. A laptop answers with two of them - the
/// processor's own graphics beside the card - and Windows keeps the key of a
/// card that has been swapped out, so what matters is that the largest present
/// one wins and that an unreadable machine costs nobody any heap.
/// </summary>
public sealed class VideoMemoryProfileTests
{
    private static long Gb(double value) => (long)(value * 1024 * 1024 * 1024);

    /// <summary>
    /// Cards report what they hold to the byte: a card sold as eight gigabytes
    /// answers 8 585 740 288, which is 7.996 of them. Rounded to the nearest,
    /// so the number is the one on the box.
    /// </summary>
    [Theory]
    [InlineData(8_585_740_288, 8)]
    [InlineData(17_171_480_576, 16)]
    [InlineData(4_293_918_720, 4)]
    [InlineData(1_073_741_824, 1)]
    public void ACardsSize_IsTheNumberOnTheBox(long bytes, int expectedGb)
    {
        Assert.Equal(new VideoMemoryProfile(expectedGb), VideoMemoryProfile.FromAdapterBytes([bytes]));
    }

    /// <summary>
    /// The game runs on the card that can hold it, not on the processor's own
    /// graphics beside it, so the largest is the one the sizing is told about.
    /// </summary>
    [Fact]
    public void ALaptopWithTwoAdapters_IsSizedByTheLargerOne()
    {
        var laptop = VideoMemoryProfile.FromAdapterBytes([Gb(1), 8_585_740_288]);

        Assert.Equal(8, laptop.DedicatedGb);
        Assert.True(laptop.IsKnown);
    }

    /// <summary>
    /// And a machine that answers nothing - no driver key, a remote session, a
    /// card with no memory of its own - is unknown rather than zero-sized, so
    /// the sizing charges it nothing at all.
    /// </summary>
    [Fact]
    public void AMachineThatAnswersNothing_IsUnknown()
    {
        Assert.Equal(VideoMemoryProfile.Unknown, VideoMemoryProfile.FromAdapterBytes([]));
        Assert.Equal(VideoMemoryProfile.Unknown, VideoMemoryProfile.FromAdapterBytes([0]));
        Assert.Equal(VideoMemoryProfile.Unknown, VideoMemoryProfile.FromAdapterBytes([-1]));
        Assert.Equal(VideoMemoryProfile.Unknown, default(VideoMemoryProfile));
        Assert.False(VideoMemoryProfile.Unknown.IsKnown);
    }

    /// <summary>
    /// Whatever this machine is, reading it must not throw and must not answer
    /// something a heap could be divided by wrongly: a number of gigabytes, or
    /// nothing.
    /// </summary>
    [Fact]
    public void ReadingThisMachine_AnswersOrSaysNothing()
    {
        var measured = VideoMemoryProfile.Measure();

        Assert.InRange(measured.DedicatedGb, 0, 1024);
        Assert.Equal(measured, VideoMemoryProfile.Measure());
    }
}
