using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace Minecraft.Tests;

/// <summary>
/// The design system lives in App.xaml as tokens and in DESIGN.md as prose.
/// These keep the two the same shape, keep the system typeface available,
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
        var documented = Regex.Matches(File.ReadAllText(FindRepositoryFile("DESIGN.md")), @"`((?:Color|Brush|Font|Space|Gap|Pad|Size|Border|Radius|Motion)\.[A-Za-z0-9]+)`")
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
    /// The Fluent system family resolves without extracted font files or an
    /// Application pack-URI server, including its regular and emphasis faces.
    /// </summary>
    [Fact]
    public void FluentSystemTypeface_ResolvesWithoutBundledFonts()
    {
        var document = XDocument.Load(FindRepositoryFile("Program", "App.xaml"));
        var familyName = document.Descendants(Presentation + "FontFamily")
            .Single(element => (string?)element.Attribute(X + "Key") == "Font.Family").Value;
        Assert.Equal("Segoe UI Variable Text, Segoe UI", familyName);
        var family = new FontFamily(familyName);
        foreach (var weight in new[] { FontWeights.Normal, FontWeights.SemiBold })
        {
            var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
            Assert.True(typeface.TryGetGlyphTypeface(out var glyphs), $"System font unavailable: {weight}");
            Assert.True(glyphs.CharacterToGlyphMap.ContainsKey('Я'), "The launcher requires Cyrillic glyphs.");
        }
        var project = File.ReadAllText(FindRepositoryFile("Program", "Minecraft.csproj"));
        Assert.DoesNotContain("Montserrat", project, StringComparison.OrdinalIgnoreCase);
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
