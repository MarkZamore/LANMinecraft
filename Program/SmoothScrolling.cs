#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Minecraft;

// Follow one continuous target on render frames; wheel input never restarts easing.
public sealed class SmoothScrollingController : IDisposable
{
    // One controller per viewer; unloading stops the render subscription. The
    // viewer and its controller can then be collected together or loaded again.
    static readonly DependencyProperty ControllerProperty = DependencyProperty.RegisterAttached(
        "Controller", typeof(SmoothScrollingController), typeof(SmoothScrollingController), new PropertyMetadata(null));

    public static SmoothScrollingController Attach(ScrollViewer viewer)
    {
        var controller = (SmoothScrollingController)viewer.GetValue(ControllerProperty);
        if (controller == null) {
            controller = new SmoothScrollingController(viewer);
            viewer.SetValue(ControllerProperty, controller);
        }
        return controller;
    }

    public const double WheelStep = 36;
    public const double TimeConstantMilliseconds = 75;
    readonly ScrollViewer viewer;
    readonly List<double> requestedOffsets = new List<double>();
    bool disposed;
    long lastTimestamp;
    double position;
    double bounceVelocity;
    double pendingBounce;
    FrameworkElement bounceContent;
    Transform originalTransform;
    TransformGroup bounceGroup;
    readonly TranslateTransform bounceTransform = new TranslateTransform();
    private bool isScrollLocked;
    internal bool IsScrollLocked
    {
        get => isScrollLocked;
        set
        {
            if (value) Cancel();
            isScrollLocked = value;
        }
    }
    public double TargetOffset { get; private set; }
    public bool IsAnimating { get; private set; }
    public double BounceOffset { get { return bounceTransform.Y; } }

    public SmoothScrollingController(ScrollViewer scrollViewer)
    {
        ArgumentNullException.ThrowIfNull(scrollViewer);
        viewer = scrollViewer;
        position = TargetOffset = viewer.VerticalOffset;
        viewer.PreviewMouseWheel += OnWheel;
        viewer.PreviewMouseDown += OnManualMouse;
        viewer.PreviewKeyDown += OnManualKey;
        viewer.PreviewTouchDown += OnManualTouch;
        viewer.ScrollChanged += OnScrollChanged;
        viewer.Unloaded += OnUnloaded;
    }

    public bool ScrollByWheelDelta(int delta)
    {
        if (disposed || delta == 0) return false;
        if (IsScrollLocked) return true;
        if (viewer.ScrollableHeight <= 0) return false;
        double raw = (IsAnimating ? TargetOffset : viewer.VerticalOffset) - delta * WheelStep / 120.0;
        double target = Clamp(raw);
        if (!SystemParameters.ClientAreaAnimation) {
            Cancel(); TargetOffset = target; RequestOffset(target); return true;
        }
        if (raw != target) {
            double impulse = -(raw - target) * 8;
            bool atEdge = target == 0 ? viewer.VerticalOffset < 0.5 : viewer.VerticalOffset > viewer.ScrollableHeight - 0.5;
            if (atEdge) {
                EnsureBounceContent();
                bounceTransform.Y = Math.Max(-18, Math.Min(18, bounceTransform.Y + Math.Sign(impulse) * 1.5));
                bounceVelocity = Math.Max(-480, Math.Min(480, bounceVelocity + impulse));
            }
            else pendingBounce = Math.Max(-480, Math.Min(480, pendingBounce + impulse));
        } else if (target > 0 && target < viewer.ScrollableHeight) pendingBounce = 0;
        TargetOffset = target;
        if (!IsAnimating && (Math.Abs(target - viewer.VerticalOffset) >= 0.25 || Math.Abs(bounceVelocity) > 0.5)) {
            position = viewer.VerticalOffset;
            lastTimestamp = Stopwatch.GetTimestamp();
            IsAnimating = true;
            CompositionTarget.Rendering += OnRendering;
        }
        return true;
    }

    void OnRendering(object sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - lastTimestamp) * 1000.0 / Stopwatch.Frequency;
        lastTimestamp = now;
        AdvanceFrame(elapsed);
    }

    // The viewport can be verified headlessly at controlled frame intervals.
    internal void AdvanceFrame(double elapsedMilliseconds)
    {
        if (!IsAnimating || disposed || elapsedMilliseconds <= 0) return;
        double elapsed = Math.Min(elapsedMilliseconds, 50);
        position += (TargetOffset - position) * (1 - Math.Exp(-elapsed / TimeConstantMilliseconds));
        bool scrollSettled = Math.Abs(TargetOffset - position) < 0.25;
        if (scrollSettled) position = TargetOffset;
        if (Math.Abs(TargetOffset-position)<=4 && pendingBounce != 0) { bounceVelocity = Math.Max(-480, Math.Min(480, bounceVelocity + pendingBounce)); pendingBounce = 0; }
        // Exact damped spring step: k=300, damping=24. This is stable even if a
        // render frame is delayed, and gives a small elastic return at either edge.
        EnsureBounceContent();
        double dt = elapsed / 1000.0, frequency = Math.Sqrt(156), damping = Math.Exp(-12 * dt);
        double cosine = Math.Cos(frequency * dt), sine = Math.Sin(frequency * dt), previous = bounceTransform.Y;
        double bounce = damping * (previous * cosine + (bounceVelocity + 12 * previous) / frequency * sine);
        bounceVelocity = damping * (bounceVelocity * cosine - (12 * bounceVelocity + 300 * previous) / frequency * sine);
        bounceTransform.Y = Math.Max(-18, Math.Min(18, bounce));
        bool springSettled = Math.Abs(bounceTransform.Y) < 0.05 && Math.Abs(bounceVelocity) < 0.5;
        if (springSettled) { bounceTransform.Y = 0; bounceVelocity = 0; }
        if (scrollSettled && springSettled) StopRendering();
        RequestOffset(position);
    }

    void EnsureBounceContent()
    {
        if (bounceContent != null) return;
        viewer.ApplyTemplate();
        bounceContent = viewer.Template.FindName("PART_ScrollContentPresenter", viewer) as FrameworkElement;
        if (bounceContent == null) return;
        originalTransform = bounceContent.RenderTransform;
        bounceGroup = new TransformGroup();
        if (originalTransform != null) bounceGroup.Children.Add(originalTransform);
        bounceGroup.Children.Add(bounceTransform);
        bounceContent.RenderTransform = bounceGroup;
    }

    void StopRendering()
    {
        if (!IsAnimating) return;
        CompositionTarget.Rendering -= OnRendering;
        IsAnimating = false;
    }
    public void Cancel()
    {
        StopRendering(); requestedOffsets.Clear();
        bounceTransform.Y = 0; bounceVelocity = 0; pendingBounce = 0;
        position = TargetOffset = viewer.VerticalOffset;
    }
    double Clamp(double offset) { return Math.Max(0, Math.Min(viewer.ScrollableHeight, offset)); }
    void RequestOffset(double offset)
    {
        offset = Clamp(offset);
        requestedOffsets.Add(offset);
        if (requestedOffsets.Count > 64) requestedOffsets.RemoveAt(0);
        viewer.ScrollToVerticalOffset(offset);
    }
    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!e.Handled && !IsComboBoxSource(e.OriginalSource as DependencyObject) && ScrollByWheelDelta(e.Delta)) e.Handled = true;
    }
    bool IsComboBoxSource(DependencyObject source)
    {
        while (source != null && source != viewer) {
            if (source is ComboBox || source is ComboBoxItem) return true;
            DependencyObject parent = null;
            if (source is Visual || source is System.Windows.Media.Media3D.Visual3D) parent = VisualTreeHelper.GetParent(source);
            source = parent ?? LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
    void OnManualMouse(object sender, MouseButtonEventArgs e) { Cancel(); }
    void OnManualKey(object sender, KeyEventArgs e) { Cancel(); }
    void OnManualTouch(object sender, TouchEventArgs e) { Cancel(); }
    void OnUnloaded(object sender, RoutedEventArgs e) { Cancel(); }
    void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource != viewer) return;
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) { Cancel(); return; }
        if (e.VerticalChange == 0) return;
        for (int i = requestedOffsets.Count - 1; i >= 0; --i) {
            if (Math.Abs(requestedOffsets[i] - viewer.VerticalOffset) < 1) { requestedOffsets.RemoveRange(0, i + 1); return; }
        }
        if (IsAnimating) Cancel();
        else position = TargetOffset = viewer.VerticalOffset;
    }
    public void Dispose()
    {
        if (disposed) return;
        Cancel(); disposed = true;
        if (bounceContent != null && Object.ReferenceEquals(bounceContent.RenderTransform, bounceGroup)) bounceContent.RenderTransform = originalTransform;
        viewer.PreviewMouseWheel -= OnWheel;
        viewer.PreviewMouseDown -= OnManualMouse;
        viewer.PreviewKeyDown -= OnManualKey;
        viewer.PreviewTouchDown -= OnManualTouch;
        viewer.ScrollChanged -= OnScrollChanged;
        viewer.Unloaded -= OnUnloaded;
    }
}
