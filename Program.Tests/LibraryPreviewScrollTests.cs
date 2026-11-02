using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Minecraft.Tests;

public sealed class LibraryPreviewScrollTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void SixPreparedPreviewsAllowTheLastCatalogRowToScrollIntoView(double scale)
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var window = WindowCanvasTests.LoadWindow();
            var library = (PackLibraryView)window.FindName("LibraryView");
            using var queue = new UiPreloadQueue(window.Dispatcher, error => throw error);
            try
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000; window.Top = -32000;
                window.ShowInTaskbar = false; window.ShowActivated = false;
                window.Show();
                ((HwndTarget)PresentationSource.FromVisual(window)!.CompositionTarget).RenderMode = RenderMode.SoftwareOnly;
                VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
                var original = Preview();
                var packs = Enumerable.Range(0, 6).Select(index => Pack(index, original)).ToArray();
                library.Preloader = queue;
                library.SetCatalog(packs);
                PumpUntil(() => queue.PendingKeys.Length == 0, "The hidden six-pack library did not finish preloading.");
                var root = (Grid)window.FindName("RootGrid");
                foreach (UIElement child in root.Children)
                    if (child != library && child is not ScrollBar) child.Visibility = Visibility.Collapsed;
                library.ShowCatalog();
                library.Visibility = Visibility.Visible;
                var viewer = (ScrollViewer)library.FindName("CatalogScroll")!;
                PumpUntil(() => viewer.ScrollableHeight > 0, "The second card row did not overflow the minimum window.");

                // Drive the actual render subscription. Manually advancing the
                // controller would miss an image keeping global layout dirty.
                var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120000)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                viewer.RaiseEvent(wheel);
                Assert.True(wheel.Handled);
                var scrolling = SmoothScrollingController.Attach(viewer);
                PumpUntil(() => viewer.VerticalOffset > 0 && !scrolling.IsAnimating &&
                    Math.Abs(viewer.ScrollableHeight - viewer.VerticalOffset) < 0.5,
                    "Wheel scrolling never reached the second row after the previews were prepared.");
                var cards = (ItemsControl)library.FindName("CatalogCards")!;
                var panel = Descendants<LibraryColumnsPanel>(cards).Single();
                Assert.Equal(6, panel.Children.Count);
                var bottom = panel.Children.Cast<FrameworkElement>()
                    .Select(card => card.TransformToAncestor(viewer).TransformBounds(new Rect(card.RenderSize)).Bottom).Max();
                Assert.InRange(bottom, viewer.ActualHeight - 0.5, viewer.ActualHeight + 0.5);
                Assert.All(Descendants<LibraryPreviewImage>(cards), image => Assert.Same(original, image.Source));
                return true;
            }
            finally
            {
                queue.Dispose(); library.Dispose(); window.Close();
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        });
    }

    private static CatalogPack Pack(int index, BitmapImage preview) => new()
    {
        Entry = new CatalogSource("pack-" + index, "Pack " + index, new PackSyncSource("author", "pack", "pack-latest")),
        Content = new CatalogDescription
        {
            Name = "Pack " + index, MinecraftVersion = "1.20.1",
            Summary = string.Join(" ", Enumerable.Repeat("The full card summary remains available below its preview.", 4)),
            Description = "A complete pack description."
        },
        Preview = preview
    };

    private static BitmapImage Preview()
    {
        const int width = 1280, height = 720;
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            new byte[width * height * 4], width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var bytes = new MemoryStream();
        encoder.Save(bytes);
        bytes.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = bytes;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void PumpUntil(Func<bool> complete, string message)
    {
        var frame = new DispatcherFrame();
        var watch = Stopwatch.StartNew();
        var finished = false;
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            finished = complete();
            if (finished || watch.Elapsed >= TimeSpan.FromSeconds(4)) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(finished, message);
    }
}
