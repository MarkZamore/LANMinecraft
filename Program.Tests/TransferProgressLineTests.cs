using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The words on the world-transfer bar, and whether they fit on it.
///
/// The bar is a fixed width inside a fixed canvas and the text is centred over
/// it with nothing to trim or wrap it, so a line that grew too long would not
/// shrink or clip - it would spill sideways over the Передать button. That is
/// the risk in writing an estimate on it, and it is measured here with the
/// launcher's own control, style and typeface rather than guessed at.
/// </summary>
public sealed class TransferProgressLineTests
{
    /// <summary>The estimate only appears once there is an answer worth giving.</summary>
    [Fact]
    public void WithoutAnHonestAnswer_ItSaysNothing()
    {
        Assert.Null(TransferProgressLine.Remaining(null));
        Assert.Null(TransferProgressLine.Remaining(TimeSpan.FromSeconds(-1)));
        // Past a day the arithmetic is describing a stall, not a wait.
        Assert.Null(TransferProgressLine.Remaining(TimeSpan.FromDays(1.01)));
    }

    /// <summary>
    /// How coarse the answer is depends on how long the wait is: seconds while
    /// seconds are what somebody is waiting out, whole minutes once they are not.
    /// </summary>
    [Theory]
    [InlineData(12, "ещё ≈ 10 с")]
    [InlineData(47, "ещё ≈ 45 с")]
    [InlineData(58, "ещё ≈ 1 мин")]
    [InlineData(200, "ещё ≈ 3 мин 20 с")]
    [InlineData(599, "ещё ≈ 10 мин")]
    [InlineData(1_637, "ещё ≈ 27 мин")]
    [InlineData(3_598, "ещё ≈ 1 ч")]
    [InlineData(8_100, "ещё ≈ 2 ч 15 мин")]
    public void TheWaitIsRoundedToWhatSomebodyWouldRead(int seconds, string expected)
    {
        Assert.Equal(expected, TransferProgressLine.Remaining(TimeSpan.FromSeconds(seconds)));
    }

    /// <summary>
    /// A second left is still a second: rounding to the nearest five must never
    /// round down to a finish that has not happened.
    /// </summary>
    [Fact]
    public void ItNeverRoundsDownToNothing()
    {
        for (var seconds = 1; seconds <= 4; seconds++)
        {
            Assert.Equal("ещё ≈ 5 с", TransferProgressLine.Remaining(TimeSpan.FromSeconds(seconds)));
        }
    }

    [Fact]
    public void TheLine_CarriesTheStepTheBytesTheSpeedAndTheWait()
    {
        Assert.Equal(
            "Отправка мира: 500 МБ / 1 ГБ (5 МБ/с, ещё ≈ 4 мин)",
            TransferProgressLine.Compose(
                "Отправка мира", 500 * 1024 * 1024L, 1024 * 1024 * 1024L, 5 * 1024 * 1024,
                TimeSpan.FromSeconds(240)));
    }

    /// <summary>Before the handover can be divided by, the line simply has no wait on it.</summary>
    [Fact]
    public void WithoutAnEstimate_TheLineIsWhatItAlwaysWas()
    {
        Assert.Equal(
            "Отправка мира: 500 МБ / 1 ГБ (5 МБ/с)",
            TransferProgressLine.Compose(
                "Отправка мира", 500 * 1024 * 1024L, 1024 * 1024 * 1024L, 5 * 1024 * 1024, null));
    }

    /// <summary>Without a step there is no prefix, and no empty colon either.</summary>
    [Fact]
    public void WithoutAStep_TheLineStartsWithTheBytes()
    {
        Assert.StartsWith(
            "500 МБ",
            TransferProgressLine.Compose("", 500 * 1024 * 1024L, 1024 * 1024 * 1024L, 0, null));
    }

    /// <summary>
    /// The unit is written once when both numbers land on it. The bar is a
    /// fixed width and "8,99 ГБ / 9,99 ГБ" spends eight characters saying ГБ
    /// twice.
    /// </summary>
    [Theory]
    [InlineData(500L * 1024 * 1024, 1024L * 1024 * 1024, "500 МБ / 1 ГБ")]
    [InlineData(900L * 1024 * 1024, 1000L * 1024 * 1024, "900 / 1000 МБ")]
    [InlineData(0L, 1024L * 1024 * 1024, "0 Б / 1 ГБ")]
    public void TheUnitIsNotWrittenTwice(long current, long total, string expected)
    {
        Assert.Equal(expected, TransferProgressLine.FormatPair(current, total));
    }

    /// <summary>
    /// When all four facts will not fit on the bar, the speed is the one that
    /// goes - the estimate answers "is this stuck" better than it did.
    /// </summary>
    [Fact]
    public void WhenTheLineWouldNotFit_TheSpeedComesOffIt()
    {
        var line = TransferProgressLine.Compose(
            "Копирование у отправителя", (long)(1022.98 * 1024 * 1024), 1024L * 1024 * 1024,
            999.99 * 1024 * 1024, TimeSpan.FromMinutes(1435));

        Assert.Equal("Копирование у отправителя: 1022,98 МБ / 1 ГБ (ещё ≈ 23 ч 55 мин)", line);
        // The same numbers under a short step name keep the speed.
        Assert.Contains(
            "МБ/с",
            TransferProgressLine.Compose(
                "Сжатие мира", (long)(1022.98 * 1024 * 1024), 1024L * 1024 * 1024,
                999.99 * 1024 * 1024, TimeSpan.FromMinutes(27)));
    }

    /// <summary>
    /// A step with no byte count of its own still carries the estimate: the
    /// handover has not stopped being measured just because this part of it
    /// cannot be counted in bytes.
    /// </summary>
    [Fact]
    public void AStepWithoutBytes_StillSaysHowLongIsLeft()
    {
        Assert.Equal(
            "Подготовка профилей... ещё ≈ 2 мин",
            TransferProgressLine.ComposeWaiting("Подготовка профилей", TimeSpan.FromSeconds(120)));
        Assert.Equal(
            "Подготовка профилей...",
            TransferProgressLine.ComposeWaiting("Подготовка профилей", null));
        Assert.Equal("Передача...", TransferProgressLine.ComposeWaiting("", null));
    }

    /// <summary>
    /// The widest line the launcher can put on that bar, against the width the
    /// bar actually has. Size, speed and wait vary independently now that the
    /// estimate covers the whole handover rather than this step, so the widest
    /// line is not one to pick by eye: every combination worth worrying about
    /// is measured and the widest of them has to fit.
    /// </summary>
    [Fact]
    public void TheWidestLineItCanWrite_FitsTheBar()
    {
        // Every label the transfer publishes, on either side of it, so the
        // longest one is covered without anybody having to pick it out.
        var stages = TransferPacing.Sending.Concat(TransferPacing.Receiving).Distinct().ToList();
        long[] totals = [1024L * 1024 * 1024, (long)(9.99 * 1024 * 1024 * 1024)];
        double[] shares = [0, 0.5, 0.9, 0.999];
        // A dial-up trickle, a Steam relay, and two speeds only a disk reaches.
        double[] rates = [512, 124_500, 5 * 1024 * 1024, 999.99 * 1024 * 1024];
        TimeSpan?[] waits =
        [
            null,
            TimeSpan.FromSeconds(45),
            TimeSpan.FromSeconds(200),
            TimeSpan.FromMinutes(27),
            TimeSpan.FromMinutes(1435)   // 23 h 55 min: the last one still shown
        ];

        var lines = (from stage in stages
                     from total in totals
                     from share in shares
                     from rate in rates
                     from wait in waits
                     select TransferProgressLine.Compose(stage, (long)(total * share), total, rate, wait))
                    .Concat(from stage in stages
                            from wait in waits
                            select TransferProgressLine.ComposeWaiting(stage, wait))
                    .Distinct()
                    .ToList();

        var (available, widest, needed) = WindowCanvasTests.OnAStaThread(() =>
        {
            var window = WindowCanvasTests.LoadWindow();
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(window.Width, double.PositiveInfinity));
            content.Arrange(new Rect(new Point(0, 0), content.DesiredSize));
            content.UpdateLayout();

            var bar = (Grid)window.FindName("TransferProgressArea")!;
            var text = (TextBlock)window.FindName("TransferProgressText")!;
            var measured = lines
                .Select(line => (line, width: Width(text, line)))
                .OrderByDescending(pair => pair.width)
                .First();
            return (bar.ActualWidth, measured.line, measured.width);
        });

        Assert.True(available > 0, "the transfer bar measured to nothing; the layout pass did not run");
        Assert.True(
            needed <= available,
            $"\"{widest}\" needs {needed:0.#}px and the bar is {available:0.#}px wide; " +
            "it would spill sideways over the Передать button");
    }

    /// <summary>
    /// Montserrat is shipped with the launcher, and a character it has no glyph
    /// for is drawn as a hollow box. The estimate introduced one that no other
    /// line in the window uses.
    /// </summary>
    [Fact]
    public void EveryCharacterTheEstimateUses_HasAGlyphInTheShippedTypeface()
    {
        var line = string.Concat(TransferPacing.Sending)
            + string.Concat(TransferPacing.Receiving)
            + TransferProgressLine.Compose("", 1, 2, 1, TimeSpan.FromSeconds(9000))
            + "чсминГБКМ0123456789";

        var missing = WindowCanvasTests.OnAStaThread(() =>
        {
            var window = WindowCanvasTests.LoadWindow();
            var text = (TextBlock)window.FindName("TransferProgressText")!;
            var typeface = new Typeface(
                text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
            Assert.True(typeface.TryGetGlyphTypeface(out var glyphs), "the shipped typeface did not load");
            return line.Distinct()
                .Where(character => !glyphs.CharacterToGlyphMap.ContainsKey(character))
                .ToList();
        });

        Assert.Empty(missing);
    }

    private static double Width(TextBlock text, string content)
    {
        text.Text = content;
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return text.DesiredSize.Width;
    }
}
