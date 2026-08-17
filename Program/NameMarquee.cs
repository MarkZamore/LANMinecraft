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
    // Reading pace, in device-independent pixels per second, at the size the
    // canvas is drawn. Slow enough to read a name letter by letter; the whole
    // window is scaled by its Viewbox, so a larger window walks it faster in
    // real pixels, which is what the eye expects.
    private const double PixelsPerSecond = 30;
    private static readonly TimeSpan ShortestTravel = TimeSpan.FromSeconds(0.4);
    private static readonly TimeSpan RestAtEnd = TimeSpan.FromSeconds(1.2);

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
    private bool _allowed = true;
    private double _walking;

    public NameMarquee(TextBox field)
    {
        _field = field ?? throw new ArgumentNullException(nameof(field));
        _field.TextChanged += (_, _) => Refresh();
        _field.SizeChanged += (_, _) => Refresh();
        _field.Loaded += (_, _) => Refresh();
        // Until the field has been through a layout pass it cannot say how much
        // of the name it is showing, and the first name is put in before that.
        // This waits for the first pass that can answer, then stops listening.
        _field.LayoutUpdated += OnLayoutUpdated;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_field.ViewportWidth <= 0) return;
        _field.LayoutUpdated -= OnLayoutUpdated;
        Refresh();
    }

    /// <summary>True while the name is walking; false when it fits or editing is on.</summary>
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
        var tail = HiddenTail();
        if (!_allowed || tail < SmallestTail)
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
    }

    private void Stop()
    {
        if (_walking <= 0) return;
        _field.BeginAnimation(HorizontalOffsetProperty, null);
        _field.SetValue(HorizontalOffsetProperty, 0d);
        _field.ScrollToHorizontalOffset(0);
        _walking = 0;
    }

    private static void OnHorizontalOffsetChanged(DependencyObject target, DependencyPropertyChangedEventArgs change)
    {
        if (target is TextBox field && change.NewValue is double offset) field.ScrollToHorizontalOffset(offset);
    }
}
