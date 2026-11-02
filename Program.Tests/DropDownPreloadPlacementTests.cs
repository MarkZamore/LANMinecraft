using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Minecraft.Tests;

public sealed class DropDownPreloadPlacementTests
{
    [Theory]
    [InlineData("BuildComboBox", false)]
    [InlineData("WorldComboBox", false)]
    [InlineData("OnlinePlayerComboBox", false)]
    [InlineData("DiagnosticLogTargetComboBox", false)]
    [InlineData("BuildComboBox", true)]
    [InlineData("WorldComboBox", true)]
    [InlineData("OnlinePlayerComboBox", true)]
    [InlineData("DiagnosticLogTargetComboBox", true)]
    public void FirstOpen_UsesTheControlsScreenBoundsBeforeAndAfterHiddenPreparation(string name, bool prepare)
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var window = WindowCanvasTests.LoadWindow();
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 220;
            window.Top = 120;
            window.Width = 820;
            window.Height = 640;
            window.SizeChanged += (_, _) => AdaptiveLauncherLayout.Apply(window);
            ((ScrollViewer)window.FindName("BodyScroll")).SizeChanged += (_, _) => AdaptiveLauncherLayout.Apply(window);
            var dropdown = (CenteredDropDown)window.FindName(name);
            dropdown.ItemsSource = Enumerable.Range(0, 80)
                .Select(index => new { Name = $"Item {index}", DisplayName = $"Item {index}" }).ToArray();
            dropdown.SelectedItem = dropdown.ItemsSource.Cast<object>().ElementAt(37);
            dropdown.IsEnabled = true;
            try
            {
                window.Show();
                Drain();
                if (prepare)
                {
                    var root = (Grid)window.FindName("RootGrid");
                    var originalVisibility = root.Children.Cast<UIElement>()
                        .ToDictionary(element => element, element => element.Visibility);
                    foreach (var child in root.Children.Cast<UIElement>()) child.Visibility = Visibility.Collapsed;
                    Drain();
                    var panelName = name == "DiagnosticLogTargetComboBox" ? "ReportPanel" : "WorldTransferPanel";
                    if (name != "BuildComboBox")
                        foreach (var action in UiPreparation.Prepare((FrameworkElement)window.FindName(panelName)))
                        { action(); Drain(); }
                    foreach (var action in dropdown.Prepare()) { action(); Drain(); }
                    foreach (var child in root.Children.Cast<UIElement>()) child.Visibility = originalVisibility[child];
                    Drain();
                    // Peer/world discovery may replace the source after its
                    // closed popup has already been prepared.
                    dropdown.ItemsSource = Enumerable.Range(0, 84)
                        .Select(index => new { Name = $"Updated {index}", DisplayName = $"Updated {index}" }).ToArray();
                    dropdown.SelectedItem = dropdown.ItemsSource.Cast<object>().ElementAt(40);
                    Drain();
                }
                if (name == "DiagnosticLogTargetComboBox")
                    ((Expander)window.FindName("ReportExpander")).IsExpanded = true;
                else if (name != "BuildComboBox")
                    ((Expander)window.FindName("WorldTransferExpander")).IsExpanded = true;
                Drain();
                dropdown.BringIntoView();
                Drain();
                AssertNativePopup(dropdown, prepare);
                window.WindowState = WindowState.Maximized;
                Drain();
                dropdown.BringIntoView();
                Drain();
                AssertNativePopup(dropdown);
                window.WindowState = WindowState.Normal;
                window.Width = 1104;
                window.Height = 761;
                window.Left += 70;
                window.Top += 30;
                Drain();
                dropdown.BringIntoView();
                Drain();
                AssertNativePopup(dropdown);
                return true;
            }
            finally { window.Close(); Drain(); }
        });
    }

    private static void AssertNativePopup(CenteredDropDown dropdown, bool prepareWhileOpen = false)
    {
        var popup = (Popup)dropdown.FindName("DropDownPopup");
        var button = (ToggleButton)dropdown.FindName("DropDownButton");
        button.IsChecked = true;
        Drain();
        if (prepareWhileOpen)
            foreach (var action in dropdown.Prepare()) { action(); Drain(); }
        try
        {
            Assert.True(popup.IsOpen, "The first click must leave the list open.");
            Assert.Same(button, popup.PlacementTarget);
            var source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(popup.Child));
            Assert.True(GetWindowRect(source.Handle, out var bounds));
            var topLeft = button.PointToScreen(new Point());
            var bottomRight = button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight));
            var dpi = VisualTreeHelper.GetDpi(button);
            var verticalGap = Math.Min(Math.Abs(bounds.Top - bottomRight.Y), Math.Abs(bounds.Bottom - topLeft.Y));
            var description = $"popup=({bounds.Left},{bounds.Top})-({bounds.Right},{bounds.Bottom}); " +
                              $"target={topLeft}-{bottomRight}; dpi={dpi.DpiScaleX},{dpi.DpiScaleY}";
            Assert.True(bounds.Right > bounds.Left && bounds.Bottom > bounds.Top, description);
            Assert.True(Math.Abs(bounds.Left - topLeft.X) <= 2, description);
            Assert.True(verticalGap <= 12 * dpi.DpiScaleY, description);
            Assert.True(bounds.Right - bounds.Left >= bottomRight.X - topLeft.X - 2, description);
            var list = (ListBox)dropdown.FindName("ItemsList");
            Assert.True(list.IsVisible && list.ActualWidth > 1 && list.ActualHeight > 1, description);
            Assert.NotEmpty(list.Items);
            Assert.Same(dropdown.ItemsSource, list.ItemsSource);
            Assert.Same(dropdown.ItemTemplate, list.ItemTemplate);
            Assert.Same(dropdown.SelectedItem, list.SelectedItem);
        }
        finally { popup.IsOpen = false; Drain(); }
    }

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect bounds);
}
