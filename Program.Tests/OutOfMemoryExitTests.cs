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
