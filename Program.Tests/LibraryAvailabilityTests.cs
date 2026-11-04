using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Minecraft.Tests;

public sealed class LibraryAvailabilityTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PreparationAndRunningGameLeaveCatalogueAndDetailsScrollable(bool preparing, bool running)
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var controls = WindowCanvasTests.LoadWindow();
            controls.WindowStartupLocation = WindowStartupLocation.Manual;
            controls.Left = -32000;
            controls.Top = -32000;
            controls.ShowActivated = false;
            controls.ShowInTaskbar = false;
            controls.Show();
            try
            {
                var root = (Grid)controls.FindName("RootGrid");
                var library = (PackLibraryView)controls.FindName("LibraryView");
                var packs = Enumerable.Range(0, 24).Select(Pack).ToArray();
                library.SetCatalog(packs);
                library.Visibility = Visibility.Visible;

                // Exercise the production availability refresh with real compiled
                // library controls, without starting Steam, networking or a game.
                var owner = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
                void Set(string name, object value) => typeof(MainWindow).GetField(name,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(owner, value);
                foreach (var field in typeof(MainWindow).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    if (typeof(FrameworkElement).IsAssignableFrom(field.FieldType) && controls.FindName(field.Name) is FrameworkElement element)
                        field.SetValue(owner, element);
                Set("_settings", new AppSettings { PlayerName = "Library Reader" });
                Set("_builds", new ObservableCollection<ClientBuildViewModel>());
                Set("_worlds", new ObservableCollection<WorldViewModel>());
                Set("_peers", new ObservableCollection<PeerViewModel>());
                Set("_launcherVisibility", new Dictionary<UIElement, Visibility>());
                Set("_launcherDataRefresh", Task.CompletedTask);
                Set("_libraryVisible", true);
                Set("_minecraftPreparing", preparing);
                Set("_minecraftRunning", running);
                var deliberatelyHidden = new Grid { Visibility = Visibility.Hidden };
                root.Children.Add(deliberatelyHidden);
                var refresh = typeof(MainWindow).GetMethod("RefreshUi", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var append = typeof(MainWindow).GetMethod("AppendLog", BindingFlags.Instance | BindingFlags.NonPublic)!;
                // Real startup/download/game logging fills an intentionally
                // collapsed service grid. Hidden would bring that text back
                // into the root Auto row's measure while opening the library.
                for (var line = 0; line < 80; line++) append.Invoke(owner, ["A retained diagnostic line " + line]);
                void Layout() => WindowCanvasTests.LayoutWindow(controls, new Size(804, 521));
                void Refresh() { refresh.Invoke(owner, null); Layout(); }
                Refresh();
                AssertFiniteRoot(root);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)((TextBox)controls.FindName("LogTextBox")).Parent).Visibility);
                var play = (Button)controls.FindName("PlayButton");
                Assert.Equal(!running, play.IsEnabled);
                Assert.Equal(!preparing, play.IsHitTestVisible);

                var catalogue = (ScrollViewer)library.FindName("CatalogScroll");
                AssertScrollable(catalogue, root, Layout);
                var offset = catalogue.VerticalOffset;
                Refresh();
                Assert.Equal(offset, catalogue.VerticalOffset, 3);

                // Enter a card through its actual routed Click handler while the
                // membership action is blocked by the in-progress launch/game.
                catalogue.ScrollToTop(); Layout();
                var cards = (ItemsControl)library.FindName("CatalogCards");
                var card = Descendants<Button>(cards).First();
                Assert.True(card.IsEnabled && card.IsHitTestVisible);
                card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, card));
                Layout();
                var details = (ScrollViewer)library.FindName("DetailScroll");
                Assert.Equal(Visibility.Visible, details.Visibility);
                Assert.False(((Button)library.FindName("MembershipButton")).IsEnabled);
                AssertScrollable(details, root, Layout);
                offset = details.VerticalOffset;
                Refresh();
                Assert.Equal(offset, details.VerticalOffset, 3);
                // New diagnostics must not grow the library or move its thumb
                // out of the finite native client after another UI refresh.
                append.Invoke(owner, ["Another diagnostic while the library is open"]);
                Refresh();
                AssertFiniteRoot(root);
                AssertScrollable(details, root, Layout);
                typeof(MainWindow).GetMethod("ShowLibrary", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(owner, [false]);
                Layout();
                Assert.Equal(Visibility.Collapsed, library.Visibility);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)((TextBox)controls.FindName("LogTextBox")).Parent).Visibility);
                Assert.Equal(Visibility.Hidden, deliberatelyHidden.Visibility);
                Assert.Equal(Visibility.Visible, ((Grid)controls.FindName("PlayArea")).Visibility);
                return true;
            }
            finally { controls.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LibraryScrollbarIsAutomaticWhenItsContentDoesNotOverflow(bool details)
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var controls = WindowCanvasTests.LoadWindow();
            controls.WindowStartupLocation = WindowStartupLocation.Manual;
            controls.Left = -32000;
            controls.Top = -32000;
            controls.ShowActivated = false;
            controls.ShowInTaskbar = false;
            controls.Show();
            try
            {
                var root = (Grid)controls.FindName("RootGrid");
                var library = (PackLibraryView)controls.FindName("LibraryView");
                foreach (UIElement child in root.Children)
                    if (child != library) child.Visibility = Visibility.Hidden;
                library.Visibility = Visibility.Visible;
                library.SetCatalog([]);
                library.CanChangeMembership = false;
                if (details)
                {
                    var pack = Pack(0);
                    pack.Content.Description = "Short description.";
                    library.SetCatalog([pack]);
                    library.ShowDetails(pack);
                }
                WindowCanvasTests.LayoutWindow(controls, new Size(804, 1001));
                var viewer = (ScrollViewer)library.FindName(details ? "DetailScroll" : "CatalogScroll");
                Assert.Equal(0, viewer.ScrollableHeight);
                Assert.Equal(ScrollBarVisibility.Auto, viewer.VerticalScrollBarVisibility);
                Assert.Equal(Visibility.Collapsed, viewer.ComputedVerticalScrollBarVisibility);
                var bar = Assert.IsType<ScrollBar>(viewer.Template.FindName("PART_VerticalScrollBar", viewer));
                Assert.False(bar.IsVisible);
                Assert.Equal(Visibility.Collapsed, bar.Visibility);
                return true;
            }
            finally { controls.Close(); }
        });
    }

    private static void AssertScrollable(ScrollViewer viewer, Grid root, Action layout)
    {
        Assert.True(viewer.IsEnabled && viewer.IsHitTestVisible);
        Assert.Equal(ScrollBarVisibility.Auto, viewer.VerticalScrollBarVisibility);
        Assert.True(viewer.ScrollableHeight > 100);
        Assert.Equal(Visibility.Visible, viewer.ComputedVerticalScrollBarVisibility);
        var scrollbar = Assert.IsType<ScrollBar>(viewer.Template.FindName("PART_VerticalScrollBar", viewer));
        var thumb = Descendants<Thumb>(scrollbar).Single();
        Assert.True(scrollbar.IsVisible && thumb.IsVisible);
        Assert.True(scrollbar.IsEnabled && thumb.IsEnabled && thumb.IsHitTestVisible);
        var point = thumb.TranslatePoint(new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2), root);
        if (System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(root) is { } clip)
            Assert.True(clip.FillContains(point), "The real root layout clip hides the library thumb.");
        var bounds = scrollbar.TransformToAncestor(root).TransformBounds(new Rect(scrollbar.RenderSize));
        Assert.InRange(bounds.Right, 0, root.ActualWidth + root.Margin.Right + 0.5);
        Assert.InRange(bounds.Bottom, 0, root.ActualHeight + 0.5);
        var hit = Assert.IsAssignableFrom<DependencyObject>(root.InputHitTest(point));
        Assert.True(IsDescendantOf(hit, thumb), "The thumb is covered by another surface.");
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        scrollbar.RaiseEvent(wheel);
        Assert.True(wheel.Handled);
        var smooth = SmoothScrollingController.Attach(viewer);
        for (var frame = 0; frame < 70; frame++) { smooth.AdvanceFrame(1000d / 60); layout(); }
        Assert.True(viewer.VerticalOffset > 0);
        var before = viewer.VerticalOffset;
        Assert.True(ScrollBar.PageDownCommand.CanExecute(null, scrollbar));
        ScrollBar.PageDownCommand.Execute(null, scrollbar);
        layout();
        Assert.True(viewer.VerticalOffset > before);
    }

    private static void AssertFiniteRoot(Grid root)
    {
        // A shown HWND may re-arrange after the manual layout pass. Compare
        // with its actual parent allocation, not a stale requested test size.
        var slot = LayoutInformation.GetLayoutSlot(root);
        Assert.InRange(root.ActualHeight, 0, slot.Height - root.Margin.Top - root.Margin.Bottom + 0.5);
    }

    private static bool IsDescendantOf(DependencyObject item, DependencyObject ancestor)
    {
        for (DependencyObject? current = item; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static CatalogPack Pack(int index)
    {
        var pack = new CatalogPack
        {
            Entry = new CatalogSource("pack" + index, "Pack " + index, new PackSyncSource("author", "pack", "pack-latest")),
            Content = new CatalogDescription
            {
                Name = "Pack " + index, MinecraftVersion = "1.20.1", Summary = "A library card available during preparation and gameplay.",
                Description = string.Join(" ", Enumerable.Repeat("A long description keeps the card detail independently scrollable.", 180))
            },
            Preview = new BitmapImage()
        };
        pack.BuildTree();
        return pack;
    }
}
