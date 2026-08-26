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
        Assert.Contains("ClientRanOutOfMemory?.Invoke(maxMemoryGb);", service, StringComparison.Ordinal);

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
        Assert.Contains(
            "ClientMemoryIsTooSmall?.Invoke(settings.MaxMemoryGb, smallestUsefulBudgetGb);",
            service,
            StringComparison.Ordinal);

        var window = Read("Program", "MainWindow.xaml.cs");
        Assert.Contains("_minecraft.ClientMemoryIsTooSmall += OnMinecraftMemoryIsTooSmall;", window, StringComparison.Ordinal);
        var handler = Between(window, "private void OnMinecraftMemoryIsTooSmall(", Environment.NewLine + "    }");
        Assert.Contains("SetBugReportStatus(", handler, StringComparison.Ordinal);
        Assert.Contains("{chosenGb} ГБ", handler, StringComparison.Ordinal);
        Assert.Contains("{neededGb} ГБ", handler, StringComparison.Ordinal);
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
