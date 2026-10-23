using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;

namespace Minecraft.Tests;

public sealed class DialogLayoutTests
{
    [Theory]
    [InlineData(1, 440)]
    [InlineData(1.5, 440)]
    [InlineData(2, 440)]
    [InlineData(2, 280)]
    public void BuildRemovalChoices_FitAndKeepCancelAsTheDefault(double dpi, double width)
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Program", "App.xaml")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            var app = XDocument.Load(Path.Combine(directory.FullName, "Program", "App.xaml"));
            var markup = XDocument.Load(Path.Combine(directory.FullName, "Program", "BuildRemovalConfirmationDialog.xaml"));
            var root = markup.Root!;
            root.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))!.Remove();
            foreach (var declaration in app.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
                if (root.Attribute(declaration.Name) is null) root.Add(new XAttribute(declaration));
            foreach (var handler in root.Descendants().Attributes("Click").ToArray()) handler.Remove();
            root.AddFirst(new XElement(root.Name.Namespace + "Window.Resources", app.Root.Elements().Single().Elements()));
            var dialog = (Window)XamlReader.Parse(markup.ToString());
            var panel = (StackPanel)dialog.FindName("RemovalPanel");
            VisualTreeHelper.SetRootDpi(dialog, new DpiScale(dpi, dpi));
            VisualTreeHelper.SetRootDpi(panel, new DpiScale(dpi, dpi));
            DialogScale.FitToClientArea(dialog, panel, new Size(width, 1000));
            panel.Measure(new Size(width, double.PositiveInfinity));
            panel.Arrange(new Rect(new Point(), panel.DesiredSize));
            panel.UpdateLayout();
            var names = new[] { "ListOnlyButton", "BuildOnlyButton", "WithWorldsButton", "KeepButton" };
            var labels = new[] { "Убрать из списка", "Удалить сборку", "Удалить сборку с мирами", "Не хочу" };
            for (var i = 0; i < names.Length; i++)
            {
                var button = (Button)dialog.FindName(names[i]);
                Assert.Equal(labels[i], button.Content);
                Assert.Equal(i == 3, button.IsDefault);
                Assert.Equal(i == 3, button.IsCancel);
                Assert.InRange(button.ActualWidth, 1, width);
                button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.True(button.DesiredSize.Width <= button.ActualWidth, $"The choice '{labels[i]}' is clipped.");
            }
            return true;
        });
    }

    [Theory]
    [InlineData(1800, 1020, 0, 0, 1368, 650)]
    [InlineData(-100, 1020, -1920, 0, -552, 650)]
    [InlineData(-2200, -100, -1920, 0, -1920, 0)]
    [InlineData(100, 100, 0, 0, 100, 100)]
    public void ADialogCenteredOnAnEdgeOwner_StaysInsideItsMonitor(
        int left, int top, int workLeft, int workTop, int expectedLeft, int expectedTop)
    {
        var position = DialogScale.ClampPosition(
            new Rect(left, top, 552, 390), new Rect(workLeft, workTop, 1920, 1040));
        Assert.Equal(new Point(expectedLeft, expectedTop), position);
    }

    [Fact]
    public void AnOrdinaryDialog_KeepsItsTextAndPreferredWidth()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var text = new TextBlock { Text = "Заменить настройки управления?", TextWrapping = TextWrapping.Wrap };
            var panel = new StackPanel { Width = 520, Margin = new Thickness(16) };
            panel.Children.Add(text);
            var dialog = Dialog(panel);

            DialogScale.FitToClientArea(dialog, panel, new Size(960, 680));

            Assert.Same(panel, dialog.Content);
            Assert.Equal(520, panel.Width);
            Assert.Equal((double)dialog.FindResource("Font.SizeBody"), text.FontSize);
            Assert.True(panel.LayoutTransform.Value.IsIdentity);
            return true;
        });
    }

    [Fact]
    public void ACrampedWorkArea_ReflowsActionsAndLetsThePlayerScrollToThem()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var text = new TextBlock
            {
                Text = string.Concat(Enumerable.Repeat("Удаление сборки и её миров нельзя отменить. ", 30)),
                TextWrapping = TextWrapping.Wrap
            };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var label in new[] { "Убрать из списка", "Удалить сборку", "Удалить сборку с мирами", "Не хочу" })
                actions.Children.Add(new Button { Content = label, Width = 140, Height = 30 });
            var panel = new StackPanel { Width = 520, Margin = new Thickness(16) };
            panel.Children.Add(text);
            panel.Children.Add(actions);
            var dialog = Dialog(panel);

            DialogScale.FitToClientArea(dialog, panel, new Size(380, 180));

            var viewer = Assert.IsType<ScrollViewer>(dialog.Content);
            viewer.Measure(new Size(380, 180));
            viewer.Arrange(new Rect(0, 0, 380, 180));
            viewer.UpdateLayout();
            viewer.ScrollToEnd();
            viewer.UpdateLayout();

            Assert.Equal((double)dialog.FindResource("Font.SizeBody"), text.FontSize);
            Assert.True(panel.LayoutTransform.Value.IsIdentity);
            Assert.True(panel.ActualWidth + panel.Margin.Left + panel.Margin.Right <= viewer.ActualWidth);
            Assert.Equal(Orientation.Vertical, actions.Orientation);
            Assert.True(viewer.ExtentHeight > viewer.ViewportHeight);
            Assert.Equal(0, viewer.ScrollableWidth);
            var cancel = (Button)actions.Children[^1];
            var bottom = cancel.TransformToAncestor(viewer).Transform(new Point(0, cancel.ActualHeight)).Y;
            Assert.InRange(bottom, 0, viewer.ActualHeight);
            return true;
        });
    }

    [Fact]
    public void AScreenCornerNotice_KeepsItsNativeSurfaceAndText()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var text = new TextBlock { Text = "Друг появился в сети", TextWrapping = TextWrapping.Wrap };
            var surface = new Border { Width = 320, Padding = new Thickness(16), Child = text };
            var notice = Dialog(surface);
            notice.WindowStyle = WindowStyle.None;

            DialogScale.FitToClientArea(notice, surface, new Size(960, 680));

            Assert.Same(surface, notice.Content);
            Assert.Equal(320, surface.Width);
            Assert.Equal((double)notice.FindResource("Font.SizeBody"), text.FontSize);
            Assert.True(surface.LayoutTransform.Value.IsIdentity);
            return true;
        });
    }

    private static Window Dialog(FrameworkElement content)
    {
        var dialog = WindowCanvasTests.LoadWindow();
        dialog.Content = content;
        dialog.MinWidth = 0;
        dialog.MinHeight = 0;
        dialog.SizeToContent = SizeToContent.WidthAndHeight;
        return dialog;
    }
}
