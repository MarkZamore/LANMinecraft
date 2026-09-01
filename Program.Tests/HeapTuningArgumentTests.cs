using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The collector flags the game is started with.
///
/// A JVM does not warn about an option it dislikes - it refuses to start, and
/// the player gets "Could not create the Java Virtual Machine" with no game
/// behind it. That happened: G1NewSizePercent is an experimental option and
/// needs -XX:+UnlockExperimentalVMOptions written before it, so a release went
/// out that no one could launch. These pin the lists so the next flag added has
/// to be looked at.
///
/// There are two lists, because there are two problems. On twelve gigabytes the
/// enemy is a pause somebody sees; on a heap of three it is the machine itself,
/// and the tuning that buys smoothness on the first buys paging on the second.
/// </summary>
public sealed class HeapTuningArgumentTests
{
    private static readonly string[] NeedAnUnlock =
    [
        "G1NewSizePercent", "G1MaxNewSizePercent", "G1MixedGCLiveThresholdPercent",
        "G1MixedGCCountTarget", "G1OldCSetRegionThresholdPercent", "G1EagerReclaimRemSetThreshold"
    ];

    /// <summary>
    /// Both lists use experimental options now, and that is the one thing about
    /// either that can stop a game from starting at all. The unlock has to come
    /// first - not merely be present - because HotSpot reads the command line in
    /// order and refuses an experimental option it meets before the switch that
    /// allows it.
    /// </summary>
    /// <remarks>
    /// The large-heap list used to keep to product options and was pinned that
    /// way. It stopped doing so when a floor was put under the young generation:
    /// a pause goal on its own only tells G1 to shrink it, which is the half of
    /// the pair that causes premature promotion rather than the half that stops
    /// it. So the rule that guarded the small list now guards both.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EachListUnlocksBeforeItUsesAnExperimentalOption(bool small)
    {
        var flags = small
            ? MinecraftProcessService.SmallHeapTuningArguments
            : MinecraftProcessService.HeapTuningArguments;
        var unlock = flags.ToList().FindIndex(
            flag => flag.Contains("UnlockExperimentalVMOptions", StringComparison.Ordinal));

        Assert.True(unlock >= 0, "the list uses experimental options, so it must unlock them");
        for (var index = 0; index < flags.Count; index++)
        {
            foreach (var name in NeedAnUnlock)
            {
                if (!flags[index].Contains(name, StringComparison.Ordinal)) continue;
                Assert.True(
                    index > unlock,
                    $"{flags[index]} is written before the unlock and the JVM will refuse to start");
            }
        }
    }

    /// <summary>
    /// The lists themselves, so adding to either is a decision somebody made on
    /// purpose and checked against the pinned runtime first.
    /// </summary>
    [Fact]
    public void TheListsAreWhatWasChecked()
    {
        Assert.Equal(
            [
                "-XX:+UseG1GC",
                "-XX:+ParallelRefProcEnabled",
                "-XX:MaxGCPauseMillis=50",
                "-XX:+UnlockExperimentalVMOptions",
                "-XX:G1NewSizePercent=20",
                "-XX:G1MaxNewSizePercent=40",
                "-XX:G1HeapRegionSize=32M",
                "-XX:G1ReservePercent=15",
                "-XX:+ExplicitGCInvokesConcurrent"
            ],
            MinecraftProcessService.HeapTuningArguments);

        Assert.Equal(
            [
                "-XX:+UseG1GC",
                "-XX:+ParallelRefProcEnabled",
                "-XX:MaxGCPauseMillis=150",
                "-XX:+UnlockExperimentalVMOptions",
                "-XX:G1NewSizePercent=20",
                "-XX:G1MaxNewSizePercent=40",
                "-XX:G1HeapRegionSize=8M",
                "-XX:G1ReservePercent=20",
                "-XX:InitiatingHeapOccupancyPercent=20",
                "-XX:+DisableExplicitGC"
            ],
            MinecraftProcessService.SmallHeapTuningArguments);
    }

    /// <summary>Every one of them is a flag, not an empty string or a stray word.</summary>
    [Fact]
    public void EveryEntryIsAnOption()
    {
        foreach (var list in new[]
                 {
                     MinecraftProcessService.HeapTuningArguments,
                     MinecraftProcessService.SmallHeapTuningArguments
                 })
        {
            Assert.All(list, argument =>
            {
                Assert.StartsWith("-XX:", argument, StringComparison.Ordinal);
                Assert.DoesNotContain(' ', argument);
            });
        }
    }

    /// <summary>
    /// Which list a heap gets. The boundary is four gigabytes: at or under it
    /// the machine is short of memory rather than short of frames.
    /// </summary>
    /// <remarks>
    /// Compared flag by flag apart from the two young-generation percentages,
    /// which are worked out from the heap rather than written down - see the
    /// theory below for what they come to.
    /// </remarks>
    [Theory]
    [InlineData(2048, false)]
    [InlineData(3584, false)]
    [InlineData(4096, false)]
    [InlineData(5120, true)]
    [InlineData(16384, true)]
    public void AHeapGetsTheTuningItsSizeCallsFor(int heapMb, bool expectTheLargeList)
    {
        static string[] WithoutTheYoungBand(IEnumerable<string> flags) => flags
            .Where(flag => !flag.StartsWith("-XX:G1NewSizePercent=", StringComparison.Ordinal) &&
                           !flag.StartsWith("-XX:G1MaxNewSizePercent=", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(
            WithoutTheYoungBand(expectTheLargeList
                ? MinecraftProcessService.HeapTuningArguments
                : MinecraftProcessService.SmallHeapTuningArguments),
            WithoutTheYoungBand(MinecraftProcessService.HeapTuningArgumentsFor(heapMb)));
    }

    /// <summary>
    /// And where the heap starts. A large one starts at its maximum, because a
    /// heap that grows does it in steps a player feels. A small one must not:
    /// committing three and a half gigabytes of an eight gigabyte machine
    /// before a chunk is drawn is what takes the machine down, and there was
    /// nothing spare to promise in the first place.
    /// </summary>
    [Theory]
    [InlineData(16384, 16384)]
    [InlineData(6144, 6144)]
    [InlineData(4096, 1024)]
    [InlineData(3584, 1024)]
    [InlineData(2048, 1024)]
    [InlineData(512, 512)]
    public void AHeapStartsWhereItsMachineCanAffordItTo(int heapMb, int expectedStartMb)
    {
        Assert.Equal(expectedStartMb, MinecraftProcessService.InitialHeapMbFor(heapMb));
        Assert.True(
            MinecraftProcessService.InitialHeapMbFor(heapMb) <= heapMb,
            "a heap may not start larger than it is allowed to become");
    }

    /// <summary>
    /// The young generation is a size, not a share. Twenty and forty per cent
    /// were chosen against a four gigabyte heap; the same percentages against
    /// sixteen give a young generation of 3.2 to 6.5 GB, and a collection that
    /// has to evacuate six gigabytes cannot meet a fifty millisecond goal
    /// however it is asked to. Measured: 202 ms on All The Fabric 3 at sixteen.
    /// </summary>
    [Theory]
    [InlineData(4 * 1024, 20, 40)]    // where the percentages came from: unchanged
    [InlineData(2 * 1024, 20, 40)]    // and below it, still unchanged
    [InlineData(8 * 1024, 13, 25)]    // 1 GB floor, 2 GB ceiling: 12.5% and 25%
    [InlineData(16 * 1024, 6, 13)]
    [InlineData(32 * 1024, 3, 6)]
    public void TheYoungGenerationIsBoundedInGigabytesNotPerCent(int heapMb, int floor, int ceiling)
    {
        var flags = MinecraftProcessService.HeapTuningArgumentsFor(heapMb);

        Assert.Contains($"-XX:G1NewSizePercent={floor}", flags);
        Assert.Contains($"-XX:G1MaxNewSizePercent={ceiling}", flags);
    }

    /// <summary>
    /// And what those percentages come to in megabytes, which is the thing that
    /// actually matters: a band that stays put however big the heap is.
    /// </summary>
    [Theory]
    [InlineData(4 * 1024)]
    [InlineData(8 * 1024)]
    [InlineData(16 * 1024)]
    [InlineData(28 * 1024)]
    public void TheYoungCeilingNeverRunsAwayWithTheHeap(int heapMb)
    {
        var flags = MinecraftProcessService.HeapTuningArgumentsFor(heapMb);
        var ceiling = flags.Single(flag => flag.StartsWith("-XX:G1MaxNewSizePercent=", StringComparison.Ordinal));
        var percent = int.Parse(ceiling.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture);
        var megabytes = heapMb * percent / 100;

        Assert.InRange(megabytes, 512, 2304);
    }

    /// <summary>A floor above its own ceiling would be refused by the JVM.</summary>
    [Theory]
    [InlineData(1024)]
    [InlineData(4 * 1024)]
    [InlineData(16 * 1024)]
    [InlineData(64 * 1024)]
    public void TheFloorIsNeverAboveTheCeiling(int heapMb)
    {
        var flags = MinecraftProcessService.HeapTuningArgumentsFor(heapMb);
        int Read(string name) => int.Parse(
            flags.Single(flag => flag.StartsWith(name, StringComparison.Ordinal)).Split('=')[1],
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(Read("-XX:G1NewSizePercent=") <= Read("-XX:G1MaxNewSizePercent="));
    }
}
