using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Minecraft.Tests;

public sealed class BackdropSurfaceBorderTests
{
    [Theory]
    [InlineData(1d)]
    [InlineData(1.5d)]
    [InlineData(2d)]
    public void PreparedFillMatchesTheNativePhotoAndRoundedSurfaceAtEveryRenderDpi(double renderScale)
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            foreach (var complexStroke in new[] { false, true })
            {
                const double width = 400, height = 280;
                var dpi = new DpiScale(renderScale, renderScale);
                // Materialize at the renderer's actual DPI. Exporting a live 200%
                // window at a different DPI would add an unrelated second resample
                // after the tint's 8-bit blend and obscure the paint comparison.
                var picture = PreparedWindowBackdrop.Rasterize(Photograph(), (int)(width * renderScale),
                    (int)(height * renderScale), dpi);
                var photograph = new ImageBrush(picture) { TileMode = TileMode.Tile, Stretch = Stretch.Fill };
                photograph.Freeze();
                var fill = Frozen(Color.FromArgb(51, 0, 0, 0));
                var liveChild = new Button { Content = "Живой контрол", Margin = new Thickness(16) };
                var surface = new BackdropSurfaceBorder
                {
                    Width = 180, Height = 112, Background = fill,
                    BorderBrush = complexStroke ? Frozen(Color.FromArgb(97, 211, 83, 149)) : Brushes.Black,
                    BorderThickness = complexStroke ? new Thickness(1, 2, 3, 4) : new Thickness(1),
                    CornerRadius = new CornerRadius(8), Child = liveChild
                };
                Canvas.SetLeft(surface, 24); Canvas.SetTop(surface, 32);
                var canvas = new Canvas();
                canvas.Children.Add(surface);
                var root = new Grid
                {
                    Width = width, Height = height, Background = photograph,
                    UseLayoutRounding = true, SnapsToDevicePixels = true
                };
                root.Children.Add(canvas);
                VisualTreeHelper.SetRootDpi(root, dpi);
                root.Measure(new Size(width, height));
                root.Arrange(new Rect(0, 0, width, height));
                root.UpdateLayout();
                var expected = Capture(root, renderScale);
                Assert.DoesNotContain(GeometryDrawings(VisualTreeHelper.GetDrawing(surface)),
                    geometry => geometry.Brush is ImageBrush);
                var tint = BackdropSurfaceCache.Materialize(picture, fill);
                surface.IsBackdropSurface = true;
                surface.UsePrepared(new BackdropSurfaceBorder.BitmapSourceAndViewport(tint,
                    new Rect(-24, -32, width, height)));
                // A detached control deliberately avoids normal visible-window
                // invalidation. Force this paint oracle to consume its prepared
                // state rather than accidentally comparing two native drawings.
                surface.InvalidateVisual();
                root.Measure(new Size(width, height));
                root.Arrange(new Rect(0, 0, width, height));
                root.UpdateLayout();
                var actual = Capture(root, renderScale);
                Assert.Contains(GeometryDrawings(VisualTreeHelper.GetDrawing(surface)),
                    geometry => geometry.Brush is ImageBrush image && ReferenceEquals(image.ImageSource, tint));
                AssertNativeSurfacePixels(expected, actual, surface, root, renderScale);
                AssertExactInterior(expected, actual, surface, root, renderScale);
                Assert.Same(fill, surface.Background);
                Assert.Same(liveChild, surface.Child);
                Assert.True(liveChild.IsEnabled);
            }
            if (renderScale == 2)
            {
                // Keep a real HWND integration comparison as well as the pure
                // renderer matrix, always at that window's native DPI.
                using var fixture = new SurfaceFixture();
                fixture.Show();
                var nativeDpi = VisualTreeHelper.GetDpi(fixture.Window).DpiScaleX;
                var nativeExpected = Capture(fixture.Window, nativeDpi);
                fixture.Surface.IsBackdropSurface = true;
                fixture.WaitForSurface();
                var nativeActual = Capture(fixture.Window, nativeDpi);
                AssertNativeSurfacePixels(nativeExpected, nativeActual, fixture.Surface, fixture.Window, nativeDpi);
                AssertExactInterior(nativeExpected, nativeActual, fixture.Surface, fixture.Window, nativeDpi);
                Assert.Empty(fixture.Failures);
            }
            return true;
        });
    }

    [Fact]
    public void MovingTheSurfaceKeepsItsPhotographAnchoredToTheWindow()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            using var fixture = new SurfaceFixture();
            fixture.Surface.IsBackdropSurface = true;
            fixture.Show();
            fixture.WaitForSurface();
            var dpi = VisualTreeHelper.GetDpi(fixture.Window).DpiScaleX;
            var initial = Capture(fixture.Window, dpi);
            var initialPoint = fixture.Surface.TranslatePoint(new Point(90, 8), fixture.Window);
            var initialPixel = Pixel(initial, initialPoint, dpi);

            Canvas.SetLeft(fixture.Surface, 164);
            Canvas.SetTop(fixture.Surface, 56);
            fixture.Window.UpdateLayout();
            Pump(40);
            Assert.True(fixture.Surface.IsBackdropPrepared);
            var moved = Capture(fixture.Window, dpi);
            var movedPoint = fixture.Surface.TranslatePoint(new Point(90, 8), fixture.Window);
            Assert.False(initialPixel.SequenceEqual(Pixel(moved, movedPoint, dpi)),
                "The moved card must reveal the photograph at its new window position.");

            fixture.Surface.IsBackdropSurface = false;
            var expected = Capture(fixture.Window, dpi);
            AssertMatchingPixels(expected, moved);
            AssertExactInterior(expected, moved, fixture.Surface, fixture.Window, dpi);

            fixture.Surface.IsBackdropSurface = true;
            fixture.WaitForSurface();
            _ = Capture(fixture.Window, dpi);
            Assert.Contains(GeometryDrawings(VisualTreeHelper.GetDrawing(fixture.Surface)),
                geometry => geometry.Brush is ImageBrush);
            var nativeDpi = VisualTreeHelper.GetDpi(fixture.Window);
            fixture.Surface.RenderTransform = new TranslateTransform(0.5 / nativeDpi.DpiScaleX, 0.5 / nativeDpi.DpiScaleY);
            fixture.Window.UpdateLayout();
            Pump(40);
            Assert.False(fixture.Surface.IsBackdropPrepared);
            var fractional = Capture(fixture.Window, dpi);
            Assert.DoesNotContain(GeometryDrawings(VisualTreeHelper.GetDrawing(fixture.Surface)),
                geometry => geometry.Brush is ImageBrush);
            fixture.Surface.IsBackdropSurface = false;
            var fractionalNative = Capture(fixture.Window, dpi);
            Assert.Equal(fractionalNative.Pixels, fractional.Pixels);

            var liveTransform = new TranslateTransform(1 / nativeDpi.DpiScaleX, 1 / nativeDpi.DpiScaleY);
            fixture.Surface.RenderTransform = liveTransform;
            fixture.Surface.IsBackdropSurface = true;
            fixture.Window.UpdateLayout();
            fixture.WaitForSurface();
            var restored = Capture(fixture.Window, dpi);
            Assert.Contains(GeometryDrawings(VisualTreeHelper.GetDrawing(fixture.Surface)),
                geometry => geometry.Brush is ImageBrush);
            fixture.Surface.IsBackdropSurface = false;
            var restoredNative = Capture(fixture.Window, dpi);
            AssertMatchingPixels(restoredNative, restored);
            AssertExactInterior(restoredNative, restored, fixture.Surface, fixture.Window, dpi);

            fixture.Surface.IsBackdropSurface = true;
            fixture.WaitForSurface();
            _ = Capture(fixture.Window, dpi);
            var preparations = fixture.Backdrop.SurfacePreparationCount;
            // Elastic motion mutates one retained transform rather than replacing
            // the RenderTransform DP. Verify the notification before a forced
            // UpdateLayout/Capture could hide a missing mutation subscription.
            liveTransform.X = 1.5 / nativeDpi.DpiScaleX;
            liveTransform.Y = 1.5 / nativeDpi.DpiScaleY;
            Pump(40);
            Assert.Same(liveTransform, fixture.Surface.RenderTransform);
            Assert.False(fixture.Surface.IsBackdropPrepared);
            var mutatedFractional = Capture(fixture.Window, dpi);
            Assert.DoesNotContain(GeometryDrawings(VisualTreeHelper.GetDrawing(fixture.Surface)),
                geometry => geometry.Brush is ImageBrush);
            fixture.Surface.IsBackdropSurface = false;
            var mutatedFractionalNative = Capture(fixture.Window, dpi);
            Assert.Equal(mutatedFractionalNative.Pixels, mutatedFractional.Pixels);

            fixture.Surface.IsBackdropSurface = true;
            liveTransform.X = 2 / nativeDpi.DpiScaleX;
            liveTransform.Y = 2 / nativeDpi.DpiScaleY;
            fixture.WaitForSurface();
            var mutatedRestored = Capture(fixture.Window, dpi);
            Assert.Same(liveTransform, fixture.Surface.RenderTransform);
            Assert.Contains(GeometryDrawings(VisualTreeHelper.GetDrawing(fixture.Surface)),
                geometry => geometry.Brush is ImageBrush);
            fixture.Surface.IsBackdropSurface = false;
            var mutatedRestoredNative = Capture(fixture.Window, dpi);
            AssertMatchingPixels(mutatedRestoredNative, mutatedRestored);
            AssertExactInterior(mutatedRestoredNative, mutatedRestored, fixture.Surface, fixture.Window, dpi);
            Assert.Equal(preparations, fixture.Backdrop.SurfacePreparationCount);
            Assert.Empty(fixture.Failures);
            return true;
        });
    }

    [Fact]
    public void ResourceReplacementAndMutableFillKeepTheLiveExpressionAndLatestColors()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            using var fixture = new SurfaceFixture();
            fixture.Window.Resources["SurfaceFixture"] = fixture.Fill;
            fixture.Surface.SetResourceReference(Border.BackgroundProperty, "SurfaceFixture");
            fixture.Surface.IsBackdropSurface = true;
            fixture.Show();
            fixture.WaitForSurface();
            Assert.True(DependencyPropertyHelper.GetValueSource(fixture.Surface, Border.BackgroundProperty).IsExpression);

            var replacement = new SolidColorBrush(Color.FromArgb(79, 13, 47, 91));
            fixture.Window.Resources["SurfaceFixture"] = replacement;
            Assert.Same(replacement, fixture.Surface.Background);
            Assert.False(fixture.Surface.IsBackdropPrepared);
            fixture.WaitForSurface();
            Assert.True(DependencyPropertyHelper.GetValueSource(fixture.Surface, Border.BackgroundProperty).IsExpression);
            AssertMatchesNativeFallback(fixture);

            replacement.Color = Color.FromArgb(111, 73, 23, 57);
            Assert.Same(replacement, fixture.Surface.Background);
            Assert.False(fixture.Surface.IsBackdropPrepared);
            fixture.WaitForSurface();
            Assert.True(DependencyPropertyHelper.GetValueSource(fixture.Surface, Border.BackgroundProperty).IsExpression);
            AssertMatchesNativeFallback(fixture);
            Assert.Empty(fixture.Failures);
            return true;
        });
    }

    [Fact]
    public void StyleDrivenHoverAndFocusFillsDoNotReuseThePreviousPreparedColor()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            using var fixture = new SurfaceFixture();
            var hover = Frozen(Color.FromArgb(89, 11, 37, 59));
            var focus = Frozen(Color.FromArgb(127, 71, 17, 41));
            var style = new Style(typeof(BackdropSurfaceBorder));
            style.Setters.Add(new Setter(Border.BackgroundProperty, fixture.Fill));
            // The trigger values represent the visual states without activating
            // a real offscreen window or sending input to the user's desktop.
            style.Triggers.Add(State("hover", hover));
            style.Triggers.Add(State("focus", focus));
            fixture.Surface.ClearValue(Border.BackgroundProperty);
            fixture.Surface.Style = style;
            fixture.Surface.IsBackdropSurface = true;
            fixture.Show();
            fixture.WaitForSurface();
            foreach (var state in new[] { (Name: "hover", Fill: hover), (Name: "focus", Fill: focus) })
            {
                fixture.Surface.Tag = state.Name;
                Assert.Same(state.Fill, fixture.Surface.Background);
                Assert.False(fixture.Surface.IsBackdropPrepared);
                Assert.Equal(BaseValueSource.StyleTrigger,
                    DependencyPropertyHelper.GetValueSource(fixture.Surface, Border.BackgroundProperty).BaseValueSource);
                AssertMatchesNativeFallback(fixture);
            }
            fixture.Surface.Tag = null;
            fixture.WaitForSurface();
            fixture.Surface.IsBackdropSurface = false;
            Assert.Same(fixture.Fill, fixture.Surface.Background);
            Assert.Equal(BaseValueSource.Style,
                DependencyPropertyHelper.GetValueSource(fixture.Surface, Border.BackgroundProperty).BaseValueSource);
            Assert.Empty(fixture.Failures);
            return true;
        });
    }

    [Fact]
    public void TransparentBackdropNeverUsesAnOpaquePreparedSurface()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            using var fixture = new SurfaceFixture();
            fixture.Window.Resources["BackdropFixture"] = Photograph();
            fixture.Window.SetResourceReference(Window.BackgroundProperty, "BackdropFixture");
            fixture.Surface.IsBackdropSurface = true;
            fixture.Show();
            fixture.WaitForSurface();
            Assert.True(DependencyPropertyHelper.GetValueSource(fixture.Window, Window.BackgroundProperty).IsExpression);

            var transparent = new DrawingBrush(new GeometryDrawing(Frozen(Color.FromArgb(128, 37, 73, 109)), null,
                new RectangleGeometry(new Rect(0, 0, 1024, 1024)))) { Stretch = Stretch.Fill };
            transparent.Freeze();
            fixture.Window.Resources["BackdropFixture"] = transparent;
            PumpUntil(() => fixture.Backdrop.IsReady && !fixture.Backdrop.IsSurfacePreparationPending,
                "The transparent backdrop did not settle.");

            var bitmap = Assert.IsAssignableFrom<BitmapSource>(Assert.IsType<ImageBrush>(fixture.Window.Background).ImageSource);
            Assert.Equal(PixelFormats.Pbgra32, bitmap.Format);
            Assert.False(fixture.Surface.IsBackdropPrepared);
            Assert.Same(fixture.Fill, fixture.Surface.Background);
            Assert.True(DependencyPropertyHelper.GetValueSource(fixture.Window, Window.BackgroundProperty).IsExpression);
            var dpi = VisualTreeHelper.GetDpi(fixture.Window).DpiScaleX;
            var actual = Capture(fixture.Window, dpi);
            Assert.DoesNotContain(GeometryDrawings(VisualTreeHelper.GetDrawing(fixture.Surface)),
                geometry => geometry.Brush is ImageBrush);
            fixture.Surface.IsBackdropSurface = false;
            var expected = Capture(fixture.Window, dpi);
            Assert.Equal(expected.Pixels, actual.Pixels);
            Assert.Empty(fixture.Failures);
            return true;
        });
    }

    [Fact]
    public void NestedPaintedParentsAndUnmarkedArtworkKeepTheirOriginalFills()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            using var fixture = new SurfaceFixture();
            fixture.Canvas.Children.Remove(fixture.Surface);
            var paintedParent = new Border { Width = 180, Height = 112, Background = Brushes.DarkRed, Child = fixture.Surface };
            fixture.Canvas.Children.Add(paintedParent);
            fixture.Surface.IsBackdropSurface = true;
            var heroFill = Frozen(Color.FromArgb(51, 0, 0, 0));
            var unmarkedHero = new BackdropSurfaceBorder { Width = 180, Height = 100, Background = heroFill };
            Canvas.SetLeft(unmarkedHero, 200);
            fixture.Canvas.Children.Add(unmarkedHero);
            fixture.Show();
            Pump(150);

            Assert.False(fixture.Surface.IsBackdropPrepared);
            Assert.Same(fixture.Fill, fixture.Surface.Background);
            Assert.False(unmarkedHero.IsBackdropPrepared);
            Assert.Same(heroFill, unmarkedHero.Background);
            Assert.Empty(fixture.Failures);
            return true;
        });
    }

    private static Trigger State(string name, Brush fill)
    {
        var trigger = new Trigger { Property = FrameworkElement.TagProperty, Value = name };
        trigger.Setters.Add(new Setter(Border.BackgroundProperty, fill));
        return trigger;
    }

    private static IEnumerable<GeometryDrawing> GeometryDrawings(Drawing? drawing)
    {
        if (drawing is GeometryDrawing geometry) yield return geometry;
        if (drawing is not DrawingGroup group) yield break;
        foreach (var child in group.Children)
            foreach (var descendant in GeometryDrawings(child)) yield return descendant;
    }

    private static void AssertMatchesNativeFallback(SurfaceFixture fixture)
    {
        var dpi = VisualTreeHelper.GetDpi(fixture.Window).DpiScaleX;
        var actual = Capture(fixture.Window, dpi);
        fixture.Surface.IsBackdropSurface = false;
        var expected = Capture(fixture.Window, dpi);
        AssertMatchingPixels(expected, actual);
        fixture.Surface.IsBackdropSurface = true;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed class SurfaceFixture : IDisposable
    {
        public readonly Canvas Canvas = new();
        public readonly SolidColorBrush Fill = Frozen(Color.FromArgb(51, 0, 0, 0));
        public readonly Button LiveChild = new() { Content = "Живой контрол", Margin = new Thickness(16) };
        public readonly BackdropSurfaceBorder Surface;
        public readonly Window Window;
        public readonly UiPreloadQueue Queue;
        public readonly PreparedWindowBackdrop Backdrop;
        public readonly List<Exception> Failures = [];

        public SurfaceFixture()
        {
            Surface = new BackdropSurfaceBorder
            {
                Width = 180, Height = 112, Background = Fill, BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = LiveChild
            };
            Canvas.SetLeft(Surface, 24); Canvas.SetTop(Surface, 32);
            Canvas.Children.Add(Surface);
            Window = new Window
            {
                Content = Canvas, Background = Photograph(), Width = 400, Height = 280,
                UseLayoutRounding = true, SnapsToDevicePixels = true,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
                ShowActivated = false, ShowInTaskbar = false
            };
            Queue = new UiPreloadQueue(Window.Dispatcher, Failures.Add);
            Backdrop = PreparedWindowBackdrop.Attach(Window, Queue);
        }

        public void Show()
        {
            Window.Show();
            ((HwndTarget)PresentationSource.FromVisual(Window)!.CompositionTarget).RenderMode = RenderMode.SoftwareOnly;
            PumpUntil(() => Backdrop.IsReady, "The fixture photograph did not prepare.");
        }

        public void WaitForSurface() => PumpUntil(() => Surface.IsBackdropPrepared,
            $"The current surface did not prepare. BackdropReady={Backdrop.IsReady}, error={Backdrop.LastError ?? "none"}.");

        public void Dispose()
        {
            Window.Close();
            Backdrop.Dispose();
            Queue.Dispose();
        }
    }

    private static DrawingBrush Photograph()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Frozen(Color.FromRgb(65, 95, 135)), null,
            new RectangleGeometry(new Rect(0, 0, 1024, 1024))));
        var colors = new[] { Color.FromRgb(195, 65, 45), Color.FromRgb(45, 175, 85), Color.FromRgb(65, 95, 215) };
        for (var index = 0; index < colors.Length; index++)
            drawing.Children.Add(new GeometryDrawing(Frozen(colors[index]), null,
                new RectangleGeometry(new Rect(index * 100, 0, 100, 1024))));
        var brush = new DrawingBrush(drawing)
        {
            Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
            ViewboxUnits = BrushMappingMode.Absolute, ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 1024, 1024), Viewport = new Rect(0, 0, 1024, 1024)
        };
        brush.Freeze();
        return brush;
    }

    private sealed record CaptureResult(int Width, int Height, byte[] Pixels);

    private static CaptureResult Capture(Window window, double scale)
        => Capture((FrameworkElement)window, scale);

    private static CaptureResult Capture(FrameworkElement window, double scale)
    {
        window.UpdateLayout();
        Pump(20);
        var width = (int)Math.Ceiling(window.ActualWidth * scale);
        var height = (int)Math.Ceiling(window.ActualHeight * scale);
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return new CaptureResult(width, height, pixels);
    }

    private static void AssertMatchingPixels(CaptureResult expected, CaptureResult actual)
    {
        Assert.Equal(expected.Width, actual.Width); Assert.Equal(expected.Height, actual.Height);
        for (var index = 0; index < expected.Pixels.Length; index++)
        {
            var difference = Math.Abs(expected.Pixels[index] - actual.Pixels[index]);
            Assert.True(difference <= (index % 4 == 3 ? 0 : 1),
                $"The surface changed pixel channel {index}: expected {expected.Pixels[index]}, actual {actual.Pixels[index]}.");
        }
    }

    private static void AssertNativeSurfacePixels(CaptureResult expected, CaptureResult actual,
        Border surface, FrameworkElement root, double scale)
    {
        Assert.Equal(expected.Width, actual.Width); Assert.Equal(expected.Height, actual.Height);
        var origin = surface.TranslatePoint(new Point(), root);
        var radius = surface.CornerRadius;
        var thickness = surface.BorderThickness;
        var corner = Math.Max(Math.Max(radius.TopLeft, radius.TopRight), Math.Max(radius.BottomLeft, radius.BottomRight));
        var stroke = Math.Max(Math.Max(thickness.Left, thickness.Right), Math.Max(thickness.Top, thickness.Bottom));
        // Only the native contour/perimeter may differ by a single RGB unit
        // because two equivalent alpha operations round in a different order.
        // The photo outside the shape, its fully covered interior and all alpha
        // channels remain exact; this is not a whole-window tolerance mask.
        var perimeter = corner + stroke + 2 / scale;
        var outer = new Rect(origin.X - 1 / scale, origin.Y - 1 / scale,
            surface.ActualWidth + 2 / scale, surface.ActualHeight + 2 / scale);
        var interior = new Rect(origin.X + perimeter, origin.Y + perimeter,
            Math.Max(0, surface.ActualWidth - 2 * perimeter), Math.Max(0, surface.ActualHeight - 2 * perimeter));
        for (var y = 0; y < expected.Height; y++)
        for (var x = 0; x < expected.Width; x++)
        {
            var point = new Point((x + 0.5) / scale, (y + 0.5) / scale);
            var atEdge = outer.Contains(point) && !interior.Contains(point);
            for (var channel = 0; channel < 4; channel++)
            {
                var index = (y * expected.Width + x) * 4 + channel;
                var difference = Math.Abs(expected.Pixels[index] - actual.Pixels[index]);
                var allowed = atEdge && channel != 3 ? 1 : 0;
                if (difference > allowed)
                    Assert.Fail($"The surface changed pixel ({x},{y}) channel {channel}: expected {expected.Pixels[index]}, " +
                        $"actual {actual.Pixels[index]}, perimeter={atEdge}.");
            }
        }
    }

    private static void AssertExactInterior(CaptureResult expected, CaptureResult actual,
        FrameworkElement surface, Window window, double scale)
        => AssertExactInterior(expected, actual, surface, (FrameworkElement)window, scale);

    private static void AssertExactInterior(CaptureResult expected, CaptureResult actual,
        FrameworkElement surface, FrameworkElement window, double scale)
    {
        var origin = surface.TranslatePoint(new Point(), window);
        var left = (int)Math.Ceiling((origin.X + 12) * scale);
        var top = (int)Math.Ceiling((origin.Y + 12) * scale);
        var right = Math.Min(expected.Width, (int)Math.Floor((origin.X + surface.ActualWidth - 12) * scale));
        var bottom = Math.Min(expected.Height, (int)Math.Floor((origin.Y + surface.ActualHeight - 12) * scale));
        for (var y = Math.Max(0, top); y < bottom; y++)
        for (var x = Math.Max(0, left); x < right; x++)
        for (var channel = 0; channel < 4; channel++)
        {
            var index = (y * expected.Width + x) * 4 + channel;
            Assert.Equal(expected.Pixels[index], actual.Pixels[index]);
        }
    }

    private static byte[] Pixel(CaptureResult capture, Point point, double scale)
    {
        var x = (int)Math.Floor(point.X * scale); var y = (int)Math.Floor(point.Y * scale);
        return capture.Pixels.AsSpan((y * capture.Width + x) * 4, 4).ToArray();
    }

    private static void PumpUntil(Func<bool> complete, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!complete() && watch.Elapsed.TotalSeconds < 8) Pump(10);
        Assert.True(complete(), message);
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
