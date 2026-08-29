using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Sizing the game by what it was seen taking rather than by what a model says
/// it should take.
///
/// The model is one pack weighed once, and it was measured being wrong on the
/// pack it was fitted to: Limitless 8 on a 24 GB budget was charged 12 GB
/// beside its heap - eight for the pack and four for an eight gigabyte card -
/// and the 12 GB heap that left it was 11.5 GB full, with AllTheLeaks warning
/// at 95% and full collections of 2.2 seconds. The launcher had the right
/// number the whole time and was writing it into a log file: "the game asked
/// for 26989 MB at its largest - a heap of 19456 MB and about 7533 MB beside
/// it". These tests are that number becoming the reserve.
/// </summary>
public sealed class MeasuredMemoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-measured-memory-tests-{Guid.NewGuid():N}");

    public void Dispose() => TempTree.Delete(_root);

    /// <summary>Limitless 8: 1128 mods, 1.9 GB of jars, and the texture beside them.</summary>
    private static PackMemoryProfile BigModpack =>
        new(1128, 1959L * 1024 * 1024, 115L * 1024 * 1024, "1.21.1");

    /// <summary>The session out of the log this whole feature was built on.</summary>
    private static MemorySession TheLoggedSession => new(
        CommittedMb: 26989, ResidentMb: 14687, HeapMb: 19456, Minutes: 95, When: DateTimeOffset.Now);

    private AppPaths Paths()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        return paths;
    }

    /// <summary>
    /// An evening of playing is a measurement; the four minutes it takes to
    /// find out that a mod crashes on the title screen is not. The pack takes
    /// about a minute to reach the menu with its 882 jars, and nothing that
    /// lives beside the heap - chunk buffers above all - exists until a world
    /// has been open for a while.
    /// </summary>
    [Fact]
    public void AShortSession_IsNotBelieved()
    {
        var brief = TheLoggedSession with { Minutes = MemorySession.ShortestSessionMinutes - 1 };

        Assert.False(brief.IsWorthKeeping);
        Assert.Contains("under the", brief.WhyNotKept, StringComparison.Ordinal);
        Assert.True((TheLoggedSession with { Minutes = MemorySession.ShortestSessionMinutes }).IsWorthKeeping);
    }

    /// <summary>
    /// A machine that could not hold what the game asked for was not measuring
    /// the pack, it was negotiating with the page file, and what the pack held
    /// there is smaller than what it wanted.
    /// </summary>
    /// <remarks>
    /// The line is drawn at half of the commit rather than higher because a
    /// healthy session sits well under its own commit too: the heap is
    /// committed the instant the game starts and only becomes resident as it
    /// fills, which is why the session in the log - 14687 MB of 26989, 54% -
    /// has to be kept, and is.
    /// </remarks>
    [Fact]
    public void ASessionOnAPagingMachine_IsNotBelieved()
    {
        Assert.True(TheLoggedSession.IsWorthKeeping);

        var paging = TheLoggedSession with { ResidentMb = 9000 };

        Assert.False(paging.IsWorthKeeping);
        Assert.Contains("paging", paging.WhyNotKept, StringComparison.Ordinal);
    }

    /// <summary>
    /// The subtraction only means anything when the heap was committed from the
    /// first instant. A small heap starts at a gigabyte and grows on purpose,
    /// so subtracting its maximum from the commit would report a pack holding
    /// nothing beside its heap - and hand the smallest machines the largest
    /// heaps.
    /// </summary>
    [Fact]
    public void ASessionWhoseHeapWasStillGrowing_IsNotBelieved()
    {
        var small = new MemorySession(
            CommittedMb: 5000, ResidentMb: 4200,
            HeapMb: MinecraftProcessService.SmallHeapCeilingMb, Minutes: 90, When: DateTimeOffset.Now);

        Assert.False(small.IsWorthKeeping);
        Assert.Contains("starts small and grows", small.WhyNotKept, StringComparison.Ordinal);
    }

    /// <summary>
    /// A session is filed under the pair it happened on, and the machine is
    /// half of that pair: the same pack on a smaller card holds gigabytes more
    /// in system memory, because the driver keeps there what the card cannot.
    /// </summary>
    [Fact]
    public void AMeasurement_BelongsToOnePackOnOneMachine()
    {
        var store = new MeasuredMemoryStore(Paths());

        store.Remember("Infinity", new VideoMemoryProfile(16), 32, TheLoggedSession);

        Assert.True(store.Recall("Infinity", new VideoMemoryProfile(16), 32).IsKnown);
        Assert.False(store.Recall("Limitless8", new VideoMemoryProfile(16), 32).IsKnown);
        Assert.False(store.Recall("Infinity", new VideoMemoryProfile(8), 32).IsKnown);
        Assert.False(store.Recall("Infinity", new VideoMemoryProfile(16), 16).IsKnown);
        // A card nobody could read is its own machine: the sizing charges it
        // nothing, so a measurement taken under that rule is not the answer for
        // a machine whose card answered.
        Assert.False(store.Recall("Infinity", VideoMemoryProfile.Unknown, 32).IsKnown);
        // And the same folder spelled another way is the same folder; Windows
        // has never thought otherwise.
        Assert.True(store.Recall("infinity", new VideoMemoryProfile(16), 32).IsKnown);
    }

    /// <summary>
    /// Several sessions are kept and the largest of them decides, because a
    /// reserve set too low is spent by the game anyway - over the budget and
    /// into the memory the machine kept for itself. The oldest fall off, so a
    /// pack that has grown is described by the evenings since it grew.
    /// </summary>
    [Fact]
    public void TheLastFewSessions_AreKept_AndTheLargestOfThemDecides()
    {
        var store = new MeasuredMemoryStore(Paths());
        var card = new VideoMemoryProfile(16);

        // One unusually heavy evening, then enough ordinary ones to push it out.
        store.Remember(
            "Infinity", card, 32, TheLoggedSession with { CommittedMb = 30000, ResidentMb = 20000 });
        Assert.Equal(30000 - 19456, store.Recall("Infinity", card, 32).AtMostMb);

        for (var session = 0; session < MeasuredMemoryStore.SessionsKept; session++)
        {
            store.Remember("Infinity", card, 32, TheLoggedSession);
        }

        var measured = store.Recall("Infinity", card, 32);
        Assert.Equal(MeasuredMemoryStore.SessionsKept, measured.Sessions);
        Assert.Equal(26989 - 19456, measured.AtMostMb);
    }

    /// <summary>
    /// A file nobody can read is a file nobody has, and the launcher goes on
    /// estimating rather than failing a launch over a cache.
    /// </summary>
    [Fact]
    public void AnUnreadableFile_IsTheSameAsNoFile()
    {
        var paths = Paths();
        File.WriteAllText(Path.Combine(paths.Personal, "memory-measurements.json"), "{ this is not json");
        var store = new MeasuredMemoryStore(paths);

        Assert.False(store.Recall("Infinity", new VideoMemoryProfile(16), 32).IsKnown);

        // And it heals: the next session that finishes writes a file that reads.
        store.Remember("Infinity", new VideoMemoryProfile(16), 32, TheLoggedSession);

        Assert.True(new MeasuredMemoryStore(paths).Recall("Infinity", new VideoMemoryProfile(16), 32).IsKnown);
    }

    /// <summary>
    /// A pack that has never been played through, or a machine whose memory
    /// could not be read at all, has no pair to file anything under.
    /// </summary>
    [Fact]
    public void AMachineOrPackThatCannotBeNamed_IsNotFiled()
    {
        var store = new MeasuredMemoryStore(Paths());

        Assert.False(store.Remember("", new VideoMemoryProfile(16), 32, TheLoggedSession).IsKnown);
        Assert.False(store.Remember("Infinity", new VideoMemoryProfile(16), 0, TheLoggedSession).IsKnown);
        // A session not worth keeping is not written down either.
        Assert.False(store
            .Remember("Infinity", new VideoMemoryProfile(16), 32, TheLoggedSession with { Minutes = 2 })
            .IsKnown);
        Assert.False(store.Recall("Infinity", new VideoMemoryProfile(16), 32).IsKnown);
    }

    /// <summary>
    /// The whole point, in the numbers the player has: a 24 GB budget on
    /// Limitless 8.
    ///
    /// Estimated, the card decides everything. An eight gigabyte card is
    /// charged the four gigabytes the pack outgrows it by on top of the pack's
    /// own eight, and the heap comes out at 12 - the heap that was measured
    /// 11.5 GB full, with full collections of 2.2 seconds. A sixteen gigabyte
    /// card is charged nothing and the same budget leaves 16.
    ///
    /// Measured, the card is not guessed at twice: whatever the driver keeps in
    /// system memory is already inside the 7533 MB the largest session could
    /// have been holding, and the estimate is not allowed above it. The eight
    /// gigabyte card's 12 comes down to 8 - the ceiling as it stands, rounded
    /// up - and its heap goes from 12 to 16. The tenth is added to the floor
    /// only: a floor is one evening's high-water mark and the next may pass it,
    /// while a ceiling is already the largest commit this machine has ever
    /// asked for.
    ///
    /// The sixteen gigabyte card is charged nothing to begin with, its estimate
    /// of 8 is already under that ceiling, and it keeps the 16 GB heap it had.
    /// The measurement is a bound here, not an answer: it has nothing to
    /// correct.
    /// </summary>
    [Theory]
    [InlineData(8, 12, 12, 8, 16)]
    [InlineData(16, 8, 16, 8, 16)]
    public void TheCeilingOnA32GbMachine_IsSetByTheMeasurementWhereThereIsOne(
        int cardGb, int estimatedReserveGb, int estimatedHeapGb, int measuredReserveGb, int measuredHeapGb)
    {
        var card = new VideoMemoryProfile(cardGb);
        var measured = MeasuredMemoryProfile.From([TheLoggedSession]);
        const ulong thirtyTwoGb = 32UL * 1024 * 1024 * 1024;

        Assert.Equal(26989 - 19456, measured.AtMostMb);
        Assert.Equal(estimatedReserveGb, MemorySizingService.GetNativeReserveGb(BigModpack, card));
        Assert.Equal(estimatedHeapGb, MemorySizingService.GetAllowedHeapGb(BigModpack, thirtyTwoGb, card));
        Assert.Equal(measuredReserveGb, MemorySizingService.GetNativeReserveGb(BigModpack, card, measured));
        Assert.Equal(
            measuredHeapGb, MemorySizingService.GetAllowedHeapGb(BigModpack, thirtyTwoGb, card, measured));
    }

    /// <summary>
    /// And it cuts both ways. A machine that really does keep four gigabytes of
    /// textures in system memory gets a smaller heap than the estimate would
    /// have given it, without anybody having guessed.
    /// </summary>
    /// <remarks>
    /// What proves it is the resident half of the pair, not the committed one.
    /// Memory that is resident is memory the machine actually gave up, so an
    /// estimate under it cannot be believed whatever the model says; commit,
    /// which the driver inflates by the whole size of the card, can only ever
    /// say what the game did not exceed. Here a session with an 8 GB heap was
    /// resident at 20000 MB - 11808 MB of it outside the heap - and the
    /// estimate of 12 GB is raised to that floor and a tenth.
    /// </remarks>
    [Fact]
    public void AMachineThatReallyHoldsMore_IsGivenLessHeap()
    {
        var card = new VideoMemoryProfile(8);
        var spilling = MeasuredMemoryProfile.From(
            [TheLoggedSession with { HeapMb = 8192, CommittedMb = 30989, ResidentMb = 20000 }]);

        Assert.Equal(13, MemorySizingService.GetNativeReserveGb(BigModpack, card, spilling));
        Assert.Equal(
            11,
            MemorySizingService.GetAllowedHeapGb(BigModpack, 32UL * 1024 * 1024 * 1024, card, spilling));
    }

    /// <summary>
    /// The evening this rule was rewritten. A sixteen gigabyte card commits its
    /// own size in system memory whether or not a texture is in it, so a
    /// twenty-six minute session of Limitless 8 was written down as holding
    /// 14135 MB beside its heap while 2853 MB of it was resident. Read as an
    /// answer, that turned a 24 GB budget into an 8 GB heap - half of what the
    /// same launcher had given the same pack the day before.
    /// </summary>
    /// <remarks>
    /// The heap was not what broke that evening, as it turned out; the game was
    /// standing in the long silence where the class transformer runs, and
    /// Windows called the loading window hung. But the arithmetic behind the 8
    /// was wrong on its own terms, and this is the number that says so.
    /// </remarks>
    [Fact]
    public void ACardCommittingItsOwnSize_DoesNotHalveTheHeap()
    {
        var card = new VideoMemoryProfile(16);
        var evening = MeasuredMemoryProfile.From(
        [
            TheLoggedSession with { CommittedMb = 30519, ResidentMb = 19237, HeapMb = 16384, Minutes = 26 },
            TheLoggedSession with { CommittedMb = 18768, ResidentMb = 14605, HeapMb = 8192, Minutes = 22 },
        ]);

        Assert.Equal(30519 - 16384, evening.AtMostMb);
        Assert.Equal(14605 - 8192, evening.AtLeastMb);
        Assert.Equal(
            16,
            MemorySizingService.GetAllowedHeapGb(BigModpack, 32UL * 1024 * 1024 * 1024, card, evening));
    }

    /// <summary>
    /// The ceiling follows the measurement and the suggestion does not, which is
    /// the whole shape of the arrangement: what the pack was seen holding beside
    /// its heap decides how much heap the machine can still offer, while the
    /// heap the pack asks for is a property of the pack alone.
    /// </summary>
    [Fact]
    public void TheCeilingFollowsTheMeasurement_AndTheSuggestionIsThePacks()
    {
        var card = new VideoMemoryProfile(8);
        var measured = MeasuredMemoryProfile.From([TheLoggedSession]);
        const ulong thirtyTwoGb = 32UL * 1024 * 1024 * 1024;

        Assert.Equal(24 - 8, MemorySizingService.GetAllowedHeapGb(BigModpack, thirtyTwoGb, card, measured));
        Assert.Equal(24 - 12, MemorySizingService.GetAllowedHeapGb(BigModpack, thirtyTwoGb, card));
        Assert.Equal(
            MemorySizingService.GetRecommendedHeapGb(BigModpack),
            MemorySizingService.GetRecommendedMemoryGb(
                BigModpack, 64UL * 1024 * 1024 * 1024, card, measured));
    }

    /// <summary>
    /// The ceiling on a recommended heap was sixteen, and sixteen was set when
    /// the only thing a large heap had been seen doing was pausing. What has
    /// been seen since is the opposite: 1128 mods in the 12 GB heap this file
    /// recommends for them, 11.5 of it in use, and full collections of 2.2
    /// seconds because there was nowhere left to collect into. Sixteen is
    /// thirty per cent above the pack the whole model was calibrated on, which
    /// makes it a ceiling the next pack up meets instead of meeting its own
    /// arithmetic.
    /// </summary>
    /// <remarks>
    /// It binds on nobody's machine today, which is the other half of the
    /// argument: a 32 GB machine may be asked for 24 GB altogether, and this
    /// pack holds 8-9 of them beside its heap, so twenty is reached from 48 GB
    /// installed upwards on a pack half again the size of the largest one on
    /// record.
    /// </remarks>
    [Fact]
    public void TheHeapCeiling_ClearsThePackTheModelWasFittedTo()
    {
        Assert.True(
            MemorySizingService.MaxRecommendedHeapGb >= 20,
            "a ceiling of 16 is barely above the 12 GB heap that was measured 95% full");

        // A 32 GB machine cannot reach it whatever the pack, so raising it took
        // nothing away from anyone: the machine's own quarter still rules.
        const ulong thirtyTwoGb = 32UL * 1024 * 1024 * 1024;
        var card = new VideoMemoryProfile(8);
        var onATypicalMachine = MemorySizingService.GetRecommendedMemoryGb(BigModpack, thirtyTwoGb, card);

        Assert.InRange(
            onATypicalMachine,
            MemorySizingService.MinHeapGb,
            MemorySizingService.GetAllowedHeapGb(BigModpack, thirtyTwoGb, card));
        Assert.True(onATypicalMachine < MemorySizingService.MaxRecommendedHeapGb);
    }
}
