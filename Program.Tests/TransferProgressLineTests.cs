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
/// the risk in adding an estimate to it, and it is measured here with the
/// launcher's own control, style and typeface rather than guessed at.
/// </summary>
public sealed class TransferProgressLineTests
{
    /// <summary>The estimate only appears once the answer is worth something.</summary>
    [Theory]
    [InlineData(0, 1_000_000)]        // nothing left to wait for
    [InlineData(-5, 1_000_000)]       // more delivered than promised
    [InlineData(1_000_000, 0)]        // the rate window has not filled yet
    [InlineData(1_000_000, -1)]
    [InlineData(long.MaxValue, 1)]    // a wait nobody is going to sit through
    public void WithoutAnHonestAnswer_ItSaysNothing(long remainingBytes, double bytesPerSecond)
    {
        Assert.Null(TransferProgressLine.Remaining(remainingBytes, bytesPerSecond));
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
        Assert.Equal(expected, TransferProgressLine.Remaining(seconds, 1));
    }

    /// <summary>
    /// A second left is still a second: rounding to the nearest five must never
    /// round down to a finish that has not happened.
    /// </summary>
    [Fact]
    public void ItNeverRoundsDownToNothing()
    {
        for (var remaining = 1; remaining <= 4; remaining++)
        {
            Assert.Equal("ещё ≈ 5 с", TransferProgressLine.Remaining(remaining, 1));
        }
    }

    [Fact]
    public void TheLine_CarriesTheStageTheBytesTheSpeedAndTheWait()
    {
        Assert.Equal(
            "Отправка мира: 500 МБ / 1 ГБ (5 МБ/с, ещё ≈ 1 мин 40 с)",
            TransferProgressLine.Compose("Отправка мира", 500 * 1024 * 1024L, 1024 * 1024 * 1024L, 5 * 1024 * 1024));
    }

    /// <summary>Without a stage there is no prefix, and no empty colon either.</summary>
    [Fact]
    public void WithoutAStage_TheLineStartsWithTheBytes()
    {
        Assert.StartsWith("500 МБ", TransferProgressLine.Compose("", 500 * 1024 * 1024L, 1024 * 1024 * 1024L, 0));
    }

    /// <summary>
    /// The widest line the launcher can put on that bar, against the width the
    /// bar actually has.
    ///
    /// The size and the wait pull against each other - the nearer the end, the
    /// shorter the estimate, and a rate slow enough to make the estimate long
    /// is short to write down - so the widest line is not one anybody can pick
    /// by eye. Every combination worth worrying about is measured and the
    /// widest of them has to fit.
    /// </summary>
    [Fact]
    public void TheWidestLineItCanWrite_FitsTheBar()
    {
        // The longest label the transfer publishes with a byte count behind it.
        const string stage = "Копирование мира";
        long[] totals = [1024L * 1024 * 1024, (long)(9.99 * 1024 * 1024 * 1024)];
        double[] shares = [0, 0.5, 0.9, 0.999];
        // A dial-up trickle, the slowest rate that still reaches a day, the
        // 5 MB/s a Steam relay gives, and two speeds only a local disk reaches.
        double[] rates = [512, 124_500, 5 * 1024 * 1024, 999.99 * 1024 * 1024, 1.5 * 1024 * 1024 * 1024];

        var lines = (from total in totals
                     from share in shares
                     from rate in rates
                     select TransferProgressLine.Compose(stage, (long)(total * share), total, rate))
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
        // Measured at the time of writing: 412.6px of the 454px the bar has.
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
        var line = TransferProgressLine.Compose("Копирование мира", 1, 2, 1) + "чсминГБКМ0123456789";

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
