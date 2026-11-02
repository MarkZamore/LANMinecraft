using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Minecraft.Tests;

public sealed class UiPreparationLayoutTests
{
    [Fact]
    public void PreparingHiddenAttachedContentPreservesItsParentsLayoutSlot()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var window = WindowCanvasTests.LoadWindow();
            var panel = new StackPanel();
            panel.Children.Add(new Border { Height = 80 });
            var content = new Border { Height = 40, Visibility = Visibility.Hidden, Child = new Button { Content = "Menu" } };
            panel.Children.Add(content);
            window.Content = panel;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            try
            {
                window.Show(); window.UpdateLayout();
                var slot = LayoutInformation.GetLayoutSlot(content);
                Assert.Equal(80, slot.Top);
                foreach (var action in UiPreparation.Prepare(content)) action();
                Assert.Equal(slot, LayoutInformation.GetLayoutSlot(content));
                content.Visibility = Visibility.Visible;
                window.UpdateLayout();
                Assert.Equal(slot, LayoutInformation.GetLayoutSlot(content));
            }
            finally { window.Close(); }
            return true;
        });
    }
}
