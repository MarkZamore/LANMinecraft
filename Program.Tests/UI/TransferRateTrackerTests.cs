namespace Minecraft.Tests;

/// <summary>
/// The speed shown beside a byte count, and the one case it used to get wrong.
/// </summary>
public class TransferRateTrackerTests
{
    private const string Scope = "runtime:InstallingJava:Java 21.0.12.1";

    /// <summary>
    /// A download that starts over under the same name is a new download, and
    /// the speed says so.
    /// </summary>
    /// <remarks>
    /// The tracker used to hold that "a restart changes the stage", and clamped
    /// any backward byte count to the last sample. Two things restart without
    /// changing it: the Java archive falls over to its second source and begins
    /// again at nothing under the same "Java 21.0.12.1", and the loader fetches
    /// its installer and then its libraries under one name. Every later sample
    /// was then pinned to the old high-water mark, the difference across the
    /// window was exactly zero, and the play button read "0 Б/с" for the whole
    /// of the second pass while a hundred and fifty megabytes went by.
    /// </remarks>
    [Fact]
    public void ADownloadThatStartsOver_IsMeasuredAgainRatherThanReadingZero()
    {
        var tracker = new TransferRateTracker();

        tracker.Update(100L * 1024 * 1024, Scope);
        Thread.Sleep(1100);
        var first = tracker.Update(150L * 1024 * 1024, Scope);
        Assert.True(first > 0, "the first pass should have a speed");

        // The mirror died; the retry starts at nothing, under the same name.
        Assert.Equal(0d, tracker.Update(0, Scope));

        tracker.Update(4L * 1024 * 1024, Scope);
        Thread.Sleep(1100);
        var second = tracker.Update(40L * 1024 * 1024, Scope);

        Assert.True(
            second > 0,
            "the restarted download is moving; the old window must not pin the speed at zero");
    }

    /// <summary>
    /// And a small step back is still a wobble, not a restart: one bad sample
    /// must not throw away six seconds of history.
    /// </summary>
    [Fact]
    public void ASampleThatStepsBackALittle_KeepsTheWindow()
    {
        var tracker = new TransferRateTracker();

        tracker.Update(100L * 1024 * 1024, Scope);
        Thread.Sleep(1100);
        var before = tracker.Update(150L * 1024 * 1024, Scope);
        var after = tracker.Update(149L * 1024 * 1024, Scope);

        Assert.True(before > 0);
        Assert.True(after > 0, "a wobble above the window's oldest sample is clamped, not a restart");
    }

    /// <summary>A different subject is a different measurement.</summary>
    [Fact]
    public void ANewScope_StartsFromNothing()
    {
        var tracker = new TransferRateTracker();
        tracker.Update(100L * 1024 * 1024, Scope);
        Thread.Sleep(1100);
        Assert.True(tracker.Update(150L * 1024 * 1024, Scope) > 0);
        Assert.Equal(0d, tracker.Update(150L * 1024 * 1024, "runtime:Downloading:NeoForge"));
    }
}
