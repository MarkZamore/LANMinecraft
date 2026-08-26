using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The play button holds its caption on a canvas that never changes: no
/// wrapping, no trimming, so a caption too wide is cut mid-letter and nothing
/// says it happened. Every caption the preparation can show is measured here
/// with the real font files, at the real size, against the real button width.
/// </summary>
public sealed class PlayButtonCaptionTests
{
    // The widest numbers the formatter can produce: just under the gigabyte
    // rollover, both halves at once, with a three-digit speed beside them.
    private const long JustUnderAGigabyte = (1024L * 1024 * 1024) - 1;
    private const double FastDownload = 123.45 * 1024 * 1024;

    public static TheoryData<string> EveryCaption()
    {
        var captions = new TheoryData<string>
        {
            "Играть",
            "Игра запущена",
            "Не удалось подготовить сборку",
        };
        var already = new HashSet<string>(StringComparer.Ordinal) { "Играть", "Игра запущена", "Не удалось подготовить сборку" };
        // Several stages settle on the same words; each is measured once.
        foreach (var caption in Captions()) if (already.Add(caption)) captions.Add(caption);
        return captions;
    }

    [Theory]
    [MemberData(nameof(EveryCaption))]
    public void EveryCaption_FitsInsideTheButton(string caption)
    {
        var width = Measure(caption);

        Assert.True(
            width <= PlayButtonCaption.MaxWidth,
            $"'{caption}' draws {width:0.#} px of the {PlayButtonCaption.MaxWidth} px the button has; it would be cut mid-letter");
    }

    /// <summary>
    /// The pair of sizes carries one unit, taken from the whole, and a number
    /// of a hundred or more drops its decimal: at that size the tenth is noise,
    /// and the width it costs is the width the caption does not have.
    /// </summary>
    [Fact]
    public void TheSizes_AreWrittenOnceInTheUnitOfTheWhole()
    {
        Assert.Equal("12,3 / 457 МБ", PlayButtonCaption.Pair(12_900_000, 479_000_000).Replace('.', ','));
        Assert.Equal("0 / 1,2 ГБ", PlayButtonCaption.Pair(0, 1_288_490_188).Replace('.', ','));
        Assert.Equal("123 МБ/с", PlayButtonCaption.Rate(FastDownload).Replace('.', ','));
        Assert.Equal("0 Б/с", PlayButtonCaption.Rate(0).Replace('.', ','));
    }

    private static IEnumerable<string> Captions()
    {
        var byteStages = new[]
        {
            RuntimePreparationStage.SyncingPack,
            RuntimePreparationStage.Downloading,
            RuntimePreparationStage.InstallingJava,
        };
        // Every word the preparation can put in front of a pair of sizes: the
        // Java it is fetching, and the name of whatever set of files is coming
        // down - the base game, or the loader the pack asks for.
        var subjects = new[]
        {
            $"Java {PortableJavaRuntimeService.PinnedJavaVersion}",
            "Minecraft", "NeoForge", "Fabric", "Quilt", "Forge", "Файлы"
        };
        foreach (var stage in byteStages)
        {
            foreach (var subject in subjects)
            {
                var progress = new RuntimePreparationProgress(
                    stage,
                    subject,
                    Fraction: 1,
                    DownloadedBytes: JustUnderAGigabyte,
                    TotalBytes: JustUnderAGigabyte);
                yield return PlayButtonCaption.For(progress, FastDownload);
                yield return PlayButtonCaption.For(progress with { DownloadedBytes = 0, TotalBytes = 0 }, 0);
            }
        }

        foreach (var loader in new[] { "NeoForge", "Fabric", "Quilt", "Forge", "Minecraft" })
        {
            yield return PlayButtonCaption.For(
                new RuntimePreparationProgress(RuntimePreparationStage.InstallingLoader, loader),
                0);
        }

        foreach (var stage in new[] { RuntimePreparationStage.Checking, RuntimePreparationStage.Verifying })
        {
            yield return PlayButtonCaption.For(new RuntimePreparationProgress(stage, "Проверка"), 0);
        }
        yield return PlayButtonCaption.For(new RuntimePreparationProgress(RuntimePreparationStage.Ready, "Готовится к запуску", 1), 0);
    }

    private static double Measure(string text)
    {
        var program = FindProgramDirectory();
        var fonts = new Uri(Path.Combine(program, "Fonts") + Path.DirectorySeparatorChar);
        var typeface = new Typeface(
            new FontFamily(fonts, "#Montserrat"),
            FontStyles.Normal,
            FontWeights.Light,
            FontStretches.Normal);
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            DisplayFontSize(program),
            Brushes.Black,
            pixelsPerDip: 1);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    /// <summary>The caption's size is a token; the test reads it rather than repeating it.</summary>
    private static double DisplayFontSize(string program)
    {
        var app = File.ReadAllText(Path.Combine(program, "App.xaml"));
        var match = Regex.Match(app, @"x:Key=""Font\.SizeDisplay""[^>]*>([0-9.]+)<");
        Assert.True(match.Success, "App.xaml should still define Font.SizeDisplay");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static string FindProgramDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Program");
            if (File.Exists(Path.Combine(candidate, "App.xaml"))) return candidate;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Program directory was not found");
    }
}
