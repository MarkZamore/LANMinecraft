using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
namespace Minecraft.Tests;
public sealed class UiPreloadTests
{
    [Fact]
    public void QueuePromotesDeduplicatesAndCancelsWithoutRunningDeferredWork()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var calls = new List<string>();
            using var queue = new UiPreloadQueue(Dispatcher.CurrentDispatcher, ex => throw ex);
            IEnumerable<Action> Steps(string value) { yield return () => calls.Add(value); }
            queue.Enqueue("later", 80, Steps("later"));
            queue.Enqueue("first", 10, Steps("first"));
            queue.Enqueue("later", 80, Steps("duplicate"));
            queue.Promote("later");
            Drain();
            Assert.Equal(new[] { "later", "first" }, calls);
            Assert.True(queue.IsComplete("later"));
            queue.Enqueue("cancelled", 0, Steps("cancelled"));
            queue.Dispose(); Drain();
            Assert.Equal(2, calls.Count);
            return true;
        });
    }
    [Fact]
    public void RetainedPagesKeepTheirScrollAndExpansionAcrossNavigation()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var window = WindowCanvasTests.LoadWindow();
            var view = (PackLibraryView)window.FindName("LibraryView");
            var root = Path.Combine(Path.GetTempPath(), "preload-" + Guid.NewGuid().ToString("N"));
            try
            {
                var paths = new AppPaths(root); paths.Ensure();
                var catalog = new PackCatalogService(paths, new Logger(paths.LogFile));
                var pack = catalog.Packs.First(p => p.Mods.Any(n => n.HasChildren));
                view.SetCatalog(catalog.Packs); view.ShowDetails(pack);
                view.Visibility = Visibility.Visible;
                WindowCanvasTests.LayoutWindow(window, new Size(804, 521));
                var scroll = (ScrollViewer)view.FindName("DetailScroll")!;
                scroll.ScrollToVerticalOffset(150); scroll.UpdateLayout();
                var offset = scroll.VerticalOffset;
                Assert.True(offset > 0);
                pack.Mods.First(n => n.HasChildren).IsExpanded = true;
                view.ShowCatalog(); view.ShowDetails(catalog.Packs.First(p => p != pack));
                view.ShowCatalog(); view.ShowDetails(pack);
                WindowCanvasTests.LayoutWindow(window, new Size(804, 521));
                Assert.Same(scroll, view.FindName("DetailScroll"));
                Assert.Equal(offset, scroll.VerticalOffset, 2);
                // A new decode of identical verified bytes must retain the same view.
                view.SetCatalog(new PackCatalogService(paths, new Logger(paths.LogFile)).Packs);
                Assert.Same(scroll, view.FindName("DetailScroll"));
                Assert.True(pack.Mods.First(n => n.HasChildren).IsExpanded);
                view.Dispose();
            }
            finally { TempTree.Delete(root); }
            return true;
        });
    }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
