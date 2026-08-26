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

    private static Func<string, object?> Adapter(params (string Name, object? Value)[] values) =>
        name => values.FirstOrDefault(entry => entry.Name == name).Value;

    /// <summary>
    /// The value this was got wrong on. A processor's own graphics have no
    /// memory of their own to report, and the Intel driver fills the old 32-bit
    /// field with 0x7FFFF000 regardless - two gigabytes less a page. Believed,
    /// it was a two gigabyte card; it is not a card at all.
    /// </summary>
    [Fact]
    public void SharedGraphics_AreNotACardWithTwoGigabytes()
    {
        var integrated = Adapter(
            ("DriverDesc", "Intel(R) Arc(TM) B390 GPU"),
            ("HardwareInformation.MemorySize", new byte[] { 0, 240, 255, 127 }));

        Assert.Equal(0, VideoMemoryProfile.DedicatedBytes(integrated));
    }

    /// <summary>A card that does have memory of its own still reports it.</summary>
    [Fact]
    public void ACardWithMemoryOfItsOwn_IsStillRead()
    {
        var discrete = Adapter(
            ("HardwareInformation.qwMemorySize", 8_585_740_288L),
            ("HardwareInformation.MemorySize", new byte[] { 0, 240, 255, 127 }));

        Assert.Equal(8_585_740_288L, VideoMemoryProfile.DedicatedBytes(discrete));
    }

    /// <summary>
    /// And what believing it cost, on the pack and the machine it cost it on:
    /// three hundred and twelve mods on a ten gigabyte budget. Read as a two
    /// gigabyte card, the pack "outgrew" it by four, and the heap was pushed
    /// down onto its floor - which is how a modern kitchen-sink pack came to
    /// run in two gigabytes and die of it twice.
    /// </summary>
    [Fact]
    public void ThePackThatDiedOfIt_KeepsItsHeapNow()
    {
        var pack = new PackMemoryProfile(312, 611_350_362, 0, "1.21.1");

        Assert.Equal(6, MemorySizingService.GetHeapGb(pack, 10, VideoMemoryProfile.Unknown));
        Assert.Equal(2, MemorySizingService.GetHeapGb(pack, 10, new VideoMemoryProfile(2)));
    }
}
