using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The number a player sets is the Java heap and goes to -Xmx untouched. What
/// the game takes on top of it belongs to the pack, not to the launcher - a
/// vanilla client holds about a gigabyte outside its heap, and Limitless 8,
/// measured, held almost eight above a twelve gigabyte one - so that room comes
/// out of the largest heap the field will accept. Every rule here is therefore
/// asked about a pack, and the same field serves vanilla on an old version and
/// something heavier than Limitless 8 on a new one.
/// </summary>
public sealed class MemorySizingTests
{
    private static ulong Gb(int value) => (ulong)value * 1024 * 1024 * 1024;
    private static long Mb(int value) => (long)value * 1024 * 1024;

    /// <summary>Limitless 8 as it stands: 874 jars, 1.9 GB of them, and the texture beside them.</summary>
    // Limitless 8, the one pack these rules were measured against: 882 jars in
    // its mods folder and 1128 mods once the ones nested inside those jars are
    // counted, which is what the loader loads and what the sizing charges for.
    // This number used to be the file count, and the constants were fitted to
    // it; both moved together, so every expectation below is the same
    // measurement it always was.
    private static PackMemoryProfile BigModpack => new(1128, Mb(1959), Mb(115), "1.21.1");
    private static PackMemoryProfile Vanilla => new(0, 0, 0, "1.21.1");
    private static PackMemoryProfile OldVanilla => new(0, 0, 0, "1.7.10");
    private static PackMemoryProfile SmallModpack => new(60, Mb(180), Mb(40), "1.20.1");
    private static PackMemoryProfile HeavierThanLimitless => new(1500, Mb(4000), Mb(500), "1.22");

    [Theory]
    [InlineData(8, 5)]
    [InlineData(16, 12)]
    [InlineData(32, 24)]
    [InlineData(64, 48)]
    public void TheLargestTheMachineIsAskedFor_LeavesItItsQuarter(int installedGb, int expected)
    {
        Assert.Equal(expected, MemorySizingService.GetWholeGameAllowanceGb(Gb(installedGb)));
    }

    /// <summary>
    /// Under a quarter there is a floor, and it is three gigabytes rather than
    /// four. Windows reports what the hardware has left it, so a laptop sold
    /// with eight gigabytes says seven and a half; the old floor took four of
    /// those seven and offered three, which is under what a pack of fifty mods
    /// needs to keep the promise the number makes. Machines large enough for
    /// the quarter to be the larger of the two are not affected at all.
    /// </summary>
    [Theory]
    [InlineData(7.5, 4)]   // "8 GB" as Windows counts it
    [InlineData(15.6, 12)] // "16 GB", the same rounding
    [InlineData(11.9, 8)]
    [InlineData(31.6, 24)] // the quarter rules from twelve gigabytes up
    [InlineData(63.5, 48)]
    public void ASmallMachine_KeepsThreeGigabytesBack_NotFour(double installedGb, int expected)
    {
        var installed = (ulong)(installedGb * 1024 * 1024 * 1024);

        Assert.Equal(expected, MemorySizingService.GetWholeGameAllowanceGb(installed));
    }

    /// <summary>A very small machine still gets the floor, not a negative.</summary>
    [Fact]
    public void ASmallMachine_StillGetsTheFloor()
    {
        Assert.Equal(MemorySizingService.MinHeapGb, MemorySizingService.GetWholeGameAllowanceGb(Gb(4)));
        Assert.Equal(MemorySizingService.MinHeapGb, MemorySizingService.GetWholeGameAllowanceGb(Gb(2)));
    }

    /// <summary>
    /// The pack the model was calibrated on keeps the split it was calibrated
    /// to: eight gigabytes outside the heap and twelve inside. On a 32 GB
    /// machine those eight come out of the twenty-four the machine may be asked
    /// for, so the field offers sixteen at most and suggests the twelve the
    /// pack wants.
    /// </summary>
    [Fact]
    public void ThePackTheModelWasMeasuredOn_KeepsItsSplit()
    {
        Assert.Equal(8, MemorySizingService.GetNativeReserveGb(BigModpack));
        Assert.Equal(12, MemorySizingService.GetHeapForBudgetGb(BigModpack, 20));
        Assert.Equal(16, MemorySizingService.GetAllowedHeapGb(BigModpack, Gb(32)));
        Assert.Equal(12, MemorySizingService.GetRecommendedMemoryGb(BigModpack, Gb(32)));
    }

    /// <summary>
    /// And vanilla is not charged for mods it does not carry: the same twenty
    /// gigabytes are nearly all heap, and what it is offered is a small number.
    /// </summary>
    [Fact]
    public void APackWithoutMods_IsNotChargedForThem()
    {
        Assert.Equal(1, MemorySizingService.GetNativeReserveGb(Vanilla));
        Assert.Equal(23, MemorySizingService.GetAllowedHeapGb(Vanilla, Gb(32)));
        Assert.True(
            MemorySizingService.GetAllowedHeapGb(Vanilla, Gb(32)) >
            MemorySizingService.GetAllowedHeapGb(BigModpack, Gb(32)),
            "the same machine must offer vanilla the larger heap");

        var suggested = MemorySizingService.GetRecommendedMemoryGb(Vanilla, Gb(32));
        Assert.InRange(suggested, MemorySizingService.MinHeapGb, 6);
    }

    /// <summary>
    /// A pack nobody has weighed is sized by arithmetic, not by a table of
    /// sizes. The steps this replaced answered every machine from twelve to
    /// fifteen gigabytes with the same number and then jumped four at sixteen,
    /// so two laptops one gigabyte apart were offered four gigabytes apart, and
    /// a machine just under a step was offered less of itself than a smaller
    /// one. What is asked of the shape here is only that: it never falls as the
    /// machine grows, and it never moves further than the machine did.
    /// </summary>
    [Fact]
    public void AnUnseenPack_MovesWithTheMachineRatherThanInSteps()
    {
        var offered = Enumerable.Range(4, 61)
            .Select(installedGb => (
                Installed: installedGb,
                Offered: MemorySizingService.GetRecommendedMemoryGb(
                    PackMemoryProfile.Unknown, Gb(installedGb))))
            .ToList();

        foreach (var (smaller, larger) in offered.Zip(offered.Skip(1)))
        {
            Assert.True(
                larger.Offered >= smaller.Offered,
                $"{larger.Installed} GB installed is offered {larger.Offered}, " +
                $"less than the {smaller.Offered} of a {smaller.Installed} GB machine");
            Assert.True(
                larger.Offered - smaller.Offered <= 1,
                $"{smaller.Installed} GB installed is offered {smaller.Offered} and " +
                $"{larger.Installed} GB is offered {larger.Offered}: one gigabyte of machine, " +
                $"{larger.Offered - smaller.Offered} of answer");
        }

        // And it is an answer about this machine, not about the band it fell in.
        Assert.NotEqual(
            MemorySizingService.GetRecommendedMemoryGb(PackMemoryProfile.Unknown, Gb(12)),
            MemorySizingService.GetRecommendedMemoryGb(PackMemoryProfile.Unknown, Gb(15)));
    }

    /// <summary>An older Minecraft asks for less than a new one, never more.</summary>
    [Fact]
    public void AnOlderMinecraft_AsksForNoMoreThanANewOne()
    {
        Assert.True(
            MemorySizingService.GetNativeReserveGb(OldVanilla) <= MemorySizingService.GetNativeReserveGb(Vanilla));
        Assert.True(
            MemorySizingService.GetRecommendedMemoryGb(OldVanilla, Gb(32)) <=
            MemorySizingService.GetRecommendedMemoryGb(Vanilla, Gb(32)));
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
            MemorySizingService.GetRecommendedMemoryGb(HeavierThanLimitless, Gb(64)) >
            MemorySizingService.GetRecommendedMemoryGb(BigModpack, Gb(64)));
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
        Assert.Equal(heapGb, MemorySizingService.GetAllowedHeapGb(BigModpack, Gb(32), video));
    }

    /// <summary>
    /// And the ceiling moves with it rather than the pick: what the driver
    /// keeps in system memory comes out of the largest heap the field will
    /// take, so the smaller card offers the smaller maximum - by itself,
    /// without anyone being told to type a different number. The suggestion is
    /// the heap the pack wants either way, because that has nothing to do with
    /// the card until the machine runs out of room for it.
    /// </summary>
    [Fact]
    public void TheCeiling_MakesRoomForWhatTheDriverKeeps()
    {
        var smallCard = new VideoMemoryProfile(8);
        var largeCard = new VideoMemoryProfile(16);

        Assert.Equal(12, MemorySizingService.GetAllowedHeapGb(BigModpack, Gb(32), smallCard));
        Assert.Equal(16, MemorySizingService.GetAllowedHeapGb(BigModpack, Gb(32), largeCard));

        Assert.Equal(
            MemorySizingService.GetRecommendedHeapGb(BigModpack),
            MemorySizingService.GetRecommendedMemoryGb(BigModpack, Gb(32), smallCard));
        Assert.Equal(
            MemorySizingService.GetRecommendedMemoryGb(BigModpack, Gb(32), largeCard),
            MemorySizingService.GetRecommendedMemoryGb(BigModpack, Gb(32), smallCard));
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
            MemorySizingService.GetAllowedHeapGb(PackMemoryProfile.Unknown, Gb(32)),
            MemorySizingService.GetAllowedHeapGb(PackMemoryProfile.Unknown, Gb(32), smallCard));
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
        Assert.Equal(heapGb, MemorySizingService.GetHeapForBudgetGb(PackMemoryProfile.Unknown, budgetGb));
    }

    /// <summary>
    /// However small the machine, the field still offers a heap: a pack that
    /// holds more beside its heap than the machine can lend altogether would
    /// otherwise be offered nothing at all, and a launcher that offers nothing
    /// is a launcher that cannot start.
    /// </summary>
    [Fact]
    public void AMachineTooSmallForThePack_StillOffersTheFloor()
    {
        foreach (var pack in new[] { Vanilla, SmallModpack, BigModpack, HeavierThanLimitless })
        {
            for (var installedGb = 4; installedGb <= 64; installedGb++)
            {
                Assert.True(
                    MemorySizingService.GetAllowedHeapGb(pack, Gb(installedGb)) >= MemorySizingService.MinHeapGb,
                    $"{installedGb} GB installed offered less than the floor");
            }
        }
    }

    /// <summary>
    /// A setting written while the number meant the whole of the game becomes
    /// the heap that number was already producing - so nobody has their game
    /// change size on the launch that changes what the number means, whichever
    /// pack they play. Only what they are shown changes.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    public void AStoredBudget_BecomesTheHeapItWasAlreadyLeaving(int budgetGb)
    {
        foreach (var pack in new[] { PackMemoryProfile.Unknown, Vanilla, BigModpack })
        {
            var heapGb = MemorySizingService.GetHeapForBudgetGb(pack, budgetGb);

            Assert.Equal(
                Math.Max(
                    MemorySizingService.MinHeapGb,
                    budgetGb - MemorySizingService.GetNativeReserveGb(pack, budgetGb)),
                heapGb);
            Assert.InRange(heapGb, MemorySizingService.MinHeapGb, budgetGb);
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
        foreach (var pack in new[]
                 {
                     PackMemoryProfile.Unknown, OldVanilla, Vanilla, SmallModpack, BigModpack, HeavierThanLimitless
                 })
        {
            var allowed = MemorySizingService.GetAllowedHeapGb(pack, Gb(installedGb));
            var suggested = MemorySizingService.GetRecommendedMemoryGb(pack, Gb(installedGb));

            Assert.InRange(suggested, MemorySizingService.MinHeapGb, allowed);
        }
    }

    /// <summary>
    /// The field says what the number buys and the launch argument is the heap
    /// the budget leaves - both worked out from the pack that is selected, not
    /// from a constant left over from one pack.
    /// </summary>
    /// <remarks>
    /// The launch call gained a fourth argument when the room beside the heap
    /// stopped being estimated wherever it has been measured, and this test
    /// asks for the new one by name: a launch that divided the budget by the
    /// pack model while the field divided it by the measurement would describe
    /// a game that does not start. <c>measured</c> has to be in that call for
    /// the same reason <c>video</c> did.
    /// </remarks>
    [Fact]
    public void TheLaunchAndTheField_BothSpeakOfThePacksSplit()
    {
        var launch = ReadRepositoryFile("Program", "MinecraftProcessService.cs");
        Assert.Contains("PackMemoryProfile.Measure(packDir)", launch, StringComparison.Ordinal);
        Assert.Contains("var heapGb = settings.MaxHeapGb;", launch, StringComparison.Ordinal);
        Assert.Contains("var maximumRamMb = checked(heapGb * 1024);", launch, StringComparison.Ordinal);
        // And the measurement is looked up for this pack on this machine, not
        // taken from whatever pack was played last.
        Assert.Contains(
            "_measuredMemory.Recall(settings.ClientRelativePath, video, installedGb)",
            launch,
            StringComparison.Ordinal);

        var window = ReadRepositoryFile("Program", "MainWindow.xaml.cs");
        Assert.Contains("MemoryTextBox.ToolTip =", window, StringComparison.Ordinal);
        Assert.Contains("это же число покажет игра по F3", window, StringComparison.Ordinal);

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
            // And so does the measurement, for the same three.
            Assert.Contains("measured", source, StringComparison.OrdinalIgnoreCase);
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
