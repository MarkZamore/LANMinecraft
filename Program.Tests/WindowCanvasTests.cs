using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace Minecraft.Tests;

/// <summary>
/// The window is a fixed canvas inside a Viewbox: 820 by a height written into
/// the markup. That height has to be exactly what the left column needs -
/// shorter and the footer is cut off the bottom of every window, taller and
/// there is a band of nothing above it. The number was set by hand once from a
/// stand-in ComboBox and was 25px short for three releases, so it is measured
/// here instead, with the launcher's own controls.
/// </summary>
public sealed class WindowCanvasTests
{
    [Fact]
    public void TheCanvas_IsExactlyAsTallAsTheLeftColumnNeeds()
    {
        var (declared, needed) = OnAStaThread(() =>
        {
            var window = LoadWindow();
            var canvas = (Grid)window.FindName("RootGrid")!;
            // The content stays in the window: implicit styles are found by
            // walking up to the window's resources, and a detached tree has no
            // window to walk up to.
            var content = (FrameworkElement)window.Content;

            // The right column is a scrolling list: it would answer with the
            // height of the whole history, so it is asked to stand aside.
            var right = canvas.Children.Cast<UIElement>().Where(child => Grid.GetColumn(child) == 2).ToList();
            foreach (var child in right) child.Visibility = Visibility.Collapsed;

            var height = canvas.Height;
            canvas.Height = double.NaN;
            content.Measure(new Size(canvas.Width, double.PositiveInfinity));
            return (height, canvas.DesiredSize.Height - canvas.Margin.Top - canvas.Margin.Bottom);
        });

        Assert.True(
            declared >= needed - 0.5,
            $"the canvas is {declared} tall and the left column needs {needed:0.##}; the footer would be cut off");
        Assert.True(
            declared <= needed + 6,
            $"the canvas is {declared} tall and the left column needs only {needed:0.##}; that is empty space above the footer");
    }

    /// <summary>
    /// The window keeps the canvas's shape, so its own size in the markup has to
    /// name that same shape - the placement service holds every later size to it.
    /// </summary>
    [Fact]
    public void TheStartingWindowSize_MatchesTheCanvasShape()
    {
        var (canvasAspect, windowAspect) = OnAStaThread(() =>
        {
            var window = LoadWindow();
            var canvas = (Grid)window.FindName("RootGrid")!;
            var margin = canvas.Margin;
            var canvasShape = (canvas.Width + margin.Left + margin.Right) / (canvas.Height + margin.Top + margin.Bottom);
            // A window's Height covers its title bar and borders; the client area
            // below them is what the canvas fills.
            const double chromeWidth = 16;
            const double chromeHeight = 39;
            return (canvasShape, (window.Width - chromeWidth) / (window.Height - chromeHeight));
        });

        Assert.True(
            Math.Abs(canvasAspect - windowAspect) < 0.02,
            $"the canvas is {canvasAspect:0.###} wide for its height, the window {windowAspect:0.###}");
    }

    private static Window LoadWindow()
    {
        var program = Path.GetDirectoryName(FindRepositoryFile("Program", "MainWindow.xaml"))!;
        var app = File.ReadAllText(Path.Combine(program, "App.xaml"));
        var resources = Regex.Match(app, @"<Application\.Resources>(.*)</Application\.Resources>", RegexOptions.Singleline)
            .Groups[1].Value;
        // The tokens use namespaces App.xaml declares on its root; the window
        // has to declare the same ones for the spliced resources to parse.
        var namespaces = string.Concat(Regex.Matches(app, @"\sxmlns:(?!x=)[A-Za-z0-9]+=""[^""]*""")
            .Select(match => match.Value));

        var xaml = File.ReadAllText(Path.Combine(program, "MainWindow.xaml"));
        xaml = Regex.Replace(xaml, @"x:Class=""[^""]*""", namespaces);
        // Handlers live in the code-behind this loose copy does not have.
        xaml = Regex.Replace(
            xaml,
            @"\s(Click|Loaded|Closing|SelectionChanged|TextChanged|PreviewTextInput|LostFocus|KeyDown|DataObject\.Pasting)=""[^""]*""",
            "");
        xaml = xaml.Replace(@"Icon=""Minecraft.ico""", "");
        // A loose XAML resolves clr-namespace against the assembly parsing it.
        xaml = xaml.Replace(@"clr-namespace:Minecraft""", @"clr-namespace:Minecraft;assembly=LANMinecraft""");
        // The typeface's pack URI is served only inside an Application; the
        // same files are read from the repository, so the metrics are the real ones.
        var fonts = new Uri(Path.Combine(program, "Fonts") + Path.DirectorySeparatorChar).AbsoluteUri;
        resources = resources.Replace(
            "pack://application:,,,/LANMinecraft;component/Fonts/#Montserrat", fonts + "#Montserrat");
        xaml = xaml.Replace("<Window.Resources>", "<Window.Resources>" + resources);
        return (Window)XamlReader.Parse(xaml);
    }

    /// <summary>WPF objects belong to a single-threaded apartment; xUnit's is not one.</summary>
    private static T OnAStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(1)), "the layout pass did not finish in a minute");
        if (failure is not null) throw new InvalidOperationException("The window could not be measured.", failure);
        return result;
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
