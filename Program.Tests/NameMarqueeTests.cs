using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

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
        var walking = WindowCanvasTests.OnAStaThread(() =>
        {
            using var fixture = new ShownField("Oskar");
            var field = fixture.Field;
            var marquee = new NameMarquee(field);
            marquee.Refresh();
            Assert.False(IsAnimated(field));
            return marquee.IsWalking;
        });

        Assert.False(walking);
    }

    /// <summary>A name that does not fit walks exactly what the field hides.</summary>
    [Fact]
    public void AName_ThatDoesNotFit_WalksItsHiddenTail()
    {
        var (walking, tail, hidden) = WindowCanvasTests.OnAStaThread(() =>
        {
            using var fixture = new ShownField("MarkZamoreLongest");
            var field = fixture.Field;
            var marquee = new NameMarquee(field);
            marquee.Refresh();
            WaitFor(() => IsAnimated(field), "The visible overflowing field did not attach its WPF animation clock.");
            return (marquee.IsWalking, marquee.Tail, field.ExtentWidth - field.ViewportWidth);
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
        var (walkingWhileEditing, offset) = WindowCanvasTests.OnAStaThread(() =>
        {
            using var fixture = new ShownField("MarkZamoreLongest");
            var field = fixture.Field;
            var marquee = new NameMarquee(field);
            marquee.Refresh();
            marquee.SetAllowed(false);
            AssertStopped(field);
            fixture.Window.UpdateLayout();
            return (marquee.IsWalking, field.HorizontalOffset);
        });

        Assert.False(walkingWhileEditing);
        Assert.Equal(0, offset, 3);
    }

    [Fact]
    public void HiddenAndUnloadedFieldsReleaseTheirClockAndResumeWhenShown()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            using var fixture = new ShownField("MarkZamoreLongest");
            var field = fixture.Field;
            var marquee = new NameMarquee(field);
            marquee.Refresh();
            WaitForMovement(field);

            fixture.Parent.Visibility = Visibility.Hidden;
            Assert.Equal(Visibility.Visible, field.Visibility);
            WaitFor(() => !field.IsVisible, "The ancestor did not hide its field.");
            field.Text = "MarkZamoreLongestHidden";
            marquee.Refresh();
            fixture.Window.UpdateLayout();
            AssertStopped(field);

            fixture.Parent.Visibility = Visibility.Visible;
            WaitFor(() => IsAnimated(field), "Showing the overflowing field did not attach a new clock.");
            field.Visibility = Visibility.Collapsed;
            marquee.Refresh();
            AssertStopped(field);
            field.Visibility = Visibility.Visible;
            WaitFor(() => IsAnimated(field), "Expanding the field did not attach a new clock.");

            fixture.Parent.Children.Remove(field);
            WaitFor(() => !field.IsLoaded, "The removed field did not unload.");
            field.Text = "MarkZamoreLongestDetached";
            marquee.Refresh();
            AssertStopped(field);

            var replacement = new Window
            {
                Resources = fixture.Window.Resources, Content = field, Width = 320, Height = 160,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
                ShowActivated = false, ShowInTaskbar = false, UseLayoutRounding = true
            };
            try
            {
                replacement.Show();
                ((HwndTarget)PresentationSource.FromVisual(replacement)!.CompositionTarget).RenderMode = RenderMode.SoftwareOnly;
                WaitForMovement(field);
                fixture.Window.Close();
                Assert.True(IsAnimated(field), "Closing the previous host must not stop the reparented field.");
                WaitForMovement(field);
                replacement.Close();
                field.Text = "MarkZamoreLongestClosed";
                marquee.Refresh();
                AssertStopped(field);
            }
            finally { replacement.Close(); }
            return true;
        });
    }

    [Fact]
    public void MinimizedWindowsReleaseTheClockAndResumeOnlyForVisibleOverflow()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            using var fixture = new ShownField("MarkZamoreLongest");
            var field = fixture.Field;
            var marquee = new NameMarquee(field);
            marquee.Refresh();
            WaitForMovement(field);

            fixture.Window.WindowState = WindowState.Minimized;
            WaitFor(() => fixture.Window.WindowState == WindowState.Minimized, "The fixture window did not minimize.");
            // IsVisible alone is insufficient: minimized Windows can retain a
            // visible and loaded logical subtree while it is absent onscreen.
            field.Text = "MarkZamoreLongestMinimized";
            marquee.Refresh();
            AssertStopped(field);
            fixture.Parent.Visibility = Visibility.Hidden;
            fixture.Window.WindowState = WindowState.Normal;
            fixture.Window.UpdateLayout();
            AssertStopped(field);
            fixture.Parent.Visibility = Visibility.Visible;
            WaitFor(() => IsAnimated(field), "Restoring a visible overflowing field did not attach its clock.");

            fixture.Window.WindowState = WindowState.Minimized;
            field.Text = "Oskar";
            fixture.Window.WindowState = WindowState.Normal;
            fixture.Window.UpdateLayout();
            WaitFor(() => field.ViewportWidth > 0 && field.ExtentWidth <= field.ViewportWidth + 1,
                "The restored short name did not fit its real viewport.");
            AssertStopped(field);

            field.Text = "MarkZamoreLongest";
            fixture.Window.UpdateLayout();
            WaitFor(() => IsAnimated(field), "The longer name did not start after the text changed.");
            marquee.SetAllowed(false);
            fixture.Window.WindowState = WindowState.Minimized;
            fixture.Window.WindowState = WindowState.Normal;
            marquee.Refresh();
            fixture.Window.UpdateLayout();
            AssertStopped(field);
            marquee.SetAllowed(true);
            WaitForMovement(field);
            return true;
        });
    }

    /// <summary>
    /// A field the size the window gives it, with the launcher's own typeface -
    /// the width of a name is a property of the font, not of the layout.
    /// </summary>
    private sealed class ShownField : IDisposable
    {
        public Window Window { get; } = WindowCanvasTests.LoadWindow();
        public TextBox Field { get; }
        public Panel Parent { get; }
        public ShownField(string name)
        {
            Field = Assert.IsType<TextBox>(Window.FindName("PlayerNameTextBox"));
            Parent = Assert.IsAssignableFrom<Panel>(Field.Parent);
            Field.Text = name;
            Field.Width = Wide;
            Window.WindowStartupLocation = WindowStartupLocation.Manual;
            Window.Left = -32000; Window.Top = -32000;
            Window.ShowActivated = false; Window.ShowInTaskbar = false;
            Window.Show();
            ((HwndTarget)PresentationSource.FromVisual(Window)!.CompositionTarget).RenderMode = RenderMode.SoftwareOnly;
            WindowCanvasTests.LayoutWindow(Window, new Size(804, 521));
            WaitFor(() => Field.IsLoaded && Field.IsVisible && Field.ViewportWidth > 0,
                "The real name field did not establish its visible viewport.");
        }
        public void Dispose() => Window.Close();
    }

    private static bool IsAnimated(TextBox field) =>
        DependencyPropertyHelper.GetValueSource(field, NameMarquee.HorizontalOffsetProperty).IsAnimated;

    private static double AnimatedOffset(TextBox field) => (double)field.GetValue(NameMarquee.HorizontalOffsetProperty);

    private static void WaitForMovement(TextBox field)
    {
        WaitFor(() => IsAnimated(field) && AnimatedOffset(field) > 0.01,
            "The attached WPF clock did not advance the overflowing name after its reading hold.");
        var start = AnimatedOffset(field);
        WaitFor(() => IsAnimated(field) && Math.Abs(AnimatedOffset(field) - start) > 0.01,
            "The real animated offset stopped advancing.");
    }

    private static void AssertStopped(TextBox field)
    {
        WaitFor(() => !IsAnimated(field), "An inactive name field retained an animation clock.");
        Assert.Equal(0, AnimatedOffset(field), 3);
        Pump(30);
        var before = field.HorizontalOffset;
        Pump(120);
        Assert.False(IsAnimated(field));
        Assert.Equal(0, AnimatedOffset(field), 3);
        Assert.Equal(before, field.HorizontalOffset, 3);
    }

    private static void WaitFor(Func<bool> condition, string failure)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed.TotalSeconds < 4) Pump(10);
        Assert.True(condition(), failure);
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }
}
