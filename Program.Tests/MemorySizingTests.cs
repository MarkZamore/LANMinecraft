using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The number a player sets is everything the game may take, and only the heap
/// can be handed to Java. What the rest of it comes to belongs to the pack, not
/// to the launcher: a vanilla client holds about a gigabyte outside its heap,
/// and Limitless 8 - measured - held almost eight above a twelve gigabyte heap.
/// So every rule here is asked about a pack, and the same field serves vanilla
/// on an old version and something heavier than Limitless 8 on a new one.
/// </summary>
public sealed class MemorySizingTests
{
    private static ulong Gb(int value) => (ulong)value * 1024 * 1024 * 1024;
    private static long Mb(int value) => (long)value * 1024 * 1024;

    /// <summary>Limitless 8 as it stands: 874 jars, 1.9 GB of them, and the texture beside them.</summary>
    private static PackMemoryProfile BigModpack => new(874, Mb(1959), Mb(115), "1.21.1");
    private static PackMemoryProfile Vanilla => new(0, 0, 0, "1.21.1");
    private static PackMemoryProfile OldVanilla => new(0, 0, 0, "1.7.10");
    private static PackMemoryProfile SmallModpack => new(60, Mb(180), Mb(40), "1.20.1");
    private static PackMemoryProfile HeavierThanLimitless => new(1500, Mb(4000), Mb(500), "1.22");

    [Theory]
    [InlineData(8, 4)]
    [InlineData(16, 12)]
    [InlineData(32, 24)]
    [InlineData(64, 48)]
    public void TheLargestBudgetOffered_LeavesTheMachineItsQuarter(int installedGb, int expected)
    {
        Assert.Equal(expected, MemorySizingService.GetAllowedMaxMemoryGb(Gb(installedGb)));
    }

    /// <summary>A very small machine still gets the floor, not a negative.</summary>
    [Fact]
    public void ASmallMachine_StillGetsTheFloor()
    {
        Assert.Equal(MemorySizingService.MinMemoryGb, MemorySizingService.GetAllowedMaxMemoryGb(Gb(4)));
        Assert.Equal(MemorySizingService.MinMemoryGb, MemorySizingService.GetAllowedMaxMemoryGb(Gb(2)));
    }

    /// <summary>
    /// The pack the model was calibrated on keeps the split it was calibrated
    /// to: eight gigabytes outside the heap, twelve inside, out of twenty.
    /// </summary>
    [Fact]
    public void ThePackTheModelWasMeasuredOn_KeepsItsSplit()
    {
        Assert.Equal(8, MemorySizingService.GetNativeReserveGb(BigModpack));
        Assert.Equal(12, MemorySizingService.GetHeapGb(BigModpack, 20));
        Assert.Equal(20, MemorySizingService.GetRecommendedDefaultMemoryGb(BigModpack, Gb(32)));
    }

    /// <summary>
    /// And vanilla is not charged for mods it does not carry: the same twenty
    /// gigabytes are nearly all heap, and what it is offered is a small number.
    /// </summary>
    [Fact]
    public void APackWithoutMods_IsNotChargedForThem()
    {
        Assert.Equal(1, MemorySizingService.GetNativeReserveGb(Vanilla));
        Assert.Equal(19, MemorySizingService.GetHeapGb(Vanilla, 20));
        Assert.True(
            MemorySizingService.GetHeapGb(Vanilla, 8) > MemorySizingService.GetHeapGb(BigModpack, 8),
            "the same budget must leave vanilla the larger heap");

        var suggested = MemorySizingService.GetRecommendedDefaultMemoryGb(Vanilla, Gb(32));
        Assert.InRange(suggested, MemorySizingService.MinMemoryGb, 6);
    }

    /// <summary>An older Minecraft asks for less than a new one, never more.</summary>
    [Fact]
    public void AnOlderMinecraft_AsksForNoMoreThanANewOne()
    {
        Assert.True(
            MemorySizingService.GetNativeReserveGb(OldVanilla) <= MemorySizingService.GetNativeReserveGb(Vanilla));
        Assert.True(
            MemorySizingService.GetRecommendedDefaultMemoryGb(OldVanilla, Gb(32)) <=
            MemorySizingService.GetRecommendedDefaultMemoryGb(Vanilla, Gb(32)));
    }

    /// <summary>
    /// Weight decides, in that order: nothing, a small pack, Limitless 8, and
    /// something twice its size on a newer version. A pack heavier than any the
    /// launcher has met is sized by the same arithmetic, not by a ceiling.
    /// </summary>
    [Fact]
    public void HeavierPacks_AskForMore_InOrder()
    {
        var reserves = new[] { Vanilla, SmallModpack, BigModpack, HeavierThanLimitless }
            .Select(pack => MemorySizingService.GetNativeReserveGb(pack))
            .ToList();

        Assert.Equal(reserves.OrderBy(value => value), reserves);
        Assert.True(
            MemorySizingService.GetNativeReserveGb(HeavierThanLimitless) >
            MemorySizingService.GetNativeReserveGb(BigModpack),
            "a pack heavier than Limitless 8 must be given more room than it");
        Assert.True(
            MemorySizingService.GetRecommendedDefaultMemoryGb(HeavierThanLimitless, Gb(64)) >
            MemorySizingService.GetRecommendedDefaultMemoryGb(BigModpack, Gb(64)));
    }

    /// <summary>
    /// The card is the machine's half of the same sum. What a pack hands it and
    /// it cannot hold, the driver keeps in system memory, and that copy is the
    /// game's: on identical laptops with identical settings, the one with eight
    /// gigabytes of video memory was at 28.5 GB of its 31.6 with full
    /// collections of 2.2 seconds, and the one with sixteen was not. So the same
    /// budget leaves the smaller card the smaller heap - by itself, without
    /// anyone being told to type a different number.
    /// </summary>
    [Theory]
    [InlineData(0, 8, 16)]   // a card nobody could read costs nothing
    [InlineData(16, 8, 16)]  // and one that holds the pack costs nothing either
    [InlineData(12, 8, 16)]
    [InlineData(10, 10, 14)]
    [InlineData(8, 12, 12)]
    [InlineData(6, 12, 12)]  // past the cap the shortfall stops being charged
    public void ASmallCard_IsChargedToTheRoomBesideTheHeap(int videoGb, int reserveGb, int heapGb)
    {
        var video = new VideoMemoryProfile(videoGb);

        Assert.Equal(reserveGb, MemorySizingService.GetNativeReserveGb(BigModpack, video));
        Assert.Equal(heapGb, MemorySizingService.GetHeapGb(BigModpack, 24, video));
    }

    /// <summary>
    /// And the suggestion moves with it: the pack still gets the heap it wants,
    /// and the number offered grows by what the driver takes - so a player who
    /// never touches the field is not quietly given a heap the machine cannot
    /// hold in memory.
    /// </summary>
    [Fact]
    public void TheSuggestion_MakesRoomForWhatTheDriverKeeps()
    {
        var smallCard = new VideoMemoryProfile(8);
        var largeCard = new VideoMemoryProfile(16);

        var onASmallCard = MemorySizingService.GetRecommendedDefaultMemoryGb(BigModpack, Gb(32), smallCard);
        var onALargeCard = MemorySizingService.GetRecommendedDefaultMemoryGb(BigModpack, Gb(32), largeCard);

        Assert.Equal(24, onASmallCard);
        Assert.Equal(20, onALargeCard);
        Assert.Equal(
            MemorySizingService.GetRecommendedHeapGb(BigModpack),
            MemorySizingService.GetHeapGb(BigModpack, onASmallCard, smallCard));
        Assert.Equal(
            MemorySizingService.GetHeapGb(BigModpack, onALargeCard, largeCard),
            MemorySizingService.GetHeapGb(BigModpack, onASmallCard, smallCard));
    }

    /// <summary>
    /// Only what the pack itself draws is charged. Vanilla on a small card asks
    /// for nothing extra - it fits - and a pack nobody has weighed is charged
    /// nothing whatever the card, because there is nothing to compare it with.
    /// </summary>
    [Fact]
    public void OnlyAPackThatOutgrowsTheCard_IsChargedForIt()
    {
        var smallCard = new VideoMemoryProfile(8);

        Assert.Equal(0, MemorySizingService.GetVideoSpillGb(Vanilla, smallCard));
        Assert.Equal(0, MemorySizingService.GetVideoSpillGb(OldVanilla, smallCard));
        Assert.Equal(0, MemorySizingService.GetVideoSpillGb(SmallModpack, smallCard));
        Assert.True(MemorySizingService.GetVideoSpillGb(BigModpack, smallCard) > 0);
        Assert.True(
            MemorySizingService.GetVideoSpillGb(HeavierThanLimitless, smallCard) >=
            MemorySizingService.GetVideoSpillGb(BigModpack, smallCard),
            "a heavier pack must not ask the card for less");

        Assert.Equal(0, MemorySizingService.GetVideoSpillGb(PackMemoryProfile.Unknown, smallCard));
        Assert.Equal(
            MemorySizingService.GetHeapGb(PackMemoryProfile.Unknown, 24),
            MemorySizingService.GetHeapGb(PackMemoryProfile.Unknown, 24, smallCard));
        Assert.Equal(0, MemorySizingService.GetVideoSpillGb(BigModpack, VideoMemoryProfile.Unknown));
    }

    /// <summary>
    /// A pack the launcher has not been able to look at keeps the rule it used
    /// when it could not tell packs apart at all - so nothing regresses on the
    /// first run, before anything is installed.
    /// </summary>
    [Theory]
    [InlineData(24, 8, 16)]
    [InlineData(20, 8, 12)]
    [InlineData(12, 6, 6)]
    [InlineData(6, 3, 3)]
    public void AnUnseenPack_KeepsTheOlderHalfAndHalfRule(int budgetGb, int reserveGb, int heapGb)
    {
        Assert.Equal(reserveGb, MemorySizingService.GetNativeReserveGb(PackMemoryProfile.Unknown, budgetGb));
        Assert.Equal(heapGb, MemorySizingService.GetHeapGb(PackMemoryProfile.Unknown, budgetGb));
    }

    /// <summary>
    /// However small the number, the heap keeps its floor - and when the budget
    /// is under what the pack holds beside that heap, the launcher can say so
    /// rather than pretend the number was kept.
    /// </summary>
    [Fact]
    public void ABudgetBelowWhatThePackHolds_HasAFloorAndAName()
    {
        Assert.Equal(
            MemorySizingService.GetNativeReserveGb(BigModpack) + MemorySizingService.MinHeapGb,
            MemorySizingService.GetSmallestUsefulBudgetGb(BigModpack));

        foreach (var pack in new[] { Vanilla, SmallModpack, BigModpack, HeavierThanLimitless })
        {
            for (var budget = MemorySizingService.MinMemoryGb; budget <= 32; budget++)
            {
                Assert.True(
                    MemorySizingService.GetHeapGb(pack, budget) >= MemorySizingService.MinHeapGb,
                    $"{budget} GB left less than the floor");
            }
        }
    }

    /// <summary>
    /// A setting written when the number was the heap alone becomes the
    /// smallest budget that still leaves that heap - nobody's game shrinks on
    /// the launch that changed what the number means, whichever pack they play.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    public void AStoredHeap_BecomesABudgetThatStillLeavesIt(int heapGb)
    {
        foreach (var pack in new[] { PackMemoryProfile.Unknown, Vanilla, BigModpack })
        {
            var budget = MemorySizingService.GetBudgetForHeapGb(pack, heapGb);

            Assert.True(
                MemorySizingService.GetHeapGb(pack, budget) >= heapGb,
                $"{budget} GB leaves {MemorySizingService.GetHeapGb(pack, budget)} GB, less than the {heapGb} that was set");
            Assert.True(
                MemorySizingService.GetHeapGb(pack, budget - 1) < heapGb,
                $"{budget - 1} GB would have been enough too");
        }
    }

    /// <summary>
    /// And what the launcher suggests stays inside what it allows: a pack that
    /// wants more than the machine has is cut down to the machine.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void TheSuggestion_FitsInsideTheLargestOffered(int installedGb)
    {
        var allowed = MemorySizingService.GetAllowedMaxMemoryGb(Gb(installedGb));

        foreach (var pack in new[]
                 {
                     PackMemoryProfile.Unknown, OldVanilla, Vanilla, SmallModpack, BigModpack, HeavierThanLimitless
                 })
        {
            var suggested = MemorySizingService.GetRecommendedDefaultMemoryGb(pack, Gb(installedGb));

            Assert.InRange(suggested, MemorySizingService.MinMemoryGb, allowed);
        }
    }

    /// <summary>
    /// The field says what the number buys and the launch argument is the heap
    /// the budget leaves - both worked out from the pack that is selected, not
    /// from a constant left over from one pack.
    /// </summary>
    [Fact]
    public void TheLaunchAndTheField_BothSpeakOfThePacksSplit()
    {
        var launch = ReadRepositoryFile("Program", "MinecraftProcessService.cs");
        Assert.Contains("PackMemoryProfile.Measure(packDir)", launch, StringComparison.Ordinal);
        Assert.Contains(
            "MemorySizingService.GetHeapGb(packMemory, settings.MaxMemoryGb, video)",
            launch,
            StringComparison.Ordinal);

        var window = ReadRepositoryFile("Program", "MainWindow.xaml.cs");
        Assert.Contains("MemoryTextBox.ToolTip =", window, StringComparison.Ordinal);
        Assert.Contains("игра может занять всего", window, StringComparison.Ordinal);

        // The card belongs to the same sum, and to all three places that do it:
        // the number the launcher suggests, the number the field explains and
        // the number the game is started with have to agree, or the field
        // describes a launch that does not happen.
        foreach (var source in new[]
                 {
                     launch, window, ReadRepositoryFile("Program", "SettingsService.cs")
                 })
        {
            Assert.Contains("VideoMemoryProfile.Measure()", source, StringComparison.Ordinal);
        }
        // The pack is weighed again when another build is chosen and when one
        // finishes downloading; without that the field describes the pack that
        // was here before.
        Assert.Contains("private void RefreshPackMemory()", window, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] relativeParts)
    {
        var current = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(current.FullName, System.IO.Path.Combine);
            if (System.IO.File.Exists(candidate)) return System.IO.File.ReadAllText(candidate);
            current = current.Parent;
        }
        throw new System.IO.FileNotFoundException($"Repository file was not found: {System.IO.Path.Combine(relativeParts)}");
    }
}
