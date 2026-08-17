using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Minecraft.Tests;

/// <summary>
/// A name longer than its field walks to its end and back so all of it can be
/// read. These hold the two halves of that promise: the pace does not depend
/// on the window, and the walk happens exactly when there is something hidden
/// and the field is not being edited.
/// </summary>
public sealed class NameMarqueeTests
{
    // The name field in the launcher is about this wide.
    private const double Wide = 96;
    private const double Tall = 30;

    /// <summary>The walk is paced by distance, so a longer tail takes longer.</summary>
    [Fact]
    public void TheWalk_IsPacedByHowMuchIsHidden()
    {
        var shortTail = MarqueeSchedule.For(48);
        var longTail = MarqueeSchedule.For(96);

        Assert.Equal(TimeSpan.FromSeconds(2), shortTail.Travel);
        Assert.Equal(TimeSpan.FromSeconds(4), longTail.Travel);
        Assert.Equal(shortTail.Hold, longTail.Hold);
        Assert.Equal(longTail.Hold + longTail.Travel + longTail.Hold + longTail.Travel, longTail.Cycle);
    }

    /// <summary>
    /// A tail of a few pixels is the common case - a name one letter too long -
    /// and it has to be slow enough to be seen as movement rather than a blink.
    /// </summary>
    [Fact]
    public void AVeryShortWalk_IsSlowedDownUntilItReads()
    {
        Assert.Equal(TimeSpan.FromSeconds(1.5), MarqueeSchedule.For(7).Travel);
        Assert.Equal(TimeSpan.FromSeconds(1.5), MarqueeSchedule.For(0).Travel);
        Assert.True(MarqueeSchedule.For(7).Cycle > TimeSpan.FromSeconds(4),
            "a seven pixel walk that is over in a moment is the flicker this was written to avoid");
    }

    /// <summary>
    /// A drawn field knows exactly how much of the name it is hiding, and that
    /// answer wins: the room inside a TextBox is not its width less padding and
    /// border, and guessing it that way once cost a real name its walk.
    /// </summary>
    [Fact]
    public void TheFieldsOwnAnswer_WinsOverTheGuess()
    {
        // The numbers a real launcher window reports for "MarkZamore": the field
        // hides 7 pixels of it, while padding and border suggest 13 to spare.
        Assert.Equal(7.1, NameMarquee.HiddenTail(extentWidth: 76.8, viewportWidth: 69.7, textWidth: 76.8, room: 89.7), 3);
        // Before the first layout the field has nothing to say, so the guess stands.
        Assert.Equal(20, NameMarquee.HiddenTail(extentWidth: 0, viewportWidth: 0, textWidth: 100, room: 80), 3);
        // And a name that fits hides nothing, never a negative tail.
        Assert.Equal(0, NameMarquee.HiddenTail(extentWidth: 40, viewportWidth: 80, textWidth: 40, room: 80), 3);
    }

    /// <summary>A name that fits stands still; the field is left where it was.</summary>
    [Fact]
    public void AName_ThatFits_DoesNotMove()
    {
        var walking = OnAStaThread(() =>
        {
            var field = Field("Oskar");
            var marquee = new NameMarquee(field);
            marquee.Refresh();
            return marquee.IsWalking;
        });

        Assert.False(walking);
    }

    /// <summary>A name that does not fit walks exactly what the field hides.</summary>
    [Fact]
    public void AName_ThatDoesNotFit_WalksItsHiddenTail()
    {
        var (walking, tail, hidden) = OnAStaThread(() =>
        {
            var field = Field("MarkZamoreLongest");
            var marquee = new NameMarquee(field);
            marquee.Refresh();
            return (marquee.IsWalking, marquee.Tail, marquee.Tail);
        });

        Assert.True(hidden > 1, $"the test name should not fit the field, but it hides only {hidden:0.0} px");
        Assert.True(hidden < 120, $"the tail cannot be wider than the name itself: {hidden:0.0} px");
        Assert.True(walking);
        Assert.Equal(hidden, tail, 3);
    }

    /// <summary>Editing stops the walk and puts the field back to its first letter.</summary>
    [Fact]
    public void Editing_StopsTheWalk()
    {
        var (walkingWhileEditing, offset) = OnAStaThread(() =>
        {
            var field = Field("MarkZamoreLongest");
            var marquee = new NameMarquee(field);
            marquee.Refresh();
            marquee.SetAllowed(false);
            return (marquee.IsWalking, field.HorizontalOffset);
        });

        Assert.False(walkingWhileEditing);
        Assert.Equal(0, offset, 3);
    }

    /// <summary>
    /// A field the size the window gives it, with the launcher's own typeface -
    /// the width of a name is a property of the font, not of the layout.
    /// </summary>
    private static TextBox Field(string name)
    {
        var program = Path.GetDirectoryName(FindRepositoryFile("Program", "MainWindow.xaml"))!;
        var fonts = new Uri(Path.Combine(program, "Fonts") + Path.DirectorySeparatorChar);
        var field = new TextBox
        {
            Text = name,
            FontFamily = new FontFamily(fonts, "./#Montserrat, Segoe UI"),
            FontSize = 12,
            IsReadOnly = true,
        };
        field.Width = Wide;
        field.Measure(new Size(Wide, Tall));
        field.Arrange(new Rect(0, 0, Wide, Tall));
        field.UpdateLayout();
        return field;
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
        Assert.True(thread.Join(TimeSpan.FromMinutes(1)), "the field did not measure in a minute");
        if (failure is not null) throw new InvalidOperationException("The field could not be measured.", failure);
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
