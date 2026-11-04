using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Minecraft.Tests;

public sealed class PreparedWindowBackdropTests
{
    [Fact]
    public void OpaqueSourcesUseLosslessBgr32AndTransparencyKeepsItsAlphaChannel()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var color = Color.FromRgb(17, 93, 211);
            var opaque = PreparedWindowBackdrop.Rasterize(new SolidColorBrush(color), 8, 8, new DpiScale(1, 1));
            Assert.Equal(PixelFormats.Bgr32, opaque.Format);
            var pixels = Pixels(opaque);
            for (var offset = 0; offset < pixels.Length; offset += 4)
            {
                Assert.Equal(color.B, pixels[offset]);
                Assert.Equal(color.G, pixels[offset + 1]);
                Assert.Equal(color.R, pixels[offset + 2]);
            }
            var transparent = PreparedWindowBackdrop.Rasterize(
                new SolidColorBrush(Color.FromArgb(127, color.R, color.G, color.B)), 8, 8, new DpiScale(1, 1));
            Assert.Equal(PixelFormats.Pbgra32, transparent.Format);
            var transparentPixels = Pixels(transparent);
            for (var offset = 3; offset < transparentPixels.Length; offset += 4)
                Assert.Equal(127, transparentPixels[offset]);
            return true;
        });
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(1.5d)]
    [InlineData(2d)]
    public void FrozenBackdropMatchesTheOriginalAtTheCurrentPhysicalClientSize(double scale)
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var original = Backdrop(Colors.DarkSlateBlue);
            var window = Host(original);
            var failures = new List<Exception>();
            using var queue = new UiPreloadQueue(window.Dispatcher, failures.Add);
            using var prepared = PreparedWindowBackdrop.Attach(window, queue);
            try
            {
                Assert.Same(prepared, PreparedWindowBackdrop.Attach(window, queue));
                Show(window);
                VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
                prepared.Refresh();
                PumpUntil(() => prepared.IsReady, "The current-size backdrop was not prepared.");
                var bitmap = Assert.IsAssignableFrom<BitmapSource>(Assert.IsType<ImageBrush>(window.Background).ImageSource);
                Assert.True(bitmap.IsFrozen);
                var handle = new WindowInteropHelper(window).Handle;
                Assert.True(GetClientRect(handle, out var client));
                Assert.Equal(client.Right - client.Left, bitmap.PixelWidth);
                Assert.Equal(client.Bottom - client.Top, bitmap.PixelHeight);
                Assert.Equal(bitmap.PixelWidth, prepared.TargetPixelWidth);
                Assert.Equal(bitmap.PixelHeight, prepared.TargetPixelHeight);
                var expected = PreparedWindowBackdrop.Rasterize(original, bitmap.PixelWidth, bitmap.PixelHeight,
                    VisualTreeHelper.GetDpi(window));
                Assert.Equal(Pixels(expected), Pixels(bitmap));
                var stamped = PreparedWindowBackdrop.Rasterize(window.Background, bitmap.PixelWidth, bitmap.PixelHeight,
                    VisualTreeHelper.GetDpi(window));
                Assert.Equal(Pixels(expected), Pixels(stamped));
                Assert.Empty(failures);
                return true;
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ResizeAndSourceChangesUseOneWorkerAndOnlyPublishTheLatestGeneration()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var window = Host(Backdrop(Colors.DarkSlateBlue));
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var renders = 0;
            var active = 0;
            var maximumActive = 0;
            var callerThread = Environment.CurrentManagedThreadId;
            var failures = new List<Exception>();
            using var queue = new UiPreloadQueue(window.Dispatcher, failures.Add);
            using var prepared = PreparedWindowBackdrop.Attach(window, queue, (brush, width, height, dpi) =>
            {
                Assert.True(brush.IsFrozen);
                Assert.NotEqual(callerThread, Environment.CurrentManagedThreadId);
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                var count = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, count);
                try
                {
                    if (Interlocked.Increment(ref renders) == 1)
                    {
                        entered.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture render was not released.");
                    }
                    return PreparedWindowBackdrop.Rasterize(brush, width, height, dpi);
                }
                finally { Interlocked.Decrement(ref active); }
            });
            try
            {
                Show(window);
                PumpUntil(() => entered.IsSet, "The initial worker did not start.");
                window.Width = 360; window.Height = 260; window.UpdateLayout(); prepared.Refresh();
                window.Width = 420; window.Height = 300; window.UpdateLayout();
                var latest = Backdrop(Colors.DarkMagenta);
                window.Background = latest;
                prepared.Refresh();
                Assert.Same(latest, window.Background);
                release.Set();
                PumpUntil(() => prepared.IsReady && !prepared.IsPreparing, "The latest request was not published.");
                Assert.Equal(2, prepared.PreparationCount);
                Assert.Equal(1, prepared.AppliedCount);
                Assert.Equal(1, maximumActive);
                var bitmap = Assert.IsAssignableFrom<BitmapSource>(Assert.IsType<ImageBrush>(window.Background).ImageSource);
                Assert.Equal(Pixels(PreparedWindowBackdrop.Rasterize(latest, bitmap.PixelWidth, bitmap.PixelHeight,
                    VisualTreeHelper.GetDpi(window))), Pixels(bitmap));
                prepared.Dispose();
                Assert.Same(latest, window.Background);
                Assert.Empty(failures);
                return true;
            }
            finally { release.Set(); window.Close(); }
        });
    }

    [Fact]
    public void ClosingWithAPendingWorkerCannotPublishOrLeaveQueueWorkBehind()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var original = Backdrop(Colors.DarkSlateBlue);
            var window = Host(original);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var completed = new ManualResetEventSlim();
            var failures = new List<Exception>();
            using var queue = new UiPreloadQueue(window.Dispatcher, failures.Add);
            using var prepared = PreparedWindowBackdrop.Attach(window, queue, (brush, width, height, dpi) =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture render was not released.");
                try { return PreparedWindowBackdrop.Rasterize(brush, width, height, dpi); }
                finally { completed.Set(); }
            });
            try
            {
                Show(window);
                PumpUntil(() => entered.IsSet, "The worker did not start before close.");
                window.Close();
                release.Set();
                PumpUntil(() => completed.IsSet, "The released fixture worker did not finish.");
                Pump(50);
                Assert.False(prepared.IsReady);
                Assert.False(prepared.IsPreparing);
                Assert.Empty(queue.PendingKeys);
                Assert.Same(original, window.Background);
                Assert.Empty(failures);
                return true;
            }
            finally { release.Set(); window.Close(); }
        });
    }

    [Fact]
    public void FailedPreparationKeepsTheOriginalWithoutSpinningAndCanRefreshAgain()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var original = Backdrop(Colors.DarkSlateBlue);
            var window = Host(original);
            var renders = 0;
            using var queue = new UiPreloadQueue(window.Dispatcher, error => throw error);
            using var prepared = PreparedWindowBackdrop.Attach(window, queue, (brush, width, height, dpi) =>
                Interlocked.Increment(ref renders) == 1
                    ? throw new InvalidOperationException("Fixture preparation failed.")
                    : PreparedWindowBackdrop.Rasterize(brush, width, height, dpi));
            try
            {
                Show(window);
                PumpUntil(() => prepared.LastError is not null, "The fixture failure did not settle.");
                Assert.Same(original, window.Background);
                Assert.False(prepared.IsReady);
                Pump(50);
                Assert.Equal(1, prepared.PreparationCount);
                prepared.Refresh();
                PumpUntil(() => prepared.IsReady, "An explicit refresh did not recover the backdrop.");
                Assert.Equal(2, prepared.PreparationCount);
                Assert.Null(prepared.LastError);
                return true;
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void AResourceChangeStillUpdatesTheBackdropAfterThePreparedBrushWasApplied()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            foreach (var fromStyle in new[] { false, true })
            {
                var window = Host(null);
                window.Resources["FixtureBackdrop"] = Backdrop(Colors.DarkSlateBlue);
                if (fromStyle)
                {
                    window.ClearValue(Window.BackgroundProperty);
                    window.Style = new Style(typeof(Window))
                    {
                        Setters = { new Setter(Window.BackgroundProperty, new DynamicResourceExtension("FixtureBackdrop")) }
                    };
                }
                else window.SetResourceReference(Window.BackgroundProperty, "FixtureBackdrop");
                var source = DependencyPropertyHelper.GetValueSource(window, Window.BackgroundProperty);
                if (!fromStyle) Assert.True(source.IsExpression);
                using var queue = new UiPreloadQueue(window.Dispatcher, error => throw error);
                using var prepared = PreparedWindowBackdrop.Attach(window, queue);
                try
                {
                    Show(window);
                    PumpUntil(() => prepared.IsReady, "The initial resource backdrop did not prepare.");
                    Assert.Equal(source.IsExpression, DependencyPropertyHelper.GetValueSource(window, Window.BackgroundProperty).IsExpression);
                    var latest = Backdrop(Colors.DarkMagenta);
                    window.Resources["FixtureBackdrop"] = latest;
                    PumpUntil(() => prepared.IsReady && prepared.AppliedCount == 2,
                        () => $"A resource change did not replace the prepared backdrop. Ready={prepared.IsReady}, " +
                            $"preparing={prepared.IsPreparing}, started={prepared.PreparationCount}, applied={prepared.AppliedCount}, " +
                            $"background={window.Background?.GetType().Name}, expression=" +
                            $"{DependencyPropertyHelper.GetValueSource(window, Window.BackgroundProperty).IsExpression}, " +
                            $"pending={string.Join(",", queue.PendingKeys)}, error={prepared.LastError ?? "none"}.");
                    Assert.Equal(source.IsExpression, DependencyPropertyHelper.GetValueSource(window, Window.BackgroundProperty).IsExpression);
                    Assert.Equal(source.BaseValueSource, DependencyPropertyHelper.GetValueSource(window, Window.BackgroundProperty).BaseValueSource);
                    var bitmap = Assert.IsAssignableFrom<BitmapSource>(Assert.IsType<ImageBrush>(window.Background).ImageSource);
                    Assert.Equal(Pixels(PreparedWindowBackdrop.Rasterize(latest, bitmap.PixelWidth, bitmap.PixelHeight,
                        VisualTreeHelper.GetDpi(window))), Pixels(bitmap));
                    prepared.Dispose();
                    Assert.Same(latest, window.Background);
                    var afterDispose = Backdrop(Colors.DarkGreen);
                    window.Resources["FixtureBackdrop"] = afterDispose;
                    PumpUntil(() => ReferenceEquals(afterDispose, window.Background),
                        "Disposing preparation detached the original live resource expression.");
                }
                finally { window.Close(); }
            }
            return true;
        });
    }

    [Fact]
    public void APlainSolidBackgroundDoesNotAllocateARasterCache()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var original = Brushes.DarkSlateBlue;
            var window = Host(original);
            using var queue = new UiPreloadQueue(window.Dispatcher, error => throw error);
            using var prepared = PreparedWindowBackdrop.Attach(window, queue);
            try
            {
                Show(window); Pump(50); prepared.Refresh(); Pump(50);
                Assert.Same(original, window.Background);
                Assert.Equal(0, prepared.PreparationCount);
                Assert.False(prepared.IsReady);
                Assert.Empty(queue.PendingKeys);
                return true;
            }
            finally { window.Close(); }
        });
    }

    private static DrawingBrush Backdrop(Color color)
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(color), null, new RectangleGeometry(new Rect(0, 0, 16, 9))));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(color.B, color.R, color.G)), null,
            new RectangleGeometry(new Rect(0, 0, 8, 9))));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(66, 0, 0, 0)), null,
            new RectangleGeometry(new Rect(0, 0, 16, 9))));
        var brush = new DrawingBrush(drawing) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        return brush;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static Window Host(Brush? background) => new()
    {
        Width = 320, Height = 240, Background = background, Content = new Border(),
        WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
        ShowActivated = false, ShowInTaskbar = false
    };

    private static void Show(Window window)
    {
        window.Show();
        ((HwndTarget)PresentationSource.FromVisual(window)!.CompositionTarget).RenderMode = RenderMode.SoftwareOnly;
    }

    private static void PumpUntil(Func<bool> complete, string message)
        => PumpUntil(complete, () => message);

    private static void PumpUntil(Func<bool> complete, Func<string> message)
    {
        var watch = Stopwatch.StartNew();
        while (!complete() && watch.Elapsed < TimeSpan.FromSeconds(8)) Pump(10);
        Assert.True(complete(), message());
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

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr handle, out ClientRectangle rectangle);
}
