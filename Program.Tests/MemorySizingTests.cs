using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The number a player sets is the Java heap, and the game takes a great deal
/// more beside it: the class data of nine hundred mods, the compiled code, and
/// the buffers Sodium hands the graphics driver, which no Java setting bounds.
/// Measured on this pack that was almost eight gigabytes above a twelve
/// gigabyte heap. So the field must not offer the whole machine.
/// </summary>
public sealed class MemorySizingTests
{
    private static ulong Gb(int value) => (ulong)value * 1024 * 1024 * 1024;

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
    /// <summary>
    /// And the budget splits: the heap takes what is left after the room kept
    /// for everything the game holds outside it.
    /// </summary>
    [Theory]
    [InlineData(24, 8, 16)]
    [InlineData(20, 6, 14)]
    [InlineData(12, 4, 8)]
    [InlineData(6, 2, 4)]
    public void TheBudget_SplitsIntoHeapAndTheRoomBesideIt(int budgetGb, int reserveGb, int heapGb)
    {
        Assert.Equal(reserveGb, MemorySizingService.GetNativeReserveGb(budgetGb));
        Assert.Equal(heapGb, MemorySizingService.GetHeapGb(budgetGb));
    }

    /// <summary>
    /// A setting written when the number was the heap alone becomes the
    /// smallest budget that still leaves that heap - nobody's game shrinks on
    /// the launch that changed what the number means.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    public void AStoredHeap_BecomesABudgetThatStillLeavesIt(int heapGb)
    {
        var budget = MemorySizingService.GetBudgetForHeapGb(heapGb);

        Assert.True(
            MemorySizingService.GetHeapGb(budget) >= heapGb,
            $"{budget} GB leaves {MemorySizingService.GetHeapGb(budget)} GB, less than the {heapGb} that was set");
        Assert.True(
            MemorySizingService.GetHeapGb(budget - 1) < heapGb,
            $"{budget - 1} GB would have been enough too");
    }

    /// <summary>
    /// The field says what the number buys: the launch argument is the heap
    /// the budget leaves, not the budget itself, and the field explains the
    /// division rather than leaving a player to guess at it.
    /// </summary>
    [Fact]
    public void TheLaunchAndTheField_BothSpeakOfTheSplit()
    {
        var launch = ReadRepositoryFile("Program", "MinecraftProcessService.cs");
        Assert.Contains("MemorySizingService.GetHeapGb(settings.MaxMemoryGb)", launch, StringComparison.Ordinal);

        var window = ReadRepositoryFile("Program", "MainWindow.xaml.cs");
        Assert.Contains("MemoryTextBox.ToolTip =", window, StringComparison.Ordinal);
        Assert.Contains("игра может занять всего", window, StringComparison.Ordinal);
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

    [Fact]
    public void ASmallMachine_StillGetsTheFloor()
    {
        Assert.Equal(MemorySizingService.MinMemoryGb, MemorySizingService.GetAllowedMaxMemoryGb(Gb(4)));
        Assert.Equal(MemorySizingService.MinMemoryGb, MemorySizingService.GetAllowedMaxMemoryGb(Gb(2)));
    }

    /// <summary>
    /// And what the launcher suggests stays inside what it allows - the pack
    /// needs about ten gigabytes of heap to open a world, so the suggestion is
    /// generous, but never more than the machine can hold.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void TheSuggestion_FitsInsideTheLargestOffered(int installedGb)
    {
        var suggested = MemorySizingService.GetRecommendedDefaultMemoryGb(Gb(installedGb));
        var allowed = MemorySizingService.GetAllowedMaxMemoryGb(Gb(installedGb));

        Assert.InRange(suggested, MemorySizingService.MinMemoryGb, allowed);
    }
}
