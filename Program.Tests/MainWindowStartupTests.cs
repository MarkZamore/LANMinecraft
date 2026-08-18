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

    /// <summary>
    /// The window opens, connects to Steam and starts the skin service before
    /// it ever paints a state, so until then every control wears what the
    /// markup gave it. A button that acts on a state must therefore start
    /// switched off in the markup and be switched on by the first refresh -
    /// otherwise the launcher opens offering to apply a preset already applied.
    /// </summary>
    [Fact]
    public void TheStateDrivenButtons_DoNotStartEnabledInTheMarkup()
    {
        var markup = File.ReadAllText(FindRepositoryFile("Program", "MainWindow.xaml"));
        var button = markup[markup.IndexOf("x:Name=\"ControlsPresetButton\"", StringComparison.Ordinal)..];
        button = button[..button.IndexOf("/>", StringComparison.Ordinal)];

        Assert.Contains("IsEnabled=\"False\"", button, StringComparison.Ordinal);
    }

    /// <summary>
    /// The transfer bar says "В ожидании мира" even when nothing could ever
    /// arrive: the game holds the world, no world is chosen, or there is nobody
    /// to send it to. It starts quiet in the markup and is lit by the same
    /// condition that lights the button beside it.
    /// </summary>
    [Fact]
    public void TheTransferBar_StartsQuietAndFollowsItsButton()
    {
        var markup = File.ReadAllText(FindRepositoryFile("Program", "MainWindow.xaml"));
        var area = markup[markup.IndexOf("x:Name=\"TransferProgressArea\"", StringComparison.Ordinal)..];
        area = area[..area.IndexOf(">", StringComparison.Ordinal)];
        Assert.Contains("IsEnabled=\"False\"", area, StringComparison.Ordinal);

        var source = File.ReadAllText(FindRepositoryFile("Program", "MainWindow.xaml.cs"));
        Assert.Contains("TransferButton.IsEnabled = canTransfer;", source, StringComparison.Ordinal);
        Assert.Contains("TransferProgressArea.IsEnabled = _transferActive || canTransfer;", source, StringComparison.Ordinal);
    }

    /// <summary>And the first refresh has to happen while the window is loading.</summary>
    [Fact]
    public void Startup_PaintsTheRealStateItself()
    {
        var source = File.ReadAllText(FindRepositoryFile("Program", "MainWindow.xaml.cs"));
        var loaded = source[source.IndexOf("private async void Window_Loaded(", StringComparison.Ordinal)..];
        var end = loaded.IndexOf("private async void Window_Closing(", StringComparison.Ordinal);
        if (end > 0) loaded = loaded[..end];
        var status = loaded.IndexOf("RefreshControlsPresetStatus();", StringComparison.Ordinal);
        var refresh = loaded.IndexOf("RefreshUi();", status, StringComparison.Ordinal);

        Assert.True(status > 0, "startup should still work out whether the preset is applied");
        Assert.True(refresh > status, "and it should put that on the buttons, not wait for the timer");
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
