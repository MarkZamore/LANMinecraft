using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;

namespace Minecraft.Tests;

public sealed class FluentControlMotionTests
{
    [Fact]
    public void SectionMotion_ReversesSafelyAndRestoresAutomaticHeight()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var window = WindowCanvasTests.LoadWindow();
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            try
            {
                window.Show();
                Frame(30);
                var category = Assert.IsType<Expander>(window.FindName("WorldTransferExpander"));
                category.ApplyTemplate();
                var content = Assert.IsType<Border>(category.Template.FindName("SectionContent", category));
                var header = Assert.IsType<ToggleButton>(category.Template.FindName("HeaderToggle", category));
                header.ApplyTemplate();
                var arrow = Assert.IsType<Path>(header.Template.FindName("Chevron", header));
                Assert.Equal(Visibility.Collapsed, content.Visibility);
                Assert.Equal(-90, Assert.IsType<RotateTransform>(arrow.RenderTransform).Angle);

                category.IsExpanded = true;
                Frame(50);
                if (SystemParameters.ClientAreaAnimation)
                {
                    Assert.True(content.HasAnimatedProperties);
                    Assert.True(content.Height > 0);
                    Assert.InRange(content.Opacity, 0.001, 0.999);
                }
                else
                {
                    Assert.False(content.HasAnimatedProperties);
                    Assert.True(double.IsNaN(content.Height));
                }

                category.IsExpanded = false;
                Frame(30);
                category.IsExpanded = true;
                Frame(280);
                Assert.Equal(Visibility.Visible, content.Visibility);
                Assert.True(double.IsNaN(content.Height));
                Assert.Equal(1, content.Opacity);
                Assert.Equal(0, Assert.IsType<RotateTransform>(arrow.RenderTransform).Angle);

                category.IsExpanded = false;
                Frame(250);
                Assert.Equal(Visibility.Collapsed, content.Visibility);
                Assert.True(double.IsNaN(content.Height));
                Assert.False(content.HasAnimatedProperties);
                Assert.Equal(-90, Assert.IsType<RotateTransform>(arrow.RenderTransform).Angle);
                return true;
            }
            finally
            {
                window.Close();
            }
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
