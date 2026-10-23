using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>Preparation captions wrap inside the section-width button without clipping its text or progress.</summary>
public sealed class PlayButtonCaptionTests
{
    // The widest numbers the formatter can produce: just under the gigabyte
    // rollover, both halves at once, with a three-digit speed beside them.
    private const long JustUnderAGigabyte = (1024L * 1024 * 1024) - 1;
    private const double FastDownload = 123.45 * 1024 * 1024;

    [Fact]
    public void ThePlaySurface_StaysReferenceBlueWhilePreparingAndMutesOnlyWhenDisabled()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var window = WindowCanvasTests.LoadWindow();
            var button = Assert.IsType<Button>(window.FindName("PlayButton"));
            var text = Assert.IsType<TextBlock>(window.FindName("PlayButtonText"));
            var progress = Assert.IsType<ProgressBar>(window.FindName("PlayProgressBar"));
            var size = new Size(804, 521);
            // A different Windows accent must not recolour the explicitly blue action.
            window.Resources["Brush.Accent"] = new SolidColorBrush(Colors.Magenta);
            button.IsEnabled = true;
            WindowCanvasTests.LayoutWindow(window, size);
            var surface = Assert.IsType<Border>(button.Template.FindName("Surface", button));
            var blue = Color.FromRgb(0x60, 0xCD, 0xFF);
            Assert.Equal(blue, Assert.IsType<SolidColorBrush>(surface.Background).Color);
            Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(text.Foreground).Color);

            button.IsHitTestVisible = false;
            progress.Visibility = Visibility.Visible;
            progress.Value = 50;
            text.Text = "Проверка сборки";
            WindowCanvasTests.LayoutWindow(window, size);
            Assert.True(button.IsEnabled);
            Assert.Equal(blue, Assert.IsType<SolidColorBrush>(surface.Background).Color);
            Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(text.Foreground).Color);
            Assert.Equal(0, Assert.IsType<SolidColorBrush>(progress.Background).Color.A);

            button.IsHitTestVisible = true;
            button.IsEnabled = false;
            progress.Visibility = Visibility.Collapsed;
            WindowCanvasTests.LayoutWindow(window, size);
            Assert.Equal(Assert.IsType<SolidColorBrush>(window.FindResource("Brush.SurfaceDisabled")).Color,
                Assert.IsType<SolidColorBrush>(surface.Background).Color);
            Assert.Equal(Assert.IsType<SolidColorBrush>(window.FindResource("Brush.TextDisabled")).Color,
                Assert.IsType<SolidColorBrush>(text.Foreground).Color);
            return true;
        });
    }

    public static TheoryData<string> EveryCaption()
    {
        var captions = new TheoryData<string>
        {
            "Играть",
            "Включите Steam",
            "Проверка сборок",
            "Игра запущена",
            "Не удалось подготовить сборку",
        };
        var already = new HashSet<string>(StringComparer.Ordinal) { "Играть", "Включите Steam", "Проверка сборок", "Игра запущена", "Не удалось подготовить сборку" };
        // Several stages settle on the same words; each is measured once.
        foreach (var caption in Captions()) if (already.Add(caption)) captions.Add(caption);
        return captions;
    }

    [Theory]
    [MemberData(nameof(EveryCaption))]
    public void EveryCaption_FitsInsideTheButton(string caption)
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var window = WindowCanvasTests.LoadWindow();
            var button = Assert.IsType<Button>(window.FindName("PlayButton"));
            var text = Assert.IsType<TextBlock>(window.FindName("PlayButtonText"));
            text.Text = caption;
            button.IsHitTestVisible = false;
            WindowCanvasTests.LayoutWindow(window, new Size(804, 521));

            Assert.Equal((FontFamily)window.FindResource("Font.Family"), text.FontFamily);
            Assert.Equal((FontWeight)window.FindResource("Font.WeightEmphasis"), text.FontWeight);
            Assert.Equal((double)window.FindResource("Font.SizeDisplay"), text.FontSize);
            Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
            var actions = Assert.IsType<Grid>(window.FindName("LaunchActions"));
            Assert.Equal(Assert.IsType<Grid>(window.FindName("PlayArea")).ColumnDefinitions[0].ActualWidth, actions.ActualWidth, 3);
            Assert.Equal(Assert.IsType<Grid>(window.FindName("BodyGrid")).ActualWidth, actions.ActualWidth, 3);
            Assert.Equal(Assert.IsType<Button>(window.FindName("LibraryButton")).ActualWidth * 1.5, button.ActualWidth, 3);
            var bounds = text.TransformToAncestor(button)
                .TransformBounds(new Rect(new Point(), text.RenderSize));
            Assert.True(bounds.Left >= button.BorderThickness.Left - 0.5 &&
                bounds.Right <= button.ActualWidth - button.BorderThickness.Right + 0.5,
                $"'{caption}' runs outside the button horizontally: {bounds}");
            Assert.True(bounds.Top >= button.BorderThickness.Top - 0.5 &&
                bounds.Bottom <= button.ActualHeight - button.BorderThickness.Bottom + 0.5,
                $"'{caption}' runs outside the button vertically: {bounds}");
            var requiredHeight = text.DesiredSize.Height;
            Assert.True(text.ActualHeight >= requiredHeight - 0.5,
                $"'{caption}' needs {requiredHeight} DIP, but only {text.ActualHeight} DIP is visible");
            return true;
        });
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
        // Every Java the catalogue pins, not just the one this build defaults
        // to. A 1.16 pack fetches Java 8, whose version reads "1.8.0_504" - the
        // longest of the four - and it was never measured while this named the
        // default alone.
        var subjects = JavaRuntimeCatalog.Releases
            .Select(release => $"Java {release.JavaVersion}")
            .Concat(["Minecraft", "NeoForge", "Fabric", "Quilt", "Forge", "Файлы"])
            .ToArray();
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

}
