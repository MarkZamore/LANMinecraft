using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace Minecraft.Tests;

public sealed class LibraryTreeMotionTests
{
    [Fact]
    public void ExpandedAddonsAreMeasuredWithoutShowingANativeWindow()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var window = WindowCanvasTests.LoadWindow();
            var library = (PackLibraryView)window.FindName("LibraryView");
            var node = new CatalogModNode(new CatalogMod { Id = "parent", Name = "Parent" }) { IsExpanded = true };
            node.Children.Add(new CatalogModNode(new CatalogMod { Id = "child", Name = "Child" }));
            var item = new TreeViewItem
            {
                DataContext = node, Header = "Parent", Style = (Style)library.Resources["LibraryTreeItemStyle"]
            };
            item.Items.Add(new TreeViewItem { Header = "Child" });
            var tree = new TreeView();
            tree.Items.Add(item);
            window.Content = tree;
            tree.Measure(new Size(400, 400));
            tree.Arrange(new Rect(0, 0, 400, 400));
            tree.UpdateLayout();
            var reveal = Assert.IsType<Border>(item.Template.FindName("ChildrenReveal", item));
            Assert.False(item.IsLoaded);
            Assert.True(item.IsExpanded);
            Assert.Equal(Visibility.Visible, reveal.Visibility);
            Assert.True(reveal.ActualHeight > 0);
            Assert.True(double.IsNaN(reveal.Height));
            Assert.False(reveal.HasAnimatedProperties);
            return true;
        });
    }

    [Fact]
    public void AddonReveal_ReversesWithoutJumpAndRestoresAutomaticLayoutAfterNavigation()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var window = WindowCanvasTests.LoadWindow();
            var library = (PackLibraryView)window.FindName("LibraryView");
            var node = new CatalogModNode(new CatalogMod { Id = "create", Name = "Create" });
            for (var index = 0; index < 6; index++)
                node.Children.Add(new CatalogModNode(new CatalogMod { Id = "addon" + index, Name = "Addon " + index }));
            var style = (Style)library.Resources["LibraryTreeItemStyle"];
            var item = new TreeViewItem
            {
                DataContext = node, Header = node, ItemsSource = node.Children,
                Style = style, ItemContainerStyle = style
            };
            var tree = new TreeView { BorderThickness = new Thickness(0) };
            tree.Resources.Add(new DataTemplateKey(typeof(CatalogModNode)), library.Resources[new DataTemplateKey(typeof(CatalogModNode))]);
            tree.Items.Add(item);
            var anchor = new Button { Content = "Keep focus" };
            var host = new StackPanel();
            host.Children.Add(anchor);
            host.Children.Add(tree);
            window.Content = host;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            try
            {
                window.Show();
                Frame(40);
                var content = Assert.IsType<Border>(item.Template.FindName("ChildrenReveal", item));
                var toggle = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(item.Template.FindName("Expander", item));
                var arrow = Assert.IsType<Path>(toggle.Template.FindName("Chevron", toggle));
                Assert.Equal(Visibility.Collapsed, content.Visibility);
                Assert.True(double.IsNaN(content.Height));
                Assert.Equal(-90, Assert.IsType<RotateTransform>(arrow.RenderTransform).Angle);
                FocusManager.SetFocusedElement(window, anchor);
                var focus = FocusManager.GetFocusedElement(window);

                item.IsExpanded = true;
                Frame(45);
                if (SystemParameters.ClientAreaAnimation)
                {
                    Assert.True(content.HasAnimatedProperties);
                    Assert.True(content.Height > 0);
                    var currentHeight = content.Height;
                    item.IsExpanded = false;
                    Assert.InRange(Math.Abs(currentHeight - content.Height), 0, 0.01);
                    Frame(25);
                    currentHeight = content.Height;
                    item.IsExpanded = true;
                    Assert.InRange(Math.Abs(currentHeight - content.Height), 0, 0.01);
                }
                Frame(200);
                Assert.Equal(Visibility.Visible, content.Visibility);
                Assert.True(double.IsNaN(content.Height));
                Assert.False(content.HasAnimatedProperties);
                Assert.True(content.ActualHeight >= 100);
                Assert.Equal(0, Assert.IsType<RotateTransform>(arrow.RenderTransform).Angle);
                Assert.Same(focus, FocusManager.GetFocusedElement(window));
                Assert.True(node.IsExpanded);

                item.IsExpanded = false;
                Frame(200);
                Assert.Equal(Visibility.Collapsed, content.Visibility);
                Assert.True(double.IsNaN(content.Height));
                Assert.False(content.HasAnimatedProperties);
                Assert.False(node.IsExpanded);

                item.IsExpanded = true;
                Frame(30);
                host.Children.Remove(tree);
                Frame(20);
                Assert.False(content.HasAnimatedProperties);
                Assert.True(double.IsNaN(content.Height));
                Assert.False(Assert.IsType<RotateTransform>(arrow.RenderTransform).HasAnimatedProperties);
                Assert.True(node.IsExpanded);
                host.Children.Add(tree);
                Frame(30);
                Assert.Equal(Visibility.Visible, content.Visibility);
                Assert.False(content.HasAnimatedProperties);
                Assert.True(double.IsNaN(content.Height));
                return true;
            }
            finally { window.Close(); }
        });
    }

    private static void Frame(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += Stop;
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); timer.Tick -= Stop; }
        void Stop(object? sender, EventArgs e) => frame.Continue = false;
    }
}
