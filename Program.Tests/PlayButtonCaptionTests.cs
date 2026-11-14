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
    public void ThePlaySurface_StaysGreenWhilePreparingAndMutesOnlyWhenDisabled()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var window = WindowCanvasTests.LoadWindow();
            var button = Assert.IsType<Button>(window.FindName("PlayButton"));
            var text = Assert.IsType<TextBlock>(window.FindName("PlayButtonText"));
            var progress = Assert.IsType<ProgressBar>(window.FindName("PlayProgressBar"));
            var size = new Size(804, 521);
            // A different Windows accent must not recolour the explicitly green action.
            window.Resources["Brush.Accent"] = new SolidColorBrush(Colors.Magenta);
            button.IsEnabled = true;
            WindowCanvasTests.LayoutWindow(window, size);
            var surface = Assert.IsType<Border>(button.Template.FindName("Surface", button));
            var green = Assert.IsType<SolidColorBrush>(window.FindResource("Brush.Primary")).Color;
            Assert.True(green.G > green.R && green.G > green.B);
            Assert.Equal(green, Assert.IsType<SolidColorBrush>(surface.Background).Color);
            Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(text.Foreground).Color);

            button.IsHitTestVisible = false;
            progress.Visibility = Visibility.Visible;
            progress.Value = 50;
            text.Text = "Проверка сборки";
            WindowCanvasTests.LayoutWindow(window, size);
            Assert.True(button.IsEnabled);
            Assert.Equal(green, Assert.IsType<SolidColorBrush>(surface.Background).Color);
            Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(text.Foreground).Color);
            Assert.Equal(0, Assert.IsType<SolidColorBrush>(progress.Background).Color.A);
            var fill = Assert.IsType<Border>(progress.Template.FindName("PART_Indicator", progress));
            var fillColor = Assert.IsType<SolidColorBrush>(fill.Background).Color;
            Assert.True(fillColor.G > fillColor.R && fillColor.G > fillColor.B);
            Assert.True(fillColor.R < green.R && fillColor.G < green.G && fillColor.B < green.B);
            progress.IsIndeterminate = true;
            WindowCanvasTests.LayoutWindow(window, size);
            var moving = Assert.IsType<Border>(progress.Template.FindName("PART_GlowRect", progress));
            Assert.Equal(Visibility.Visible, moving.Visibility);
            Assert.Equal(fillColor, Assert.IsType<SolidColorBrush>(moving.Background).Color);
            var track = Assert.IsType<RoundedClipBorder>(progress.Template.FindName("PART_Track", progress));
            var clipped = Assert.IsType<Grid>(track.Child);
            var clip = Assert.IsType<RectangleGeometry>(clipped.Clip);
            Assert.Equal(((CornerRadius)window.FindResource("Radius.Primary")).TopLeft, clip.RadiusX);
            Assert.Equal(new Rect(clipped.RenderSize), clip.Rect);
            Assert.False(clip.FillContains(new Point(0, 0)));
            Assert.False(clip.FillContains(new Point(clipped.ActualWidth, 0)));
            Assert.True(clip.FillContains(new Point(clipped.ActualWidth / 2, clipped.ActualHeight / 2)));
            // Render an overshooting animation segment at both edges. The
            // parent's rounded geometry must clip pixels, not just its layout.
            progress.IsIndeterminate = false;
            fill.Background = Brushes.Transparent;
            moving.Visibility = Visibility.Visible;
            moving.BeginAnimation(FrameworkElement.MarginProperty, null);
            moving.Width = track.ActualWidth * 2;
            moving.Margin = new Thickness(-track.ActualWidth / 2, 0, 0, 0);
            WindowCanvasTests.LayoutWindow(window, size);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(track.ActualWidth),
                (int)Math.Ceiling(track.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(track);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            Assert.Equal(0, pixels[3]);
            Assert.Equal(0, pixels[(bitmap.PixelWidth - 1) * 4 + 3]);
            Assert.True(pixels[((bitmap.PixelHeight / 2) * bitmap.PixelWidth + bitmap.PixelWidth / 2) * 4 + 3] > 0);

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

    [Fact]
    public void JavaCaptionKeepsItsStageNumberAndDownloadNumbers()
    {
        var progress = new RuntimePreparationProgress(
            RuntimePreparationStage.InstallingJava, "Java 1/2",
            DownloadedBytes: 1024, TotalBytes: 2048);
        Assert.Equal("Java 1/2: 1 / 2 КБ (1 КБ/с)", PlayButtonCaption.For(progress, 1024));
        Assert.Equal("Java 2/2", PlayButtonCaption.For(progress with { Message = "Java 2/2", TotalBytes = 0 }, 0));
    }

    [Fact]
    public void OnlyRepeatedLabelsAreNumberedInOrder()
    {
        Assert.Equal(
            ["Minecraft 1/2", "Forge 1/3", "Java", "Forge 2/3", "Minecraft 2/2", "Forge 3/3"],
            PreparationLabels.NumberRepeated("Minecraft", "Forge", "Java", "Forge", "Minecraft", "Forge"));
    }

    private static IEnumerable<string> Captions()
    {
        var byteStages = new[]
        {
            RuntimePreparationStage.SyncingPack,
            RuntimePreparationStage.Downloading,
            RuntimePreparationStage.InstallingJava,
        };
        string[] subjects = ["Java", "Java 1/2", "Minecraft", "Minecraft 2/2", "NeoForge",
            "Fabric", "Quilt", "Forge", "NeoForge 2/2", "Legacy Fabric 2/2", "Файлы"];
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
