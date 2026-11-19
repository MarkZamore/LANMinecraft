using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;

namespace Minecraft.Tests;

/// <summary>
/// The design system lives in App.xaml as tokens and in DESIGN.md as prose.
/// These keep the two the same shape, keep the bundled typefaces available,
/// and keep raw colours and sizes out of the markup: a screen is
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
        var documented = Regex.Matches(File.ReadAllText(FindRepositoryFile("Program", "Docs", "DESIGN.md")), @"`((?:Color|Brush|Font|Space|Gap|Pad|Size|Border|Radius|Motion)\.[A-Za-z0-9]+)`")
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
    /// Both launcher weights resolve to the packaged files even when a window
    /// loads its tokens through loose XAML. No font installation is involved.
    /// </summary>
    [Fact]
    public void RobotoTypefaces_ResolveFromApplicationResourcesWithExactWeights()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            // Load the real tokens without starting the launcher or preparing
            // unrelated resources such as the background and the catalog.
            var assembly = typeof(App).Assembly;
            var document = XDocument.Load(FindRepositoryFile("Program", "App.xaml"));
            var keys = new[] { "Font.Family", "Font.WeightBody", "Font.WeightEmphasis" };
            var dictionaryMarkup = new XElement(Presentation + "ResourceDictionary",
                new XAttribute(XNamespace.Xmlns + "x", X),
                document.Descendants().Where(node => keys.Contains((string?)node.Attribute(X + "Key"))));
            var resources = Assert.IsType<ResourceDictionary>(XamlReader.Parse(dictionaryMarkup.ToString()));
            var family = Assert.IsType<FontFamily>(resources["Font.Family"]);
            foreach (var (token, weight, hash) in new[]
            {
                ("Font.WeightBody", 400, "dece7c71bbe61710787f0839c722c65e87355dd2eb4a823e6b5488121ff81e4b"),
                ("Font.WeightEmphasis", 600, "f478ba88b0292e685e487901dfbb1401457d32d6ebee4030cdfd5698235bffa8")
            })
            {
                var configuredWeight = Assert.IsType<FontWeight>(resources[token]);
                Assert.Equal(weight, configuredWeight.ToOpenTypeWeight());
                var typeface = new Typeface(family, FontStyles.Normal, configuredWeight, FontStretches.Normal);
                Assert.True(typeface.TryGetGlyphTypeface(out var glyphs), $"Packaged Roboto unavailable: {weight}");
                Assert.Equal(weight, glyphs.Weight.ToOpenTypeWeight());
                Assert.Equal(StyleSimulations.None, glyphs.StyleSimulations);
                Assert.Equal("pack", glyphs.FontUri.Scheme);
                Assert.All("Яё", character => Assert.True(glyphs.CharacterToGlyphMap.ContainsKey(character)));
                using var font = glyphs.GetFontStream();
                Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(font)));
            }

            var license = Application.GetResourceStream(new Uri(
                $"pack://application:,,,/{assembly.GetName().Name};component/Assets/Fonts/OFL.txt", UriKind.Absolute));
            Assert.NotNull(license);
            using var reader = new StreamReader(license.Stream);
            var text = reader.ReadToEnd();
            Assert.Contains("Copyright 2011 The Roboto Project Authors", text, StringComparison.Ordinal);
            Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", text, StringComparison.Ordinal);
            return true;
        });
    }

    /// <summary>
    /// The markup names tokens. A hex colour or a SystemColors key in a window
    /// or a control is a decision made outside the system, and on a dark
    /// canvas a system grey is invisible.
    /// </summary>
    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("CenteredDropDown.xaml")]
    [InlineData("PackLibraryView.xaml")]
    [InlineData("WorldTransferConfirmationDialog.xaml")]
    [InlineData("BuildRemovalConfirmationDialog.xaml")]
    [InlineData("ControlsPresetConfirmationDialog.xaml")]
    [InlineData("WorldRemovalConfirmationDialog.xaml")]
    [InlineData("NoticeDialog.xaml")]
    [InlineData("PeerNoticeWindow.xaml")]
    public void TheMarkup_NamesTokensNotColours(string file)
    {
        var xaml = File.ReadAllText(FindRepositoryFile("Program", file));

        Assert.DoesNotMatch(@"#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\b", xaml);
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
