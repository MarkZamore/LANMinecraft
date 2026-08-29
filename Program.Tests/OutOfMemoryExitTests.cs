using System.IO;
using System.Linq;

namespace Minecraft.Tests;

/// <summary>
/// Running out of memory while a world's data packs load makes the game offer
/// to open that world with the vanilla data pack alone, and a modded world
/// saved without its data packs loses everything they define. The offer comes
/// up seconds before the "out of memory" screen, so the dangerous button is the
/// one a player sees first. The game must not get that far.
/// </summary>
public sealed class OutOfMemoryExitTests
{
    [Fact]
    public void TheGame_DiesAtTheFirstOutOfMemory()
    {
        var source = Read("Program", "MinecraftProcessService.cs");

        Assert.Contains("-XX:+ExitOnOutOfMemoryError", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the launcher takes over the explaining, since nothing is left on
    /// screen to do it: the ending is named, with the size that ran out.
    /// </summary>
    [Fact]
    public void TheLauncher_SaysSo_WithTheSizeThatRanOut()
    {
        var service = Read("Program", "MinecraftProcessService.cs");
        Assert.Contains("public event Action<int>? ClientRanOutOfMemory;", service, StringComparison.Ordinal);
        Assert.Contains("ClientRanOutOfMemory?.Invoke(heapGb);", service, StringComparison.Ordinal);

        var window = Read("Program", "MainWindow.xaml.cs");
        Assert.Contains("_minecraft.ClientRanOutOfMemory += OnMinecraftRanOutOfMemory;", window, StringComparison.Ordinal);
        var handler = Between(window, "private void OnMinecraftRanOutOfMemory(", Environment.NewLine + "    }");
        Assert.Contains("SetBugReportStatus(", handler, StringComparison.Ordinal);
        Assert.Contains("{maxMemoryGb} ГБ", handler, StringComparison.Ordinal);
    }

    /// <summary>
    /// And it has to look where the JVM actually leaves it. The flag above is
    /// what makes this hard: the JVM dies on the spot, so its one parting line
    /// goes straight to stdout and log4j never writes it down. latest.log ends
    /// mid-sentence, Windows blames whatever native library a thread was inside
    /// - for a client, usually OpenAL - and a launcher reading only the tail
    /// reports a crash with no cause, twice in a row, while writing the line
    /// itself into its own log one statement earlier.
    /// </summary>
    [Fact]
    public void TheOneLineTheJvmLeaves_IsOnTheConsole()
    {
        Assert.True(MinecraftProcessService.NamesOutOfMemory(
            "[08:06:26] [Render thread/INFO]: Loading plugin from geneticsresequenced" +
            Environment.NewLine +
            "Terminating due to java.lang.OutOfMemoryError: Java heap space",
            logTail: "[08:06:26.901] [Thread-54/INFO] [EMI/]: Loading plugin from geneticsresequenced"));
    }

    /// <summary>One that did reach the game's own log is read as it always was.</summary>
    [Fact]
    public void AnEndingThatReachedTheLog_IsStillRead()
    {
        Assert.True(MinecraftProcessService.NamesOutOfMemory(
            consoleOutput: "",
            logTail: "[Render thread/ERROR]: java.lang.OutOfMemoryError: Java heap space"));
    }

    /// <summary>And an ordinary ending is not turned into one.</summary>
    [Fact]
    public void AnOrdinaryEnding_IsNotMistakenForIt()
    {
        Assert.False(MinecraftProcessService.NamesOutOfMemory(
            consoleOutput: "Terminating due to a signal",
            logTail: "[Render thread/INFO]: Stopping!"));
    }

    /// <summary>
    /// Better still, before it happens. A number somebody typed is never moved
    /// for them, and it follows them from the pack they typed it for onto every
    /// pack after it - so the launch that knows the number cannot work is the
    /// last moment anyone can be told while it still matters.
    /// </summary>
    [Fact]
    public void TheLauncher_SaysSoBeforeTheGameStarts()
    {
        var service = Read("Program", "MinecraftProcessService.cs");
        Assert.Contains("public event Action<int, int>? ClientMemoryIsTooSmall;", service, StringComparison.Ordinal);
        Assert.Contains("ClientMemoryIsTooSmall?.Invoke(", service, StringComparison.Ordinal);
        // The number offered carries the measurement too - it is what the
        // launch about to be refused would have used, so advising a budget
        // without it would advise one the launcher then divides differently.
        Assert.Contains(
            "GetRecommendedMemoryGb(packMemory, video, measured)", service, StringComparison.Ordinal);

        var window = Read("Program", "MainWindow.xaml.cs");
        Assert.Contains("_minecraft.ClientMemoryIsTooSmall += OnMinecraftMemoryIsTooSmall;", window, StringComparison.Ordinal);
        var handler = Between(window, "private void OnMinecraftMemoryIsTooSmall(", Environment.NewLine + "    }");
        Assert.Contains("SetBugReportStatus(", handler, StringComparison.Ordinal);
        Assert.Contains("{chosenGb} ГБ", handler, StringComparison.Ordinal);
        Assert.Contains("{neededGb} ГБ", handler, StringComparison.Ordinal);

        // And it does not ask for a number the machine will not take: a laptop
        // keeps a quarter of itself back, so the box refuses what a three
        // hundred mod pack needs long before the pack is satisfied.
        Assert.Contains("neededGb > GetAllowedHeapGb()", handler, StringComparison.Ordinal);
    }

    /// <summary>
    /// The machine that cannot be advised is a real one, not a corner: eight
    /// gigabytes installed leaves four or five for the whole game, and a three
    /// hundred mod pack holds more than that outside its heap alone. Whichever
    /// of the two the machine reports, the largest heap it can offer is the
    /// floor.
    /// </summary>
    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void AnEightGigabyteLaptop_CannotBeAdvisedIntoAKitchenSinkPack(int reportedGb)
    {
        // All The Mods 10, measured: 476 jars in its mods folder, 621 mods once
        // the ones nested inside them are counted, 1323 MiB of jars. A real
        // kitchen-sink pack rather than a shape invented for the test.
        var pack = new PackMemoryProfile(621, 1_386_772_253, 0, "1.21.1");
        var installed = (ulong)reportedGb * 1024 * 1024 * 1024;
        var offerable = MemorySizingService.GetAllowedHeapGb(installed);
        var needed = MemorySizingService.GetRecommendedHeapGb(pack);

        Assert.True(needed > offerable, $"{needed} GB needed should be past the {offerable} GB this machine offers");
        // What is left after Windows on a machine this size, and it is not
        // enough: the pack asks for eight.
        Assert.Equal(reportedGb - 3, offerable);
    }

    /// <summary>
    /// And the number it offers has to be a heap worth having rather than the
    /// floor. The floor is two gigabytes - the size this was all found on, which
    /// the game had already died in twice - and the pack it was found on asks
    /// for five.
    /// </summary>
    [Fact]
    public void TheNumberOffered_LeavesAHeapWorthHaving()
    {
        // 312 as recorded, which was that pack's file count; see the note in
        // VideoMemoryProfileTests. The two numbers this test compares move
        // together, so what it asserts is unchanged.
        var pack = new PackMemoryProfile(312, 611_350_362, 0, "1.21.1");
        var noCard = VideoMemoryProfile.Unknown;
        var offered = MemorySizingService.GetRecommendedMemoryGb(pack, 32UL * 1024 * 1024 * 1024, noCard);

        Assert.True(
            offered > MemorySizingService.MinHeapGb,
            $"{offered} GB should be more than the floor the game has already died in");
        Assert.Equal(5, offered);
    }

    /// <summary>
    /// And every session leaves behind the one measurement the whole memory
    /// model has been short of. It rests on a single pack - Limitless 8, 874
    /// jars, about eight gigabytes outside a twelve gigabyte heap - and every
    /// other pack is an extrapolation from it, including the arithmetic that
    /// decides whether a machine is offered a pack at all. The subtraction is
    /// honest because -Xms is set equal to -Xmx, so the heap is committed and
    /// what stands above it is the room beside it.
    /// </summary>
    [Fact]
    public void EverySessionLeavesWhatItActuallyHeld()
    {
        // The line the game is started with names both ends of the heap, since
        // they are no longer the same number on a machine short of memory.
        Assert.Contains(
            "-Xms{InitialHeapMbFor(maximumRamMb)}M -Xmx{maximumRamMb}M",
            Read("Program", "MinecraftProcessService.cs"),
            StringComparison.Ordinal);

        var held = MinecraftProcessService.DescribeMemoryHeld(
            residentBytes: 6L * 1024 * 1024 * 1024,
            committedBytes: 6L * 1024 * 1024 * 1024,
            heapGb: 4);
        Assert.Contains("6144 MB", held, StringComparison.Ordinal);
        Assert.Contains("4096 MB", held, StringComparison.Ordinal);
        Assert.Contains("2048 MB", held, StringComparison.Ordinal);
    }

    /// <summary>
    /// And it is the commit that answers, not the resident set. A machine short
    /// of memory pages the rest out, so the working set understates what the
    /// pack wanted - and understates it worst on the small machines this
    /// measurement exists for. Here the game asked for six gigabytes and only
    /// three were resident: the room beside the heap is two gigabytes, not
    /// none.
    /// </summary>
    [Fact]
    public void APagingMachine_DoesNotMakeThePackLookSmaller()
    {
        var paging = MinecraftProcessService.DescribeMemoryHeld(
            residentBytes: 3L * 1024 * 1024 * 1024,
            committedBytes: 6L * 1024 * 1024 * 1024,
            heapGb: 4);

        Assert.Contains("asked for 6144 MB", paging, StringComparison.Ordinal);
        Assert.Contains("2048 MB beside it", paging, StringComparison.Ordinal);
        // The gap between the two is itself the reading, so both are kept.
        Assert.Contains("3072 MB of it resident", paging, StringComparison.Ordinal);
    }

    /// <summary>
    /// A session nobody could measure, and one whose heap is not known - an
    /// already-running game the launcher adopted - say less rather than
    /// something made up.
    /// </summary>
    [Fact]
    public void WhatWasNotMeasured_IsNotInvented()
    {
        Assert.Equal("", MinecraftProcessService.DescribeMemoryHeld(0, 0, heapGb: 4));
        Assert.Equal("", MinecraftProcessService.DescribeMemoryHeld(-1, -1, heapGb: 4));

        var adopted = MinecraftProcessService.DescribeMemoryHeld(
            3L * 1024 * 1024 * 1024, 3L * 1024 * 1024 * 1024, heapGb: 0);
        Assert.Contains("3072 MB", adopted, StringComparison.Ordinal);
        Assert.DoesNotContain("beside it", adopted, StringComparison.Ordinal);
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' should still be there");
        var rest = source[from..];
        var to = rest.IndexOf(end, start.Length, StringComparison.Ordinal);
        return to < 0 ? rest : rest[..to];
    }

    private static string Read(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            current = current.Parent;
        }
        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(relativeParts)}");
    }
}
