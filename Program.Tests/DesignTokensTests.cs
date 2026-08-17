using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace Minecraft.Tests;

/// <summary>
/// The design system lives in App.xaml as tokens and in DESIGN.md as prose.
/// These keep the two the same shape, keep the typeface reachable from the
/// assembly, and keep raw colours and sizes out of the markup: a screen is
/// styled by naming a token, never by writing a number.
/// </summary>
public sealed class DesignTokensTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Every token DESIGN.md names is defined, and nothing defined is undocumented.</summary>
    [Fact]
    public void AppXaml_DefinesEveryTokenDesignMdNames_AndNoOthers()
    {
        var defined = TokenKeys();
        var documented = Regex.Matches(File.ReadAllText(FindRepositoryFile("DESIGN.md")), @"`((?:Color|Brush|Font|Space|Gap|Pad|Size|Border|Radius)\.[A-Za-z0-9]+)`")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(documented);
        var missing = documented.Except(defined).Order().ToArray();
        var undocumented = defined.Except(documented).Order().ToArray();
        Assert.True(missing.Length == 0, "DESIGN.md names tokens App.xaml lacks: " + string.Join(", ", missing));
        Assert.True(undocumented.Length == 0, "App.xaml defines tokens DESIGN.md does not mention: " + string.Join(", ", undocumented));
    }

    /// <summary>Every brush is built from a named colour, so a colour lives in exactly one place.</summary>
    [Fact]
    public void EveryBrush_IsMadeFromANamedColour()
    {
        var document = XDocument.Load(FindRepositoryFile("Program", "App.xaml"));
        var brushes = document.Descendants(Presentation + "SolidColorBrush")
            .Where(brush => ((string?)brush.Attribute(X + "Key"))?.StartsWith("Brush.", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.NotEmpty(brushes);
        Assert.All(brushes, brush =>
            Assert.Matches(@"^\{StaticResource Color\.[A-Za-z0-9]+\}$", (string?)brush.Attribute("Color")));
    }

    /// <summary>The spacing scale is 4/8/12/16/24 and nothing else.</summary>
    [Fact]
    public void TheSpacingScale_HasFiveSteps()
    {
        var document = XDocument.Load(FindRepositoryFile("Program", "App.xaml"));
        var steps = document.Descendants()
            .Where(element => ((string?)element.Attribute(X + "Key"))?.StartsWith("Space.", StringComparison.Ordinal) == true)
            .Select(element => double.Parse(element.Value, System.Globalization.CultureInfo.InvariantCulture))
            .Order()
            .ToArray();

        Assert.Equal([4, 8, 12, 16, 24], steps);
    }

    /// <summary>
    /// The typeface is embedded as a WPF resource under the path the token
    /// names, and the family holds both faces the system uses. The pack URI
    /// itself is only served once an Application exists, which a test host
    /// does not have, so the two halves are checked apart: the resource
    /// stream in the assembly, and the family read from the same files.
    /// </summary>
    [Fact]
    public void Montserrat_IsEmbeddedInBothWeights()
    {
        var assembly = typeof(ChangelogService).Assembly;
        using var stream = assembly.GetManifestResourceStream("LANMinecraft.g.resources");
        Assert.NotNull(stream);
        using var reader = new System.Resources.ResourceReader(stream);
        var embedded = reader.Cast<System.Collections.DictionaryEntry>().Select(entry => (string)entry.Key).ToArray();
        Assert.Contains("fonts/montserrat-light.ttf", embedded);
        Assert.Contains("fonts/montserrat-medium.ttf", embedded);

        var fontsDirectory = Path.GetDirectoryName(FindRepositoryFile("Program", "Fonts", "Montserrat-Light.ttf"))!;
        var families = Fonts.GetFontFamilies(new Uri(fontsDirectory + Path.DirectorySeparatorChar));
        var montserrat = Assert.Single(families, family => family.Source.EndsWith("#Montserrat", StringComparison.Ordinal));
        var weights = montserrat.GetTypefaces().Select(typeface => typeface.Weight).ToArray();
        Assert.Contains(FontWeights.Light, weights);
        Assert.Contains(FontWeights.Medium, weights);

        // And the token names that family by the assembly, the way the launcher loads it.
        var appXaml = File.ReadAllText(FindRepositoryFile("Program", "App.xaml"));
        Assert.Contains("pack://application:,,,/LANMinecraft;component/Fonts/#Montserrat", appXaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The markup names tokens. A hex colour or a SystemColors key in a window
    /// or a control is a decision made outside the system, and on a dark
    /// canvas a system grey is invisible.
    /// </summary>
    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("CenteredDropDown.xaml")]
    [InlineData("WorldTransferConfirmationDialog.xaml")]
    public void TheMarkup_NamesTokensNotColours(string file)
    {
        var xaml = File.ReadAllText(FindRepositoryFile("Program", file));

        Assert.DoesNotMatch(@"#[0-9A-Fa-f]{6}\b", xaml);
        Assert.DoesNotContain("SystemColors", xaml, StringComparison.Ordinal);
        // Sizes and gaps are tokens too; a bare number on one of these is a
        // decision made outside the system. Window geometry (Width, Height,
        // MinWidth, MinHeight on the Window itself) and grid stars are
        // structure, not style, and stay literal.
        var literal = Regex.Matches(xaml, @"\b(Margin|Padding|FontSize|CornerRadius|BorderThickness)=""[0-9]")
            .Select(match => match.Value)
            .ToArray();
        Assert.True(literal.Length == 0, $"{file} sets a literal where a token belongs: {string.Join(", ", literal)}");
    }

    private static HashSet<string> TokenKeys()
    {
        var document = XDocument.Load(FindRepositoryFile("Program", "App.xaml"));
        return document.Descendants()
            .Select(element => (string?)element.Attribute(X + "Key"))
            .Where(key => key is not null && key.Contains('.'))
            .Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);
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
