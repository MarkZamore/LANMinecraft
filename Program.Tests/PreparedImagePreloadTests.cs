using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Minecraft.Tests;

public sealed class PreparedImagePreloadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HiddenWorkerCompletion_KeepsLayoutValidAndPublishesPreparedBitmapWhenShown(bool attachedToCollapsedOwner)
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Window? window = null;
            try
            {
                var original = FrozenPreview();
                var image = SeededPreview();
                var owner = new Border { Child = image };
                window = Host(owner);
                window.Show();
                // WPF publishes ActualWidth from its native layout lifecycle,
                // rather than from a detached Arrange's deferred size change.
                PumpUntil(() => image.IsLoaded && image.IsVisible && image.ActualWidth == 240 && image.ActualHeight == 135,
                    "The empty preview did not establish its native viewport.");
                if (attachedToCollapsedOwner) owner.Visibility = Visibility.Collapsed;
                else
                {
                    // A cached page is newly constructed, not a disconnected
                    // formerly-loaded visual whose layout WPF has suspended.
                    image = SeededPreview();
                    image.Measure(new Size(240, 135));
                    image.Arrange(new Rect(0, 0, 240, 135));
                    window.UpdateLayout();
                }
                PumpUntil(() => !image.IsVisible && (attachedToCollapsedOwner || !image.IsLoaded),
                    "The preview did not become inactive before its source was assigned.");

                // Establish the hidden viewport before the catalog publishes
                // its frozen source. A detached Arrange alone need not dispatch
                // OnRenderSizeChanged; assigning Source at a known size starts
                // the real worker without depending on that deferred callback.
                image.Measure(new Size(240, 135));
                image.Arrange(new Rect(0, 0, 240, 135));
                Assert.Equal(240, image.ActualWidth);
                Assert.Equal(135, image.ActualHeight);
                image.Source = original;
                // Settle the initial source assignment. Only its later async
                // replacement would leave the invisible visual dirty again.
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    image.Measure(new Size(240, 135));
                    image.Arrange(new Rect(0, 0, 240, 135));
                    if (image.IsMeasureValid && image.IsArrangeValid) break;
                }
                Assert.True(image.IsMeasureValid);
                Assert.False(image.IsVisible);
                PumpUntil(() => WorkerFinished(image, original),
                    "The hidden preview resize did not finish within three seconds.");
                Assert.Same(original, image.Source);
                Assert.True(image.IsMeasureValid,
                    "A completed resize must not leave an invisible preview in WPF's global measure queue.");

                if (!attachedToCollapsedOwner) owner.Child = image;
                owner.Visibility = Visibility.Visible;
                PumpUntil(() => image.IsLoaded && image.IsVisible && image.IsMeasureValid && image.IsArrangeValid &&
                    DrawnBitmap(image) is BitmapSource bitmap && !ReferenceEquals(bitmap, original) &&
                    bitmap.PixelWidth == ExpectedPixels(image, original).Width &&
                    bitmap.PixelHeight == ExpectedPixels(image, original).Height,
                    "The visible preview did not draw its bitmap at the current viewport and DPI.");
                Assert.Same(original, image.Source);
                var prepared = Assert.IsAssignableFrom<BitmapSource>(DrawnBitmap(image));
                Assert.True(prepared.IsFrozen);
                Assert.InRange(prepared.PixelWidth, 1, original.PixelWidth - 1);
                Assert.InRange(prepared.PixelHeight, 1, original.PixelHeight - 1);
                return true;
            }
            finally
            {
                window?.Close();
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        });
    }

    private static BitmapSource FrozenPreview()
    {
        const int width = 1280, height = 720;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            new byte[width * height * 4], width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static LibraryPreviewImage SeededPreview() => new()
    {
        // An unfrozen seed supplies the real aspect ratio without starting
        // the background worker before the frozen catalog source arrives.
        Source = BitmapSource.Create(16, 9, 96, 96, PixelFormats.Bgra32, null, new byte[16 * 9 * 4], 16 * 4),
        Width = 240,
        Height = 135,
        Stretch = Stretch.UniformToFill
    };

    private static Window Host(UIElement child) => new()
    {
        Content = child,
        Width = 320,
        Height = 240,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = 160,
        Top = 120,
        ShowActivated = false,
        ShowInTaskbar = false
    };

    private static bool WorkerFinished(PreparedImage image, BitmapSource original)
    {
        // Synchronize with actual worker completion, including when running this
        // regression against the original implementation without a private cache.
        var cached = typeof(PreparedImage).GetField("prepared", BindingFlags.Instance | BindingFlags.NonPublic);
        return cached is not null ? cached.GetValue(image) is BitmapSource : !ReferenceEquals(image.Source, original);
    }

    private static BitmapSource? DrawnBitmap(PreparedImage image) => VisualTreeHelper.GetDrawing(image)?
        .Children.OfType<ImageDrawing>().Select(drawing => drawing.ImageSource).OfType<BitmapSource>().SingleOrDefault();

    private static (int Width, int Height) ExpectedPixels(PreparedImage image, BitmapSource original)
    {
        var dpi = VisualTreeHelper.GetDpi(image);
        var factor = Math.Max(image.ActualWidth * dpi.DpiScaleX / original.PixelWidth,
            image.ActualHeight * dpi.DpiScaleY / original.PixelHeight);
        return ((int)Math.Ceiling(original.PixelWidth * factor), (int)Math.Ceiling(original.PixelHeight * factor));
    }

    private static void PumpUntil(Func<bool> complete, string failure)
    {
        var frame = new DispatcherFrame();
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(10) };
        bool finished = false;
        Exception? error = null;
        EventHandler tick = (_, _) =>
        {
            try
            {
                finished = complete();
                if (finished || clock.Elapsed >= TimeSpan.FromSeconds(3)) frame.Continue = false;
            }
            catch (Exception caught) { error = caught; frame.Continue = false; }
        };
        timer.Tick += tick;
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); timer.Tick -= tick; }
        if (error is not null) throw new InvalidOperationException(failure, error);
        Assert.True(finished, failure);
    }
}
