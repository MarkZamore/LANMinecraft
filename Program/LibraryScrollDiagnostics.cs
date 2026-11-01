using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Minecraft;

/// <summary>Bounded numeric UI observations; never reads catalogue or player data.</summary>
internal static class LibraryScrollDiagnostics
{
    internal static string Capture(PackLibraryView library, ScrollViewer catalogue, ScrollViewer details, int reasons)
    {
        // Reasons: data=1, catalogue=2, details=4, visibility=8, loaded=16,
        // detail-loaded=32. Viewer order is catalogue/details. Bounds use window
        // coordinates; clip rectangles use their element's local coordinates.
        var window = Window.GetWindow(library);
        var root = window?.Content as FrameworkElement;
        var dpi = VisualTreeHelper.GetDpi(library);
        return "Library scroll snapshot " + JsonSerializer.Serialize(new
        {
            schema = 1, reasons, dpiX = dpi.DpiScaleX, dpiY = dpi.DpiScaleY,
            root = Element(root, window), library = Element(library, window),
            viewers = new[] { Viewer(catalogue), Viewer(details) }
        });

        object Viewer(ScrollViewer viewer)
        {
            var bar = Part<ScrollBar>(viewer, "PART_VerticalScrollBar");
            var track = bar is null ? null : Part<Track>(bar, "PART_Track");
            var clips = new List<object>();
            var depth = 0;
            for (DependencyObject? parent = viewer; parent is Visual && depth++ < 16;
                 parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is FrameworkElement element &&
                    (element.ClipToBounds || element.Clip is not null || LayoutInformation.GetLayoutClip(element) is not null))
                    clips.Add(Element(element, window)!);
            }
            return new
            {
                element = Element(viewer, window), viewer.CanContentScroll,
                viewer.ViewportWidth, viewer.ViewportHeight, viewer.ExtentWidth, viewer.ExtentHeight,
                viewer.ScrollableWidth, viewer.ScrollableHeight, viewer.HorizontalOffset, viewer.VerticalOffset,
                verticalPolicy = (int)viewer.VerticalScrollBarVisibility,
                computedVerticalVisibility = (int)viewer.ComputedVerticalScrollBarVisibility,
                bar = bar is null ? null : new
                {
                    element = Element(bar, window), bar.Minimum, bar.Maximum, bar.Value, bar.ViewportSize,
                    track = Element(track, window), thumb = Element(track?.Thumb, window)
                },
                presenter = Element(Part<FrameworkElement>(viewer, "PART_ScrollContentPresenter"), window),
                clips
            };
        }
    }

    private static T? Part<T>(Control control, string name) where T : FrameworkElement
    {
        try { return control.Template?.FindName(name, control) as T; }
        catch (InvalidOperationException) { return null; } // Template not applied yet; do not apply it here.
    }

    private static object? Element(FrameworkElement? element, Window? window)
    {
        if (element is null) return null;
        return new
        {
            element.ActualWidth, element.ActualHeight,
            visibility = (int)element.Visibility, element.IsVisible, element.IsEnabled, element.IsHitTestVisible,
            element.ClipToBounds, bounds = Bounds(element, window),
            clip = Rectangle(element.Clip?.Bounds),
            layoutClip = Rectangle(LayoutInformation.GetLayoutClip(element)?.Bounds)
        };
    }

    private static double[]? Bounds(FrameworkElement element, Window? window)
    {
        if (window is null) return null;
        try { return Rectangle(element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize))); }
        catch (InvalidOperationException) { return null; } // Disconnected during a visibility transition.
    }

    private static double[]? Rectangle(Rect? value) => value is { IsEmpty: false } rect &&
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) && double.IsFinite(rect.Width) && double.IsFinite(rect.Height)
        ? [Math.Round(rect.X, 3), Math.Round(rect.Y, 3), Math.Round(rect.Width, 3), Math.Round(rect.Height, 3)] : null;
}
