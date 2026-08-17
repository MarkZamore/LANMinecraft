using System.Reflection;
using System.Windows.Controls;

namespace Minecraft.Tests;

/// <summary>
/// The window's constructor runs before anything is set up: no paths, no
/// settings, no logger. A single Require* call in there stops the launcher
/// from opening at all, which is exactly what shipped in 138 - so nothing in
/// the constructor may ask for something the constructor does not create.
/// </summary>
public sealed class MainWindowStartupTests
{
    [Fact]
    public void TheConstructor_AsksForNothingItDoesNotHaveYet()
    {
        var source = File.ReadAllText(FindRepositoryFile("Program", "MainWindow.xaml.cs"));
        var constructor = source[source.IndexOf("public MainWindow()", StringComparison.Ordinal)..];
        constructor = constructor[..constructor.IndexOf("\n    }", StringComparison.Ordinal)];

        var demanded = System.Text.RegularExpressions.Regex.Matches(constructor, @"Require[A-Za-z]+\(\)")
            .Select(match => match.Value)
            .Distinct()
            .ToArray();

        Assert.True(
            demanded.Length == 0,
            "the constructor calls " + string.Join(", ", demanded) +
            "; those throw until Window_Loaded has built them, and the window never opens");
    }

    /// <summary>The marquee is built where its logger already exists.</summary>
    [Fact]
    public void TheNameMarquee_IsBuiltAfterTheLogger()
    {
        var source = File.ReadAllText(FindRepositoryFile("Program", "MainWindow.xaml.cs"));
        var logger = source.IndexOf("_logger = new Logger(", StringComparison.Ordinal);
        var marquee = source.IndexOf("_playerNameMarquee = new NameMarquee(", StringComparison.Ordinal);

        Assert.True(logger > 0 && marquee > 0, "both lines should still exist");
        Assert.True(marquee > logger, "the marquee takes the logger, so it cannot be built before it");
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(relativeParts)}");
    }
}
