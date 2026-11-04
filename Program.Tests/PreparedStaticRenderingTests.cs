using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Minecraft.Tests;

public sealed class PreparedStaticRenderingTests
{
    [Theory]
    [InlineData(48, 24, 180, 60, 1)]
    [InlineData(24, 48, 180, 60, 1.5)]
    [InlineData(480, 240, 120, 90, 2)]
    public void CroppedPreparationMatchesNativeImageAtTheCurrentViewportAndKeepsTheSource(
        int sourceWidth, int sourceHeight, int width, int height, double scale)
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var source = Picture(sourceWidth, sourceHeight, 255);
            var prepared = new PreparedImage { Source = source, Stretch = Stretch.UniformToFill };
            var native = new Image { Source = source, Stretch = Stretch.UniformToFill };
            var nativeHost = new Border { Width = width, Height = height, Child = native, ClipToBounds = true };
            var preparedHost = new Border { Width = width, Height = height, Child = prepared, ClipToBounds = true };
            var panel = new StackPanel(); panel.Children.Add(nativeHost); panel.Children.Add(preparedHost);
            var window = Host(panel);
            try
            {
                window.Show();
                ((HwndTarget)PresentationSource.FromVisual(window)!.CompositionTarget).RenderMode = RenderMode.SoftwareOnly;
                VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
                window.UpdateLayout();
                PumpUntil(() => !prepared.IsPreparationPending && Drawn(prepared) is { } image &&
                    image.PixelWidth == (int)Math.Ceiling(width * scale) && image.PixelHeight == (int)Math.Ceiling(height * scale));
                Assert.Equal(new Size(width, height), prepared.RenderSize);
                Assert.Same(source, prepared.Source);
                Assert.True(prepared.IsMeasureValid);
                Assert.True(prepared.IsArrangeValid);
                var cached = Assert.IsAssignableFrom<BitmapSource>(Drawn(prepared));
                Assert.Equal(PixelFormats.Bgr32, cached.Format);
                var expected = Render(nativeHost, width, height, scale);
                var actual = Render(preparedHost, width, height, scale);
                Assert.Equal(expected, actual);
                return true;
            }
            finally { window.Close(); SynchronizationContext.SetSynchronizationContext(previousContext); }
        });
    }

    [Fact]
    public void ATransparentPreviewKeepsItsPremultipliedAlphaAndLatestSource()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var image = new PreparedImage { Width = 180, Height = 60, Stretch = Stretch.UniformToFill, Source = Picture(48, 24, 255) };
            var window = Host(image);
            try
            {
                window.Show();
                var latest = Picture(24, 48, 127);
                image.Source = latest;
                window.UpdateLayout();
                PumpUntil(() => !image.IsPreparationPending && Drawn(image) is { Format: var format } && format == PixelFormats.Pbgra32);
                Assert.Same(latest, image.Source);
                var bitmap = Assert.IsAssignableFrom<BitmapSource>(Drawn(image));
                var pixels = Pixels(bitmap);
                for (var offset = 3; offset < pixels.Length; offset += 4) Assert.Equal(127, pixels[offset]);
                return true;
            }
            finally { window.Close(); SynchronizationContext.SetSynchronizationContext(previousContext); }
        });
    }

    [Fact]
    public void ImageWorkersSerializeRasterWorkSkipObsoleteVariantsAndPrioritizeVisibleRequests()
    {
        var fallback = Picture(2, 2, 255);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var order = new List<string>();
        var first = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var background = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var visible = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var obsolete = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var obsoleteRan = false;
        PreparedImageWorkQueue.Enqueue(fallback, first, () => true, () =>
        {
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            Assert.Equal(ThreadPriority.BelowNormal, Thread.CurrentThread.Priority);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The fixture image worker was not released.");
            order.Add("first"); return fallback;
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            PreparedImageWorkQueue.Enqueue(fallback, background, () => true, () => { order.Add("background"); return fallback; });
            PreparedImageWorkQueue.Enqueue(fallback, obsolete, () => false, () => { obsoleteRan = true; return fallback; });
            PreparedImageWorkQueue.Enqueue(fallback, visible, () => true, () => { order.Add("visible"); return fallback; });
            PreparedImageWorkQueue.Promote(visible.Task);
            Assert.False(background.Task.IsCompleted);
            Assert.False(visible.Task.IsCompleted);
            release.Set();
            Assert.True(Task.WhenAll(first.Task, background.Task, visible.Task, obsolete.Task).Wait(TimeSpan.FromSeconds(8)));
            Assert.Equal(new[] { "first", "visible", "background" }, order);
            Assert.False(obsoleteRan);
            Assert.Same(fallback, obsolete.Task.Result);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void UniformRoundedClipsAreReusedUntilTheirGeometryChanges()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var child = new Border();
            var border = new RoundedClipBorder { Width = 240, Height = 180, Child = child,
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
            border.Measure(new Size(240, 180)); border.Arrange(new Rect(0, 0, 240, 180));
            var clip = Assert.IsType<RectangleGeometry>(child.Clip);
            Assert.True(clip.IsFrozen);
            border.InvalidateArrange(); border.Arrange(new Rect(0, 0, 240, 180));
            Assert.Same(clip, child.Clip);
            border.Padding = new Thickness(4);
            border.Measure(new Size(240, 180)); border.Arrange(new Rect(0, 0, 240, 180));
            Assert.NotSame(clip, child.Clip);
            border.CornerRadius = new CornerRadius(4, 8, 12, 16);
            Assert.IsType<StreamGeometry>(child.Clip);
            return true;
        });
    }

    private static BitmapSource Picture(int width, int height, byte alpha)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            pixels[offset] = (byte)(x * 211 / width);
            pixels[offset + 1] = (byte)(y * 197 / height);
            pixels[offset + 2] = 83; pixels[offset + 3] = alpha;
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        source.Freeze(); return source;
    }
    private static BitmapSource? Drawn(PreparedImage image) => VisualTreeHelper.GetDrawing(image)?.Children
        .Select(drawing => drawing is ImageDrawing picture ? picture.ImageSource : (drawing as GeometryDrawing)?.Brush is ImageBrush brush ? brush.ImageSource : null)
        .OfType<BitmapSource>().SingleOrDefault();
    private static byte[] Render(Visual visual, int width, int height, double scale)
    {
        // Give both controls the same pixel origin. Comparing their two distinct
        // positions in a native window would include different raster phases.
        var element = (FrameworkElement)visual;
        if (element.Parent is Panel parent) parent.Children.Remove(element);
        var root = new Grid { Width = width, Height = height, UseLayoutRounding = true, SnapsToDevicePixels = true };
        root.Children.Add(element);
        VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root); return Pixels(bitmap);
    }
    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
    }
    private static Window Host(UIElement content) => new()
    {
        Content = content, Width = 400, Height = 360, ShowActivated = false, ShowInTaskbar = false,
        UseLayoutRounding = true, SnapsToDevicePixels = true,
        WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000
    };
    private static void PumpUntil(Func<bool> complete)
    {
        var watch = Stopwatch.StartNew();
        while (!complete() && watch.Elapsed.TotalSeconds < 8)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        Assert.True(complete(), "The current viewport bitmap did not prepare.");
    }
}
