using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Minecraft;

/// <summary>
/// How long one walk across a hidden tail takes, and how long the name rests
/// at each end before turning back. Kept apart from the animation so the pace
/// can be reasoned about - and tested - without a window.
/// </summary>
/// <param name="Travel">One crossing, start to end.</param>
/// <param name="Hold">The pause at each end, so the ends can be read.</param>
internal readonly record struct MarqueeSchedule(TimeSpan Travel, TimeSpan Hold)
{
    // Reading pace in device-independent pixels per second. Resizing changes
    // the available field width, while the font and reading pace stay fixed.
    private const double PixelsPerSecond = 24;
    // A name may hide only a letter - "MarkZamore" hides seven pixels of itself -
    // and seven pixels crossed quickly is not motion, it is a flicker nobody
    // notices. Every crossing takes at least this long, so the short ones drift
    // slowly enough to be seen for what they are.
    private static readonly TimeSpan ShortestTravel = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan RestAtEnd = TimeSpan.FromSeconds(0.9);

    /// <summary>The pace for a name whose tail hides <paramref name="distance"/> pixels.</summary>
    public static MarqueeSchedule For(double distance)
    {
        var seconds = Math.Max(distance, 0) / PixelsPerSecond;
        var travel = TimeSpan.FromSeconds(seconds);
        return new MarqueeSchedule(travel < ShortestTravel ? ShortestTravel : travel, RestAtEnd);
    }

    /// <summary>There and back, both rests included.</summary>
    public TimeSpan Cycle => Hold + Travel + Hold + Travel;
}

/// <summary>
/// Walks a name that is too long for its field to its end and back, so all of
/// it can be read without touching anything. A name that fits does not move:
/// motion here means "there is more of this than you can see", and on a name
/// that is all there it would say something untrue. The walk stops the moment
/// the field is opened for editing - from then on the caret is the player's.
/// Hidden fields and minimized windows release the animation clock until the
/// name can be read again.
/// </summary>
internal sealed class NameMarquee
{
    // Below this the tail is a rounding error, not a hidden letter.
    private const double SmallestTail = 1;

    /// <summary>
    /// The field's own scroll position, as something WPF can animate. TextBox
    /// offers ScrollToHorizontalOffset but no property to drive, so the
    /// animation runs on this and each step passes the value on.
    /// </summary>
    public static readonly DependencyProperty HorizontalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "HorizontalOffset",
            typeof(double),
            typeof(NameMarquee),
            new PropertyMetadata(0d, OnHorizontalOffsetChanged));

    private readonly TextBox _field;
    private readonly Logger? _logger;
    private Window? _window;
    private bool _allowed = true;
    private double _walking;
    private bool _told;

    public NameMarquee(TextBox field, Logger? logger = null)
    {
        _field = field ?? throw new ArgumentNullException(nameof(field));
        _logger = logger;
        _field.TextChanged += (_, _) => Refresh();
        _field.SizeChanged += (_, _) => Refresh();
        _field.Loaded += OnLoaded;
        _field.Unloaded += OnUnloaded;
        _field.IsVisibleChanged += (_, _) => Refresh();
        // The field can only say how much of the name it hides once it has been
        // laid out, and the first name is put in before that. Every later pass
        // is watched too: the window lays out constantly - progress bars, lists,
        // the timer - and one pass that answered oddly used to stop the walk for
        // good. Only a visible field with a changed tail reaches Refresh.
        _field.LayoutUpdated += OnLayoutUpdated;
        if (_field.IsLoaded) ObserveWindow(Window.GetWindow(_field));
    }

    private bool CanWalk => _allowed && _field.IsLoaded && _field.IsVisible &&
                            _window is { IsVisible: true, WindowState: not WindowState.Minimized };

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ObserveWindow(Window.GetWindow(_field));
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ObserveWindow(null);
        Stop();
    }

    private void ObserveWindow(Window? window)
    {
        if (ReferenceEquals(_window, window)) return;
        if (_window is not null)
        {
            _window.StateChanged -= OnWindowStateChanged;
            _window.Closed -= OnWindowClosed;
        }
        _window = window;
        if (_window is not null)
        {
            _window.StateChanged += OnWindowStateChanged;
            _window.Closed += OnWindowClosed;
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => Refresh();

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        ObserveWindow(null);
        Stop();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!CanWalk || _field.ViewportWidth <= 0) return;
        var tail = Math.Max(0, _field.ExtentWidth - _field.ViewportWidth);
        if (Math.Abs(tail - _walking) < SmallestTail) return;
        Refresh();
    }

    /// <summary>True while a visible name is walking; false when hidden, fitting or edited.</summary>
    public bool IsWalking => _walking > 0;

    /// <summary>The tail currently being walked, in pixels; zero when still.</summary>
    public double Tail => _walking;

    /// <summary>Editing turns the walk off; leaving the field alone turns it back on.</summary>
    public void SetAllowed(bool allowed)
    {
        if (_allowed == allowed) return;
        _allowed = allowed;
        Refresh();
    }

    /// <summary>Starts, stops or re-paces the walk to match what the field now holds.</summary>
    public void Refresh()
    {
        if (!CanWalk)
        {
            Stop();
            return;
        }
        var tail = HiddenTail();
        // Mid-layout the field answers zero for a moment. A name already walking
        // keeps walking through that: stopping on it is how the walk used to
        // disappear a second after it started, never to return.
        if (tail < SmallestTail && _walking > 0 && _field.ViewportWidth <= 0) return;
        if (tail < SmallestTail)
        {
            Stop();
            return;
        }
        if (Math.Abs(tail - _walking) < SmallestTail) return;
        Walk(tail);
    }

    /// <summary>
    /// What the field cannot show, in pixels. Once the field has been laid out
    /// it answers this itself, and its answer is the one the eye agrees with:
    /// the room inside a TextBox is not simply its width less padding and
    /// border. Before that first pass there is nothing to ask, so the name is
    /// laid out in the field's own typeface instead and compared with the room
    /// there appears to be - a guess that only has to last until the field can
    /// speak for itself.
    /// </summary>
    private double HiddenTail()
    {
        if (string.IsNullOrEmpty(_field.Text)) return 0;
        var room = _field.ActualWidth
                   - _field.Padding.Left - _field.Padding.Right
                   - _field.BorderThickness.Left - _field.BorderThickness.Right;
        if (room <= 0) return 0;
        var name = new FormattedText(
            _field.Text,
            CultureInfo.CurrentCulture,
            _field.FlowDirection,
            new Typeface(_field.FontFamily, _field.FontStyle, _field.FontWeight, _field.FontStretch),
            _field.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(_field).PixelsPerDip);
        return HiddenTail(_field.ExtentWidth, _field.ViewportWidth, name.WidthIncludingTrailingWhitespace, room);
    }

    /// <summary>The rule itself: the field when it can answer, the text when it cannot.</summary>
    internal static double HiddenTail(double extentWidth, double viewportWidth, double textWidth, double room)
        => Math.Max(0, viewportWidth > 0 ? extentWidth - viewportWidth : textWidth - room);

    private void Walk(double tail)
    {
        // A hidden field can retain its last native scroll position. Every
        // visible walk starts with the same readable rest at the first letter.
        _field.ScrollToHorizontalOffset(0);
        var schedule = MarqueeSchedule.For(tail);
        var walk = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        var at = TimeSpan.Zero;
        walk.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(at)));
        at += schedule.Hold;
        walk.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(at)));
        at += schedule.Travel;
        walk.KeyFrames.Add(new LinearDoubleKeyFrame(tail, KeyTime.FromTimeSpan(at)));
        at += schedule.Hold;
        walk.KeyFrames.Add(new LinearDoubleKeyFrame(tail, KeyTime.FromTimeSpan(at)));
        at += schedule.Travel;
        walk.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(at)));
        _field.BeginAnimation(HorizontalOffsetProperty, walk);
        _walking = tail;
        if (!_told)
        {
            // Once per run: the number behind "the name does not move".
            _logger?.Info($"The name walks {tail:0.0} px, {schedule.Travel.TotalSeconds:0.0} s each way " +
                          $"(field {_field.ActualWidth:0.0}, showing {_field.ViewportWidth:0.0}, text {_field.ExtentWidth:0.0}).");
            _told = true;
        }
    }

    private void Stop()
    {
        if (_walking <= 0) return;
        _field.BeginAnimation(HorizontalOffsetProperty, null);
        _field.SetValue(HorizontalOffsetProperty, 0d);
        if (_field.IsVisible && _window is { WindowState: not WindowState.Minimized })
            _field.ScrollToHorizontalOffset(0);
        _walking = 0;
    }

    private static void OnHorizontalOffsetChanged(DependencyObject target, DependencyPropertyChangedEventArgs change)
    {
        // Removing a clock can deliver one final property notification. Do not
        // turn that, or an already queued tick, into work for a hidden field.
        if (target is TextBox { IsLoaded: true, IsVisible: true } field &&
            Window.GetWindow(field) is { WindowState: not WindowState.Minimized } &&
            change.NewValue is double offset)
            field.ScrollToHorizontalOffset(offset);
    }
}
