using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Minecraft.Tests;

public sealed class LibraryOverviewPreparationTests
{
    [Fact]
    public void CollapsedOverviewWarmsAsOneBatchWithoutConstructingUnopenedDetailPages()
    {
        OnUi((window, view, catalog, queue) =>
        {
            var root = (FrameworkElement)window.Content;
            var originalSize = root.RenderSize;
            view.Visibility = Visibility.Collapsed;
            view.SetCatalog(catalog.Packs);
            Assert.False(view.IsOverviewReady);
            Assert.Contains("library-overview", queue.PendingKeys);
            Assert.DoesNotContain(queue.PendingKeys, key => key.StartsWith("detail:", StringComparison.Ordinal));
            Drain();

            Assert.True(view.IsOverviewReady);
            Assert.False(view.IsOverviewPreparing);
            Assert.Equal(0, DetailPageCount(view));
            Assert.Equal(originalSize, root.RenderSize);
            var cards = Assert.IsType<ItemsControl>(view.FindName("CatalogCards"));
            Assert.NotNull(cards.Parent);
            var panel = Descendants<LibraryColumnsPanel>(cards).Single();
            Assert.Equal(catalog.Packs, cards.Items.OfType<CatalogPack>().ToArray());
            Assert.Equal(cards.Items.Count, panel.Children.Count);
            Assert.Equal(catalog.Packs.Count, view.PreparedOverviewCardSizes.Count);
            Assert.All(view.PreparedOverviewCardSizes, size =>
            {
                Assert.True(size.Width > 0);
                Assert.True(size.Height > 0);
            });

            var requested = catalog.Packs[0];
            view.Visibility = Visibility.Visible;
            WindowCanvasTests.LayoutWindow(window, new Size(804, 521));
            Assert.All(cards.Items.OfType<CatalogPack>(), pack =>
            {
                var card = Assert.IsAssignableFrom<FrameworkElement>(cards.ItemContainerGenerator.ContainerFromItem(pack));
                Assert.True(card.ActualWidth > 0);
                Assert.True(card.ActualHeight > 0);
            });
            var headings = cards.Items.OfType<PackLibraryHeading>().Where(item => item.Text is not null).ToArray();
            Assert.NotEmpty(headings);
            Assert.All(headings, heading =>
            {
                var row = Assert.IsAssignableFrom<FrameworkElement>(cards.ItemContainerGenerator.ContainerFromItem(heading));
                var text = Assert.Single(Descendants<TextBlock>(row));
                Assert.Equal(heading.Text, text.Text);
                Assert.Equal(AutomationHeadingLevel.Level2, AutomationProperties.GetHeadingLevel(text));
                Assert.False(text.Focusable);
                Assert.False(text.IsHitTestVisible);
                Assert.Empty(Descendants<Button>(row));
                Assert.Equal(0, row.TranslatePoint(new Point(), panel).X, 3);
                Assert.Equal(panel.ActualWidth, row.ActualWidth, 3);
            });

            var requestedRow = Assert.IsAssignableFrom<FrameworkElement>(cards.ItemContainerGenerator.ContainerFromItem(requested));
            var requestedButton = Assert.Single(Descendants<Button>(requestedRow));
            requestedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, requestedButton));
            Assert.Equal(1, DetailPageCount(view));
            Assert.Contains("detail:" + requested.Entry.Id, queue.PendingKeys);
            Assert.DoesNotContain(queue.PendingKeys, key => key.StartsWith("detail:", StringComparison.Ordinal) && key != "detail:" + requested.Entry.Id);
            Drain();
            Assert.Equal(1, DetailPageCount(view));
        });
    }

    [Fact]
    public void RemovingAnOpenPackPrunesItsPageEvenWhileTheNextOverviewIsLoading()
    {
        OnUi((window, view, catalog, queue) =>
        {
            view.SetCatalog(catalog.Packs); Drain();
            var removed = catalog.Packs[0];
            view.ShowDetails(removed); Drain();
            Assert.Equal(1, DetailPageCount(view));
            var remaining = catalog.Packs.Where(pack => !ReferenceEquals(pack, removed)).ToArray();
            view.SetCatalog(remaining, isLoading: true);
            Assert.False(view.IsOverviewReady);
            Assert.Equal(0, DetailPageCount(view));
            Assert.Null(Assert.IsType<ContentControl>(view.FindName("DetailHost")).Content);
            Assert.Equal(Visibility.Visible, Assert.IsType<TextBlock>(view.FindName("CatalogLoadingText")).Visibility);
            Assert.DoesNotContain("detail:" + removed.Entry.Id, queue.PendingKeys);
            view.SetCatalog(remaining); Drain();
            Assert.True(view.IsOverviewReady);
            Assert.Equal(remaining, Assert.IsType<ItemsControl>(view.FindName("CatalogCards")).Items.OfType<CatalogPack>().ToArray());
        });
    }

    [Fact]
    public void FirstFixedCardKeepsItsLeftAnchorWhenMoreCardsAppear()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var panel = new LibraryColumnsPanel { FixedColumnWidth = 240, MinimumColumnWidth = 240, Gap = 16 };
            var first = new Border { Height = 44 };
            panel.Children.Add(first);
            Layout();
            Assert.Equal(0, first.TranslatePoint(new Point(), panel).X, 3);
            for (var index = 0; index < 5; index++) panel.Children.Add(new Border { Height = 44 });
            Layout();
            Assert.Equal(0, first.TranslatePoint(new Point(), panel).X, 3);
            Assert.All(panel.Children.Cast<FrameworkElement>(), card => Assert.Equal(240, card.ActualWidth, 3));

            var heading = new ContentPresenter { Content = new PackLibraryHeading("Next section"), Height = 20 };
            panel.Children.Insert(1, heading);
            Layout();
            var following = (FrameworkElement)panel.Children[2];
            Assert.Equal(1600, heading.ActualWidth, 3);
            Assert.Equal(first.ActualHeight + panel.Gap, heading.TranslatePoint(new Point(), panel).Y, 3);
            Assert.Equal(0, following.TranslatePoint(new Point(), panel).X, 3);
            Assert.Equal(first.ActualHeight + heading.ActualHeight + 2 * panel.Gap, following.TranslatePoint(new Point(), panel).Y, 3);
            return true;

            void Layout()
            {
                panel.Measure(new Size(1600, double.PositiveInfinity));
                panel.Arrange(new Rect(0, 0, 1600, panel.DesiredSize.Height));
                panel.UpdateLayout();
            }
        });
    }

    private static int DetailPageCount(PackLibraryView view) =>
        ((IDictionary)typeof(PackLibraryView).GetField("pages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!).Count;

    private static void OnUi(Action<Window, PackLibraryView, PackCatalogService, UiPreloadQueue> work)
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "overview-preparation-" + Guid.NewGuid().ToString("N"));
            var window = WindowCanvasTests.LoadWindow();
            var view = (PackLibraryView)window.FindName("LibraryView");
            using var queue = new UiPreloadQueue(window.Dispatcher, error => throw error);
            try
            {
                var paths = new AppPaths(directory); paths.Ensure();
                var catalog = new PackCatalogService(paths, new Logger(paths.LogFile));
                WindowCanvasTests.LayoutWindow(window, new Size(804, 521));
                view.Preloader = queue;
                work(window, view, catalog, queue);
                return true;
            }
            finally { view.Dispose(); window.Close(); TempTree.Delete(directory); }
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
