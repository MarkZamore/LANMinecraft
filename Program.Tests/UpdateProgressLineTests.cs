using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Minecraft.Tests;

/// <summary>
/// The update bar was the one with no guard: its lines were written inline in
/// the window, and nothing measured them. These are the same two questions the
/// other two bars are asked - does every line fit, and does it say enough.
/// </summary>
public class UpdateProgressLineTests
{
    /// <summary>
    /// Every line this bar can draw, in its worst realistic instantiation.
    /// </summary>
    private static List<string> EveryLine()
    {
        int[] releases = [UpdateService.CurrentReleaseNumber, 1000, 9999];
        long[] totals = [1024L * 1024, 144_697_727L, 1024L * 1024 * 1024];
        double[] shares = [0d, 0.5d, 0.999d];
        double[] rates = [0d, 1024d, 12.3d * 1024 * 1024, 1024d * 1024 * 1024];
        TimeSpan?[] waits =
        [
            null,
            TimeSpan.FromSeconds(9),
            TimeSpan.FromSeconds(65),
            TimeSpan.FromMinutes(27),
            TimeSpan.FromMinutes(1435)
        ];

        var lines = new List<string>();
        foreach (var release in releases)
        {
            lines.Add(UpdateProgressLine.UpToDate(release));
            lines.Add(UpdateProgressLine.Ready(release));
            lines.Add(UpdateProgressLine.Applying(release));
            lines.Add(UpdateProgressLine.Starting(release));
            lines.Add(UpdateProgressLine.DownloadFailed(release));
            foreach (var total in totals)
            foreach (var share in shares)
            foreach (var rate in rates)
            foreach (var wait in waits)
            {
                lines.Add(UpdateProgressLine.Downloading(
                    release, (long)(total * share), total, rate, wait));
            }
        }
        lines.Add(UpdateProgressLine.Checking());
        lines.Add(UpdateProgressLine.CheckFailed());
        return lines.Distinct().ToList();
    }

    /// <summary>
    /// Nothing spills over the "Обновить" button beside it. The text is centred
    /// over the bar with nothing to wrap or trim it, so a line too wide does not
    /// shrink - it runs out over its neighbour.
    /// </summary>
    [Fact]
    public void EveryLine_FitsTheBar()
    {
        var lines = EveryLine();

        var (available, widest, needed) = WindowCanvasTests.OnAStaThread(() =>
        {
            var window = WindowCanvasTests.LoadWindow();
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(window.Width, double.PositiveInfinity));
            content.Arrange(new Rect(new Point(0, 0), content.DesiredSize));
            content.UpdateLayout();

            var bar = (ProgressBar)window.FindName("UpdateProgressBar")!;
            var text = (TextBlock)window.FindName("UpdateProgressText")!;
            var measured = lines
                .Select(line => (line, width: Width(text, line)))
                .OrderByDescending(pair => pair.width)
                .First();
            return (bar.ActualWidth, measured.line, measured.width);
        });

        Assert.True(available > 0, "the update bar measured to nothing; the layout pass did not run");
        Assert.True(
            needed <= available,
            $"\"{widest}\" needs {needed:0.#}px and the bar is {available:0.#}px wide; " +
            "it would spill sideways over the Обновить button");
    }

    /// <summary>
    /// And every line that is about a release names it. The bar used to say
    /// "Скачивается обновление" and "Обновление готово к установке" without ever
    /// saying which one, while holding the number the whole time.
    /// </summary>
    [Fact]
    public void EveryLineAboutARelease_NamesIt()
    {
        const int release = 4242;
        string[] named =
        [
            UpdateProgressLine.UpToDate(release),
            UpdateProgressLine.Ready(release),
            UpdateProgressLine.Applying(release),
            UpdateProgressLine.Starting(release),
            UpdateProgressLine.DownloadFailed(release),
            UpdateProgressLine.Downloading(release, 1, 2, 1, null)
        ];
        Assert.All(named, line =>
            Assert.Contains(release.ToString(CultureInfo.InvariantCulture), line, StringComparison.Ordinal));

        // The two that are about no release in particular must not invent one.
        Assert.DoesNotContain(release.ToString(CultureInfo.InvariantCulture), UpdateProgressLine.Checking());
        Assert.DoesNotContain(release.ToString(CultureInfo.InvariantCulture), UpdateProgressLine.CheckFailed());
    }

    /// <summary>
    /// A download line carries the four things the transfer bar carries: what,
    /// how much of it, how fast, and how much longer.
    /// </summary>
    [Fact]
    public void ADownloadLine_CarriesSizeSpeedAndEstimate()
    {
        var line = UpdateProgressLine.Downloading(
            348, 45L * 1024 * 1024, 138L * 1024 * 1024, 12.3d * 1024 * 1024, TimeSpan.FromSeconds(130));

        Assert.Contains("348", line, StringComparison.Ordinal);
        Assert.Contains("МБ", line, StringComparison.Ordinal);
        Assert.Contains("/с", line, StringComparison.Ordinal);
        Assert.Contains("ещё", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The estimate is only offered when the arithmetic has something to divide.
    /// </summary>
    [Fact]
    public void TheEstimate_IsSilentUntilSomethingIsMoving()
    {
        Assert.Null(UpdateProgressLine.Estimate(0, 100, 0));
        Assert.Null(UpdateProgressLine.Estimate(100, 100, 10));
        Assert.Equal(TimeSpan.FromSeconds(9), UpdateProgressLine.Estimate(10, 100, 10));
    }

    private static double Width(TextBlock text, string line)
    {
        text.Text = line;
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return text.DesiredSize.Width;
    }
}
