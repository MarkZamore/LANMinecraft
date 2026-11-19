using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Minecraft.Tests;

public static class SmoothScrollingTests
{
    [Xunit.Fact]
    public static void DelayedFramesKeepTheSameScrollAndSpringTiming()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            foreach (var bounce in new[] { false, true })
            {
                var delayed = SampleMotion(bounce, [150]);
                var regular = SampleMotion(bounce, Enumerable.Repeat(150d / 9, 9));
                Xunit.Assert.Equal(regular.Offset, delayed.Offset, 6);
                Xunit.Assert.Equal(regular.BounceOffset, delayed.BounceOffset, 6);
                Xunit.Assert.Equal(regular.IsAnimating, delayed.IsAnimating);
                if (SystemParameters.ClientAreaAnimation)
                {
                    if (bounce) Xunit.Assert.NotEqual(0, delayed.BounceOffset);
                    else Xunit.Assert.InRange(delayed.Offset, 25, SmoothScrollingController.WheelStep);
                }
            }
            return true;
        });
    }

    [Xunit.Fact]
    public static void VeryLongFramesAreCappedAtTheSuspendLimit()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            foreach (var bounce in new[] { false, true })
            {
                var suspended = SampleMotion(bounce, [10_000]);
                var bounded = SampleMotion(bounce, [250]);
                Xunit.Assert.Equal(bounded.Offset, suspended.Offset, 6);
                Xunit.Assert.Equal(bounded.BounceOffset, suspended.BounceOffset, 6);
                Xunit.Assert.Equal(bounded.IsAnimating, suspended.IsAnimating);
                Xunit.Assert.InRange(suspended.Offset, 0, SmoothScrollingController.WheelStep);
                Xunit.Assert.InRange(suspended.BounceOffset, -18, 18);
            }
            return true;
        });
    }

    private static (double Offset, double BounceOffset, bool IsAnimating) SampleMotion(bool bounce, IEnumerable<double> intervals)
    {
        var viewer = new ScrollViewer { Width = 400, Height = 300, Content = new Border { Height = 3200 } };
        viewer.Measure(new Size(400, 300));
        viewer.Arrange(new Rect(0, 0, 400, 300));
        viewer.UpdateLayout();
        using var controller = SmoothScrollingController.Attach(viewer);
        controller.ScrollByWheelDelta(bounce ? 120 : -120);
        foreach (var elapsed in intervals)
        {
            controller.AdvanceFrame(elapsed);
            viewer.UpdateLayout();
        }
        return (viewer.VerticalOffset, controller.BounceOffset, controller.IsAnimating);
    }

    [Xunit.Fact]
    public static void WheelAndSpring_AccumulateClampCancelAndSettle()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
        var viewer = new ScrollViewer { Width = 400, Height = 300, Content = new Border { Height = 3200 } };
        viewer.Measure(new Size(400, 300));
        viewer.Arrange(new Rect(0, 0, 400, 300));
        viewer.UpdateLayout();
        var controller = SmoothScrollingController.Attach(viewer);
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void Frames(int count) {
            for (int i = 0; i < count; i++) { controller.AdvanceFrame(1000d / 60); viewer.UpdateLayout(); }
        }
        bool animated = SystemParameters.ClientAreaAnimation;
        try {
            Check(ReferenceEquals(controller, SmoothScrollingController.Attach(viewer)), "Duplicate controller");
            Check(!controller.ScrollByWheelDelta(0), "Zero delta consumed");
            controller.ScrollByWheelDelta(-120);
            Check(controller.TargetOffset == 36, "Wheel step");
            Frames(1);
            if (animated) Check(viewer.VerticalOffset > 0 && viewer.VerticalOffset < 12, "First frame jumped");
            controller.ScrollByWheelDelta(-120);
            controller.ScrollByWheelDelta(-60);
            Check(controller.TargetOffset == 90, "Fast and partial wheel accumulation");
            Frames(80);
            Check(Math.Abs(viewer.VerticalOffset - 90) < .01 && !controller.IsAnimating, "Scroll did not settle");
            controller.ScrollByWheelDelta(120);
            Frames(1);
            if (animated) Check(viewer.VerticalOffset < 90 && viewer.VerticalOffset > 78, "Reverse jumped");
            Frames(80);
            Check(Math.Abs(viewer.VerticalOffset - 54) < .01, "Reverse target");
            controller.ScrollByWheelDelta(-120);
            viewer.ScrollToVerticalOffset(200); viewer.UpdateLayout();
            Check(!controller.IsAnimating, "Manual scrolling did not cancel");
            Frames(60); Check(viewer.VerticalOffset == 200, "Animation fought manual input");
            foreach (bool bottom in new[] { false, true }) {
                if (bottom) viewer.ScrollToBottom(); else viewer.ScrollToTop();
                viewer.UpdateLayout(); controller.Cancel();
                double edge = viewer.VerticalOffset;
                controller.ScrollByWheelDelta(bottom ? -120 : 120);
                Frames(1);
                if (animated) Check(bottom ? controller.BounceOffset < 0 : controller.BounceOffset > 0, "Wrong spring direction");
                Check(Math.Abs(controller.BounceOffset) <= 18 && viewer.VerticalOffset == edge, "Spring exceeded bounds");
                Frames(100);
                Check(controller.BounceOffset == 0 && !controller.IsAnimating, "Spring did not settle");
            }
            viewer.ScrollToTop(); viewer.UpdateLayout(); controller.Cancel();
            controller.ScrollByWheelDelta(-120000);
            Check(controller.TargetOffset == viewer.ScrollableHeight, "Target not clamped");
            // The edge impulse must also work when scrolling towards an edge.
            bool bounced = false;
            for (int i = 0; i < 160; i++) { Frames(1); bounced |= controller.BounceOffset < 0; }
            if (animated) Check(bounced, "Lost pending edge impulse");
            Check(viewer.VerticalOffset == viewer.ScrollableHeight && !controller.IsAnimating, "Edge motion did not settle");
            viewer.ScrollToTop(); viewer.UpdateLayout(); controller.Cancel();
            controller.ScrollByWheelDelta(-120); Frames(1);
            viewer.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Check(!controller.IsAnimating && controller.BounceOffset == 0, "Unloaded animation still running");
            controller.ScrollByWheelDelta(120); Frames(1);
            var presenter = (FrameworkElement)viewer.Template.FindName("PART_ScrollContentPresenter", viewer);
            controller.Dispose();
            Check(!controller.IsAnimating && controller.BounceOffset == 0, "Dispose left animation running");
            Check(!(presenter.RenderTransform is TransformGroup), "Dispose left spring transform");
            Check(!controller.ScrollByWheelDelta(-120), "Disposed controller consumed input");
        } finally { controller.Dispose(); }
        return true;
        });
    }
}
