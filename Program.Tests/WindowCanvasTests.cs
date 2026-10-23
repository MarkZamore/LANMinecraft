using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace Minecraft.Tests;

/// <summary>
/// Measures the responsive launcher with its own controls and Windows font.
/// A finite client area is essential: measuring at infinity hides clipping
/// and prevents ScrollViewer from establishing a real viewport.
/// </summary>
public sealed class WindowCanvasTests
{
    [Theory]
    [InlineData(804, 521)]
    [InlineData(1104, 761)]
    [InlineData(1248, 761)]
    [InlineData(1904, 1001)]
    public void TheLayout_KeepsTwoColumnsAndEveryExpandedControlReachable(double width, double height)
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            ExpandSections(window);
            Element<Button>(window, "ChangePlayerNameButton").Content = "Сохранить";
            LayoutWindow(window, new Size(width, height));
            var root = Element<Grid>(window, "RootGrid");
            var body = Element<ScrollViewer>(window, "BodyScroll");
            var bodyGrid = Element<Grid>(window, "BodyGrid");
            var side = Element<Border>(window, "SidePanelCard");
            AssertColumnRatio(root);
            Assert.Equal(24, root.ColumnDefinitions[1].ActualWidth, 3);
            Assert.True(double.IsPositiveInfinity(root.MaxWidth));
            AssertWidthWithinOnePhysicalPixel(width - root.Margin.Left - root.Margin.Right, root.ActualWidth, root);
            Assert.Equal(2, Grid.GetColumn(side));
            Assert.InRange(Bounds(side, root).Left - Bounds(bodyGrid, root).Right, 23.5, 24.5);
            var settings = Element<Grid>(window, "SettingsGrid");
            Assert.Equal(3, settings.RowDefinitions.Count);
            var rows = new[] { "NameSettingsRow", "BuildSettingsRow", "MemorySettingsRow", "PresetSettingsRow", "BuildActionsRow" }
                .Select(name => Element<Grid>(window, name)).ToArray();
            Assert.Equal(new[] { 0, 1, 2, 2, 1 }, rows.Select(Grid.GetRow));
            Assert.Equal(new[] { 0, 0, 2, 0, 2 }, rows.Select(Grid.GetColumn));
            string[] bodyControls =
            [
                "PlayerNameTextBox", "ChangePlayerNameButton", "BuildComboBox", "SkinButton",
                "MemoryTextBox", "DeleteBuildButton", "ControlsPresetButton", "WorldComboBox",
                "DeleteWorldButton", "OnlinePlayerComboBox", "TransferButton",
                "DiagnosticLogTargetComboBox", "SendBugReportButton", "BugReportMessageTextBox"
            ];
            foreach (var name in bodyControls)
            {
                var element = Element<FrameworkElement>(window, name);
                var bounds = Bounds(element, bodyGrid);
                Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0, $"{name} has no usable size");
                Assert.True(bounds.Left >= -0.5 && bounds.Right <= bodyGrid.ActualWidth + 0.5,
                    $"{name} is clipped at {width} DIP: {bounds}; body width {bodyGrid.ActualWidth}");
                Assert.True(bounds.Bottom <= Math.Max(body.ExtentHeight, body.ViewportHeight) + 0.5);
            }
            Assert.True(Element<TextBox>(window, "PlayerNameTextBox").ActualWidth >= (width < 960 ? 64 : 95.5));
            Assert.True(body.ScrollableWidth <= 0.5);
            var footer = Element<Grid>(window, "FooterGrid");
            Assert.True(Bounds(footer, root).Bottom <= root.ActualHeight + 0.5);
            var update = Element<Grid>(window, "UpdateArea");
            Assert.True(Bounds(body, root).Bottom <= Bounds(update, root).Top + 0.5);
            Assert.True(Bounds(update, root).Bottom <= Bounds(footer, root).Top + 0.5);
            AssertSidebarUpdateAlignment(window);
            foreach (var name in new[] { "UpdateButton", "RetrySteamButton", "SidePanelToggleButton", "ChatSendButton" })
            {
                var bounds = Bounds(Element<FrameworkElement>(window, name), root);
                Assert.True(bounds.Left >= -0.5 && bounds.Right <= root.ActualWidth + 0.5, $"{name} is clipped");
                Assert.True(bounds.Bottom <= root.ActualHeight + 0.5);
            }
            var player = Element<TextBox>(window, "PlayerNameTextBox");
            Assert.Equal((double)window.FindResource("Font.SizeBody"), player.FontSize);
            var transform = player.TransformToAncestor(root);
            Assert.Equal(100, transform.Transform(new Point(100, 0)).X - transform.Transform(new Point(0, 0)).X, 3);
            AssertStretchedActions(window);
            return true;
        });
    }

    [Fact]
    public void FirstLaunch_UsesTheMinimumAndAllHeadersFitWithoutScrolling()
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            Assert.Equal(820, window.Width);
            Assert.Equal(560, window.Height);
            Assert.Equal(window.Width, window.MinWidth);
            Assert.Equal(window.Height, window.MinHeight);
            Assert.Equal(window.MinWidth * 28, window.MinHeight * 41);
            Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
            Assert.True(double.IsNaN(Element<Grid>(window, "RootGrid").Width));
            Assert.DoesNotContain("<Viewbox", File.ReadAllText(FindRepositoryFile("Program", "MainWindow.xaml")), StringComparison.Ordinal);
            LayoutWindow(window, new Size(804, 521));
            foreach (var name in SectionNames)
            {
                var section = Element<Expander>(window, name);
                Assert.False(section.IsExpanded);
                Assert.Equal(Element<Button>(window, "PlayButton").ActualHeight, section.ActualHeight, 3);
            }
            Assert.True(Element<ScrollViewer>(window, "BodyScroll").ScrollableHeight <= 0.5);
            return true;
        });
    }

    [Theory]
    [InlineData(804, 521)]
    [InlineData(1904, 1001)]
    public void SectionsFollowSettingsAndPreserveTheReportDraft(double width, double height)
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            var size = new Size(width, height);
            var report = Element<TextBox>(window, "BugReportMessageTextBox");
            report.Text = "Черновик описания сохраняется отдельно от состояния";
            report.Select(3, 9);
            LayoutWindow(window, size);
            var bodyGrid = Element<Grid>(window, "BodyGrid");
            var settings = Element<Grid>(window, "SettingsGrid");
            var transfer = Element<Expander>(window, "WorldTransferExpander");
            Assert.Null(window.FindName("DiagnosticStatusArea"));
            Assert.Null(window.FindName("DiagnosticLogStatusText"));
            Assert.False(transfer.IsExpanded);
            Assert.True(Bounds(transfer, bodyGrid).Top >= Bounds(settings, bodyGrid).Bottom);
            ExpandSections(window);
            LayoutWindow(window, size);
            Assert.Equal("Черновик описания сохраняется отдельно от состояния", report.Text);
            Assert.Equal(3, report.SelectionStart);
            Assert.Equal(9, report.SelectionLength);
            return true;
        });
    }

    [Theory]
    [InlineData(804, 521, 1)]
    [InlineData(804, 521, 1.25)]
    [InlineData(804, 521, 1.5)]
    [InlineData(804, 521, 2)]
    [InlineData(1104, 761, 1)]
    [InlineData(1104, 761, 1.25)]
    [InlineData(1104, 761, 1.5)]
    [InlineData(1104, 761, 2)]
    [InlineData(1904, 1001, 1)]
    [InlineData(1904, 1001, 1.25)]
    [InlineData(1904, 1001, 1.5)]
    [InlineData(1904, 1001, 2)]
    public void ClosedSectionFrames_MatchThePlayButtonHeightAtEverySizeAndDpi(double width, double height, double scale)
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            var dpi = new DpiScale(scale, scale);
            var content = (FrameworkElement)window.Content;
            VisualTreeHelper.SetRootDpi(window, dpi);
            VisualTreeHelper.SetRootDpi(content, dpi);
            LayoutWindow(window, new Size(width, height));
            // Templates create new visual children during the first measure.
            // Apply the requested DPI again so the controls and their templates
            // are measured at that scale rather than the host's native DPI.
            VisualTreeHelper.SetRootDpi(window, dpi);
            VisualTreeHelper.SetRootDpi(content, dpi);
            LayoutWindow(window, new Size(width, height));
            var play = Element<Button>(window, "PlayButton");
            Assert.Equal(scale, VisualTreeHelper.GetDpi(play).DpiScaleY, 6);
            Assert.Equal(scale, VisualTreeHelper.GetDpi(Element<Grid>(window, "PlayArea")).DpiScaleY, 6);
            Assert.Equal(scale, VisualTreeHelper.GetDpi(Element<Grid>(window, "BodyGrid")).DpiScaleY, 6);
            Assert.Equal(scale, VisualTreeHelper.GetDpi(Element<Border>(window, "SidePanelCard")).DpiScaleY, 6);
            Assert.Equal(scale, VisualTreeHelper.GetDpi(Element<ProgressBar>(window, "UpdateProgressBar")).DpiScaleY, 6);
            AssertSidebarUpdateAlignment(window);
            foreach (var name in SectionNames)
            {
                var section = Element<Expander>(window, name);
                var header = Assert.IsAssignableFrom<FrameworkElement>(section.Template.FindName("HeaderToggle", section));
                Assert.False(section.IsExpanded);
                Assert.Equal(scale, VisualTreeHelper.GetDpi(section).DpiScaleY, 6);
                Assert.Equal(scale, VisualTreeHelper.GetDpi(header).DpiScaleY, 6);
                Assert.Equal(play.ActualHeight, section.ActualHeight, 3);
                AssertWidthWithinOnePhysicalPixel(Element<Grid>(window, "LaunchActions").ActualWidth, section.ActualWidth, section);
            }
            AssertStretchedActions(window);
            ExpandSections(window);
            LayoutWindow(window, new Size(width, height));
            VisualTreeHelper.SetRootDpi(window, dpi);
            VisualTreeHelper.SetRootDpi(content, dpi);
            LayoutWindow(window, new Size(width, height));
            foreach (var name in SectionNames)
                AssertWidthWithinOnePhysicalPixel(Element<Grid>(window, "LaunchActions").ActualWidth, Element<Expander>(window, name).ActualWidth, play);
            AssertStretchedActions(window);
            Assert.True(Element<ScrollViewer>(window, "BodyScroll").ScrollableWidth <= 0.5);
            return true;
        });
    }

    [Fact]
    public void TheSidebar_RemainsVisibleWhileLeftSectionsCollapseAndItsFaceChanges()
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            var size = new Size(804, 521);
            var card = Element<Border>(window, "SidePanelCard");
            var title = Element<TextBlock>(window, "SidePanelTitle");
            var chat = Element<Grid>(window, "ChatPanel");
            var news = Element<Grid>(window, "ChangelogPanel");
            LayoutWindow(window, size);
            var height = card.ActualHeight;
            foreach (var expanded in new[] { true, false })
            {
                foreach (var name in SectionNames) Element<Expander>(window, name).IsExpanded = expanded;
                foreach (var showNews in new[] { true, false })
                {
                    chat.Visibility = showNews ? Visibility.Collapsed : Visibility.Visible;
                    news.Visibility = showNews ? Visibility.Visible : Visibility.Collapsed;
                    title.Text = showNews ? "Что нового" : "AI-помощник";
                    LayoutWindow(window, size);
                    Assert.Same(card, window.FindName("SidePanelCard"));
                    Assert.Equal(height, card.ActualHeight, 3);
                    Assert.True(title.ActualWidth > 0 && title.ActualHeight >= 31.5);
                    Assert.True((showNews ? news : chat).ActualHeight > 200);
                    var root = Element<Grid>(window, "RootGrid");
                    Assert.True(Bounds(card, root).Bottom <= Bounds(Element<Grid>(window, "FooterGrid"), root).Top + 0.5);
                }
            }
            return true;
        });
    }

    [Theory]
    [InlineData(0)] // The current Windows scale.
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void TheScrollbarThumb_FitsItsShortTrackSlotAtEveryPositionAndOnHover(double scale)
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            var root = new Grid { Width = 64, Height = 264, UseLayoutRounding = true, Resources = window.Resources };
            var bar = new ScrollBar
            {
                Orientation = Orientation.Vertical,
                Width = (double)window.FindResource("Size.Scrollbar"),
                Height = 240,
                Maximum = 1_000_000,
                ViewportSize = 240,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            root.Children.Add(bar);
            void Layout()
            {
                root.Measure(new Size(64, 264));
                root.Arrange(new Rect(0, 0, 64, 264));
                root.UpdateLayout();
            }
            Layout();
            var track = Assert.IsType<Track>(bar.Template.FindName("PART_Track", bar));
            var thumb = track.Thumb;
            thumb.ApplyTemplate();
            var grip = Assert.IsType<Border>(thumb.Template.FindName("Grip", thumb));
            // Create the template first, then remeasure its visuals at the test DPI.
            if (scale > 0)
            {
                VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
                void InvalidateLayout(Visual visual)
                {
                    if (visual is UIElement element) { element.InvalidateMeasure(); element.InvalidateArrange(); }
                    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(visual); i++)
                        InvalidateLayout((Visual)VisualTreeHelper.GetChild(visual, i));
                }
                InvalidateLayout(root);
            }
            var mouseOverKey = Assert.IsType<DependencyPropertyKey>(typeof(UIElement)
                .GetField("IsMouseOverPropertyKey", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(null));
            foreach (var value in new[] { 0d, 500_000d, 1_000_000d })
            foreach (var hovered in new[] { false, true })
            {
                bar.Value = value;
                bar.SetValue(mouseOverKey, hovered);
                Layout();
                var slot = LayoutInformation.GetLayoutSlot(thumb).Height;
                Assert.InRange(slot, 0.01, 12);
                Assert.Equal(slot, thumb.ActualHeight, 3);
                Assert.Equal(slot, grip.ActualHeight, 3);
                var configuredWidth = (double)window.FindResource(hovered ? "Size.ScrollThumbExpanded" : "Size.ScrollThumb");
                Assert.Equal(configuredWidth, grip.Width, 3);
                // At 125%, WPF rounds a 2 DIP grip to 2 pixels (1.6 DIP).
                var dpiScale = VisualTreeHelper.GetDpi(grip).DpiScaleX;
                if (scale > 0) Assert.Equal(scale, dpiScale, 6);
                var roundedWidth = Math.Round(configuredWidth * dpiScale) / dpiScale;
                Assert.Equal(roundedWidth, grip.ActualWidth, 3);
                var clip = VisualTreeHelper.GetClip(thumb);
                Assert.True(clip is null || clip.Bounds.Height >= thumb.ActualHeight - 0.001,
                    $"Thumb cap is clipped at value {value}, hover {hovered}: slot {slot}, clip {clip?.Bounds}");
            }
            return true;
        });
    }

    [Theory]
    [InlineData(804, 1)]
    [InlineData(804, 1.25)]
    [InlineData(804, 1.5)]
    [InlineData(804, 2)]
    [InlineData(1904, 1)]
    [InlineData(1904, 1.25)]
    [InlineData(1904, 1.5)]
    [InlineData(1904, 2)]
    public void TheExternalScrollbar_SpansPlayAndBodyAndScrollsOnlyTheBody(double width, double scale)
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            var root = Element<Grid>(window, "RootGrid");
            var body = Element<ScrollViewer>(window, "BodyScroll");
            var bar = Element<ScrollBar>(window, "BodyScrollBar");
            var play = Element<Button>(window, "PlayButton");
            var size = new Size(width, 521);
            var dpi = new DpiScale(scale, scale);
            void Layout()
            {
                LayoutWindow(window, size);
                VisualTreeHelper.SetRootDpi(window, dpi);
                VisualTreeHelper.SetRootDpi(root, dpi);
                DrainDispatcher();
                LayoutWindow(window, size);
            }
            Layout();
            Assert.Equal(Visibility.Collapsed, bar.Visibility);
            ExpandSections(window);
            Layout();
            Assert.True(body.ScrollableHeight > 0);
            Assert.Equal(Visibility.Visible, bar.Visibility);
            Assert.Same(body, ScrollBarBridge.GetTarget(bar));
            Assert.Null(body.Template.FindName("PART_VerticalScrollBar", body));
            Assert.Equal(scale, VisualTreeHelper.GetDpi(bar).DpiScaleY, 6);
            var playBounds = Bounds(play, root);
            var barBounds = Bounds(bar, root);
            Assert.Equal(playBounds.Top, barBounds.Top, 3);
            Assert.Equal(Bounds(body, root).Bottom, barBounds.Bottom, 3);
            Assert.True(barBounds.Left >= playBounds.Right + 3.5);
            Assert.InRange(Bounds(Element<Border>(window, "SidePanelCard"), root).Left - barBounds.Right, 3.5, 4.5);
            AssertStretchedActions(window);

            var middle = body.ScrollableHeight / 2;
            bar.SetCurrentValue(RangeBase.ValueProperty, middle);
            Layout();
            Assert.Equal(middle, body.VerticalOffset, 3);
            Assert.Equal(body.VerticalOffset, bar.Value, 3);
            body.ScrollToTop();
            Layout();
            Assert.Equal(0, bar.Value, 3);
            Assert.True(ScrollBar.PageDownCommand.CanExecute(null, bar));
            ScrollBar.PageDownCommand.Execute(null, bar);
            Layout();
            Assert.True(body.VerticalOffset > 0);
            ScrollBar.PageUpCommand.Execute(null, bar);
            Layout();
            Assert.Equal(0, body.VerticalOffset, 3);
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            bar.RaiseEvent(wheel);
            Layout();
            Assert.True(wheel.Handled);
            var scrolling = SmoothScrollingController.Attach(body);
            Assert.Equal(Math.Min(36, body.ScrollableHeight), scrolling.TargetOffset, 3);
            for (int frame = 0; frame < 60; frame++)
            {
                scrolling.AdvanceFrame(1000d / 60);
                body.UpdateLayout();
            }
            Assert.True(body.VerticalOffset > 0);
            Assert.False(scrolling.IsAnimating);
            body.LineDown();
            Layout();
            Assert.Equal(body.VerticalOffset, bar.Value, 3);
            Assert.Equal(playBounds, Bounds(play, root));
            Assert.True(body.ScrollableWidth <= 0.5);
            foreach (var name in SectionNames) Element<Expander>(window, name).IsExpanded = false;
            Layout();
            Assert.Equal(Visibility.Collapsed, bar.Visibility);
            Assert.Equal(0, body.VerticalOffset, 3);
            Assert.Equal(playBounds, Bounds(play, root));
            return true;
        });
    }

    [Fact]
    public void ReturningFromLibrary_PreservesAutomaticScrollbarVisibility()
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            var bar = Element<ScrollBar>(window, "BodyScrollBar");
            var library = Element<PackLibraryView>(window, "LibraryView");
            var size = new Size(804, 521);
            LayoutWindow(window, size);
            DrainDispatcher();
            LayoutWindow(window, size);
            Assert.Equal(Visibility.Collapsed, bar.Visibility);
            ExpandSections(window);
            LayoutWindow(window, size);
            DrainDispatcher();
            LayoutWindow(window, size);
            Assert.Equal(Visibility.Visible, bar.Visibility);
            for (var transition = 0; transition < 3; transition++)
            {
                library.Visibility = Visibility.Visible;
                // The transition itself hides the bar, before another layout or UI timer tick.
                Assert.Equal(Visibility.Collapsed, bar.Visibility);
                LayoutWindow(window, new Size(1104, 521));
                DrainDispatcher();
                LayoutWindow(window, size);
                Assert.Equal(Visibility.Collapsed, bar.Visibility);
                library.Visibility = Visibility.Collapsed;
                Assert.Equal(Visibility.Visible, bar.Visibility);
            }
            foreach (var name in SectionNames) Element<Expander>(window, name).IsExpanded = false;
            LayoutWindow(window, size);
            DrainDispatcher();
            LayoutWindow(window, size);
            Assert.Equal(Visibility.Collapsed, bar.Visibility);
            return true;
        });
    }

    [Fact]
    public void CollapsingASection_ReleasesItsHeightAndPreservesInput()
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            var size = new Size(804, 521);
            var report = Element<TextBox>(window, "BugReportMessageTextBox");
            report.Text = "Описание сохраняется после сворачивания";
            report.Select(3, 7);
            ExpandSections(window);
            LayoutWindow(window, size);
            var body = Element<ScrollViewer>(window, "BodyScroll");
            var expandedHeight = body.ExtentHeight;
            var expandedSectionsHeight = SectionNames.Sum(name => Element<Expander>(window, name).ActualHeight);
            foreach (var name in SectionNames) Element<Expander>(window, name).IsExpanded = false;
            LayoutWindow(window, size);
            var collapsedSectionsHeight = SectionNames.Sum(name => Element<Expander>(window, name).ActualHeight);
            Assert.True(collapsedSectionsHeight < expandedSectionsHeight - 250);
            Assert.True(body.ExtentHeight < expandedHeight);
            Assert.True(body.ScrollableHeight <= 0.5);
            foreach (var viewport in new[] { new Size(1904, 1001), size })
            {
                LayoutWindow(window, viewport);
                Assert.Same(report, window.FindName("BugReportMessageTextBox"));
                Assert.Equal("Описание сохраняется после сворачивания", report.Text);
                Assert.Equal(3, report.SelectionStart);
                Assert.Equal(7, report.SelectionLength);
                Assert.All(SectionNames, name => Assert.False(Element<Expander>(window, name).IsExpanded));
            }
            return true;
        });
    }

    [Fact]
    public void TheMinimumExpandedWindow_KeepsTheEndOfTheBodyReachable()
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            ExpandSections(window);
            var size = new Size(804, 521);
            LayoutWindow(window, size);
            var body = Element<ScrollViewer>(window, "BodyScroll");
            Assert.True(body.ScrollableHeight > 0);
            body.ScrollToEnd();
            LayoutWindow(window, size);
            DrainDispatcher();
            Assert.Equal(body.ScrollableHeight, body.VerticalOffset, 1);
            Assert.True(Bounds(Element<Expander>(window, "ReportExpander"), body).Bottom <= body.ActualHeight + 0.5);
            Assert.True(Bounds(Element<TextBox>(window, "BugReportMessageTextBox"), body).Top >= -0.5);
            return true;
        });
    }

    [Fact]
    public void ExtraWindowSpace_ExpandsTheRequestedActionsWhileTheEditorStaysFinite()
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            ExpandSections(window);
            LayoutWindow(window, new Size(804, 521));
            var report = Element<TextBox>(window, "BugReportMessageTextBox");
            var reportHeight = report.ActualHeight;
            var sideHeight = Element<Grid>(window, "SidePanel").ActualHeight;
            LayoutWindow(window, new Size(1904, 1401));
            Assert.Equal(reportHeight, report.ActualHeight, 3);
            Assert.Equal(120, report.ActualHeight, 3);
            Assert.True(Element<Grid>(window, "SidePanel").ActualHeight > sideHeight + 200);
            AssertStretchedActions(window);
            Assert.Equal(96, Element<Button>(window, "SendBugReportButton").ActualWidth, 3);
            return true;
        });
    }

    [Fact]
    public void TheNativeClient_MaximizesAndRestoresWithoutChangingColumnsOrSelection()
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            window.Opacity = 0;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(window).Handle;
                _ = SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000);
            };
            window.SizeChanged += (_, _) => AdaptiveLauncherLayout.Apply(window);
            var foreground = GetForegroundWindow();
            window.Activated += (_, _) => { if (foreground != 0) _ = SetForegroundWindow(foreground); };
            try
            {
                ExpandSections(window);
                window.Show();
                SettleNativeLayout(window);
                AssertNativeClientLayout(window);
                var handle = new WindowInteropHelper(window).Handle;
                var report = Element<TextBox>(window, "BugReportMessageTextBox");
                report.Text = "Сохранить выделенный текст после изменения окна";
                report.Select(10, 10);
                FocusManager.SetFocusedElement(window, report);
                var side = Element<Grid>(window, "SidePanel");
                foreach (var state in new[] { WindowState.Maximized, WindowState.Normal, WindowState.Maximized, WindowState.Normal })
                {
                    // Use the system commands used by the native caption,
                    // rather than only WPF's managed WindowState setter.
                    _ = SendMessage(handle, 0x0112, state == WindowState.Maximized ? 0xF030 : 0xF120, 0);
                    if (foreground != 0) _ = SetForegroundWindow(foreground);
                    SettleNativeLayout(window);
                    Assert.Equal(state, window.WindowState);
                    AssertNativeClientLayout(window);
                    var root = Element<Grid>(window, "RootGrid");
                    AssertColumnRatio(root);
                    Assert.Same(side, window.FindName("SidePanel"));
                    Assert.True(Element<Expander>(window, "ReportExpander").IsExpanded);
                }
                var dpi = VisualTreeHelper.GetDpi(window);
                foreach (var size in new[] { new Size(1140, 800), new Size(820, 560) })
                {
                    Assert.True(SetWindowPos(handle, 0, 0, 0,
                        (int)Math.Round(size.Width * dpi.DpiScaleX), (int)Math.Round(size.Height * dpi.DpiScaleY),
                        0x0002 | 0x0004 | 0x0010)); // keep position, z-order and activation
                    SettleNativeLayout(window);
                    AssertNativeClientLayout(window);
                }
                Assert.Equal(820, window.Width, 1);
                Assert.Equal(10, report.SelectionStart);
                Assert.Equal(10, report.SelectionLength);
                Assert.Same(report, FocusManager.GetFocusedElement(window));
            }
            finally
            {
                window.Close();
                if (foreground != 0) _ = SetForegroundWindow(foreground);
            }
            return true;
        });
    }

    [Theory]
    [InlineData(804, 521)]
    [InlineData(1104, 761)]
    [InlineData(1904, 1001)]
    public void ALongReport_ScrollsInsideItsFieldWithoutGrowingThePage(double width, double height)
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            ExpandSections(window);
            var size = new Size(width, height);
            LayoutWindow(window, size);
            var report = Element<TextBox>(window, "BugReportMessageTextBox");
            var body = Element<ScrollViewer>(window, "BodyScroll");
            var fieldHeight = report.ActualHeight;
            var pageHeight = body.ExtentHeight;
            report.Text = string.Concat(Enumerable.Repeat("Строка.\n", 400));
            LayoutWindow(window, size);
            DrainDispatcher();
            LayoutWindow(window, size);
            Assert.Equal(fieldHeight, report.ActualHeight, 1);
            Assert.Equal(pageHeight, body.ExtentHeight, 1);
            Assert.True(report.ExtentHeight > report.ViewportHeight);
            report.ScrollToEnd();
            DrainDispatcher();
            Assert.True(report.VerticalOffset > 0);
            return true;
        });
    }

    [Theory]
    [InlineData("BuildComboBox")]
    [InlineData("WorldComboBox")]
    [InlineData("OnlinePlayerComboBox")]
    [InlineData("DiagnosticLogTargetComboBox")]
    public void SelectedDropDownText_UsesTheLauncherFontAndEnabledStateColours(string controlName)
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            ExpandSections(window);
            var dropdown = Element<CenteredDropDown>(window, controlName);
            const string caption = "Выбранный элемент для проверки цвета";
            var item = new { Name = caption, DisplayName = caption };
            dropdown.ItemsSource = new[] { item };
            dropdown.SelectedItem = item;
            var size = new Size(804, 521);
            LayoutWindow(window, size);
            DrainDispatcher();
            LayoutWindow(window, size);
            var text = Assert.Single(VisualDescendants<TextBlock>(dropdown), element => element.Text == caption);
            Assert.Equal(((SolidColorBrush)window.FindResource("Brush.Text")).Color,
                Assert.IsType<SolidColorBrush>(text.Foreground).Color);
            Assert.Equal((double)window.FindResource("Font.SizeBody"), text.FontSize);
            Assert.Equal((FontFamily)window.FindResource("Font.Family"), text.FontFamily);
            Assert.Equal((FontWeight)window.FindResource("Font.WeightBody"), text.FontWeight);
            dropdown.IsEnabled = false;
            LayoutWindow(window, size);
            Assert.Equal(((SolidColorBrush)window.FindResource("Brush.TextDisabled")).Color,
                Assert.IsType<SolidColorBrush>(text.Foreground).Color);
            Assert.Equal(caption, text.Text);
            return true;
        });
    }

    [Theory]
    [InlineData("BuildComboBox")]
    [InlineData("WorldComboBox")]
    [InlineData("OnlinePlayerComboBox")]
    [InlineData("DiagnosticLogTargetComboBox")]
    public void OpenDropDown_LocksOnlyTheParentScrollAndReleasesItOnClose(string controlName)
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            ExpandSections(window);
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Show();
            try
            {
                var dropdown = Element<CenteredDropDown>(window, controlName);
                dropdown.ItemsSource = Enumerable.Range(0, 80).Select(i => new { Name = $"Item {i}", DisplayName = $"Item {i}" }).ToArray();
                var size = new Size(804, 521);
                LayoutWindow(window, size);
                var body = Element<ScrollViewer>(window, "BodyScroll");
                body.ScrollToVerticalOffset(40);
                DrainDispatcher();
                Assert.True(body.ScrollableHeight > body.VerticalOffset);
                var smooth = SmoothScrollingController.Attach(body);
                smooth.ScrollByWheelDelta(-120);
                var popup = (Popup)dropdown.FindName("DropDownPopup");
                popup.IsOpen = true;
                DrainDispatcher();
                // With Windows animations disabled, the preceding wheel input
                // scrolls immediately, before opening the popup acquires its lock.
                var offset = body.VerticalOffset;
                try
                {
                    Assert.Equal(byte.MaxValue, Assert.IsType<SolidColorBrush>(((Border)popup.Child).Background).Color.A);
                    Assert.False(smooth.IsAnimating);
                    body.ScrollToEnd();
                    DrainDispatcher();
                    Assert.Equal(offset, body.VerticalOffset, 3);
                    var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                    { RoutedEvent = UIElement.PreviewMouseWheelEvent };
                    body.RaiseEvent(wheel);
                    Assert.True(wheel.Handled);
                    Assert.False(smooth.IsAnimating);
                    Assert.True(smooth.ScrollByWheelDelta(-120));
                    smooth.AdvanceFrame(50);
                    DrainDispatcher();
                    Assert.Equal(offset, body.VerticalOffset, 3);
                    var list = (ListBox)dropdown.FindName("ItemsList");
                    list.ApplyTemplate();
                    list.UpdateLayout();
                    var popupScroll = VisualDescendants<ScrollViewer>(list).First();
                    popupScroll.ScrollToEnd();
                    DrainDispatcher();
                    Assert.True(popupScroll.VerticalOffset > 0);
                    Assert.Equal(offset, body.VerticalOffset, 3);
                }
                finally
                {
                    popup.IsOpen = false;
                    DrainDispatcher();
                }
                Assert.False(smooth.IsScrollLocked);
                body.ScrollToEnd();
                DrainDispatcher();
                Assert.True(body.VerticalOffset > offset);
                return true;
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(804)]
    [InlineData(1904)]
    public void SteamRetry_StaysNextToSteamWhileNewsStaysAtTheRight(double width)
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            LayoutWindow(window, new Size(width, 521));
            var group = Element<Grid>(window, "SteamConnectionPanel");
            var retry = Element<Button>(window, "RetrySteamButton");
            var label = group.Children.OfType<TextBlock>().Single(t => t.Text == "Steam:");
            Assert.Same(group, retry.Parent);
            Assert.InRange(Bounds(retry, group).Left - Bounds(label, group).Right, 7.5, 8.5);
            var footer = Element<Grid>(window, "FooterActions");
            var toggle = Element<Button>(window, "SidePanelToggleButton");
            AssertWidthWithinOnePhysicalPixel(footer.ActualWidth, Bounds(toggle, footer).Right, toggle);
            return true;
        });
    }

    private static readonly string[] SectionNames = ["WorldTransferExpander", "ReportExpander"];

    private static void AssertSidebarUpdateAlignment(Window window)
    {
        var root = Element<Grid>(window, "RootGrid");
        var cardBottom = Bounds(Element<Border>(window, "SidePanelCard"), root).Bottom;
        Assert.Equal(Bounds(Element<Grid>(window, "UpdateArea"), root).Bottom, cardBottom, 3);
        Assert.Equal(Bounds(Element<ProgressBar>(window, "UpdateProgressBar"), root).Bottom, cardBottom, 3);
        Assert.Equal(Bounds(Element<Button>(window, "UpdateButton"), root).Bottom, cardBottom, 3);
    }

    private static void AssertNativeClientLayout(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        Assert.True(GetClientRect(handle, out var client));
        var dpi = VisualTreeHelper.GetDpi(window);
        Assert.Equal(GetDpiForWindow(handle) / 96d, dpi.DpiScaleX, 6);
        Assert.Equal(GetDpiForWindow(handle) / 96d, dpi.DpiScaleY, 6);
        var root = Element<Grid>(window, "RootGrid");
        // A correct 2:1 ratio alone can hide an entire old maximized layout
        // retained inside a smaller HWND. Match the actual native client too.
        AssertWidthWithinOnePhysicalPixel((client.Right - client.Left) / dpi.DpiScaleX,
            root.RenderSize.Width + root.Margin.Left + root.Margin.Right, root);
        Assert.InRange(Math.Abs((client.Bottom - client.Top) / dpi.DpiScaleY -
            root.RenderSize.Height - root.Margin.Top - root.Margin.Bottom), 0, 1 / dpi.DpiScaleY + 0.000001);
        foreach (var name in new[]
        {
            "PlayButton", "BodyScroll", "BodyScrollBar", "SidePanelCard", "ChatMessageTextBox", "ChatSendButton",
            "UpdateArea", "UpdateButton", "FooterGrid", "SidePanelToggleButton"
        })
        {
            var element = Element<FrameworkElement>(window, name);
            if (element.Visibility != Visibility.Visible) continue;
            var bounds = Bounds(element, root);
            Assert.True(bounds.Left >= -0.5 && bounds.Top >= -0.5 &&
                bounds.Right <= root.ActualWidth + 0.5 && bounds.Bottom <= root.ActualHeight + 0.5,
                $"{name} lies outside native client content after resize: {bounds}; root {root.RenderSize}");
        }
        AssertSidebarUpdateAlignment(window);
        AssertStretchedActions(window);
    }

    private static void AssertStretchedActions(Window window)
    {
        var play = Element<Button>(window, "PlayButton");
        Assert.Equal(Element<Grid>(window, "PlayArea").ColumnDefinitions[0].ActualWidth, Element<Grid>(window, "LaunchActions").ActualWidth, 3);
        AssertWidthWithinOnePhysicalPixel(play.ActualWidth * 2 / 3, Element<Button>(window, "LibraryButton").ActualWidth, play);
        Assert.Equal(play.FontWeight, Element<Button>(window, "LibraryButton").FontWeight);
        Assert.Equal(play.FontSize, Element<Button>(window, "LibraryButton").FontSize);
        Assert.Equal(play.FontFamily, Element<Button>(window, "LibraryButton").FontFamily);
        Assert.Equal(8, Element<Grid>(window, "LaunchActions").ColumnDefinitions[1].ActualWidth, 3);
        AssertWidthWithinOnePhysicalPixel(Element<Grid>(window, "BodyGrid").ActualWidth, Element<Grid>(window, "LaunchActions").ActualWidth, play);
        var skin = Element<Button>(window, "SkinButton");
        var settings = Element<Grid>(window, "SettingsGrid");
        var library = Element<Button>(window, "LibraryButton");
        AssertWidthWithinOnePhysicalPixel(play.ActualWidth, settings.ColumnDefinitions[0].ActualWidth, play);
        AssertWidthWithinOnePhysicalPixel(library.ActualWidth, settings.ColumnDefinitions[2].ActualWidth, library);
        AssertWidthWithinOnePhysicalPixel(play.ActualWidth, Bounds(Element<TextBox>(window, "PlayerNameTextBox"), settings).Right, play);
        AssertWidthWithinOnePhysicalPixel(play.ActualWidth, Bounds(Element<CenteredDropDown>(window, "BuildComboBox"), settings).Right, play);
        AssertWidthWithinOnePhysicalPixel(library.ActualWidth, Element<Button>(window, "ChangePlayerNameButton").ActualWidth, library);
        // Compare each rounded column with its unrounded share. Multiplying
        // the rounded skin width by 4/3 amplifies its device-pixel error.
        Assert.Equal(3, Element<Grid>(window, "PresetSettingsRow").ColumnDefinitions[0].Width.Value);
        Assert.Equal(4, Element<Grid>(window, "PresetSettingsRow").ColumnDefinitions[2].Width.Value);
        AssertWidthWithinOnePhysicalPixel((play.ActualWidth - 8) * 3 / 7, skin.ActualWidth, skin);
        AssertWidthWithinOnePhysicalPixel((play.ActualWidth - 8) * 4 / 7, Element<Button>(window, "ControlsPresetButton").ActualWidth, skin);
        AssertWidthWithinOnePhysicalPixel(play.ActualWidth, skin.ActualWidth + 8 + Element<Button>(window, "ControlsPresetButton").ActualWidth, play);
        var preset = Element<Button>(window, "ControlsPresetButton");
        var presetText = VisualDescendants<TextBlock>(preset).Single(text => text.Text == "Пресет управления");
        Assert.Equal(TextWrapping.NoWrap, presetText.TextWrapping);
        Assert.True(presetText.DesiredSize.Width <= presetText.ActualWidth + 1);
        Assert.True(presetText.ActualHeight <= (double)window.FindResource("Font.LineHeightBody") + 1);
        var transfer = Element<Expander>(window, "WorldTransferExpander");
        var header = (FrameworkElement)transfer.Template.FindName("HeaderToggle", transfer);
        Assert.Equal(header.ActualHeight, preset.ActualHeight, 3);
        Assert.Equal(header.ActualHeight, skin.ActualHeight, 3);
        Assert.Equal(header.ActualHeight, Element<TextBox>(window, "PlayerNameTextBox").ActualHeight, 3);
        Assert.Equal(header.ActualHeight, Element<Button>(window, "ChangePlayerNameButton").ActualHeight, 3);
        Assert.Equal(header.ActualHeight, Element<TextBox>(window, "MemoryTextBox").ActualHeight, 3);
        Assert.Equal(header.ActualHeight, Element<CenteredDropDown>(window, "BuildComboBox").ActualHeight, 3);
        Assert.Equal(header.ActualHeight, Element<Button>(window, "DeleteBuildButton").ActualHeight, 3);
        var estimate = Element<TextBlock>(window, "MemoryEstimateText");
        Assert.Equal(TextWrapping.NoWrap, estimate.TextWrapping);
        Assert.True(estimate.DesiredSize.Width <= estimate.ActualWidth + 0.5, "The RAM recommendation is clipped.");
        Assert.Equal(Element<Grid>(window, "PresetSettingsRow").ColumnDefinitions[2].ActualWidth,
            preset.ActualWidth + preset.Margin.Left + preset.Margin.Right, 3);
        Assert.All(new[] { play, skin, preset }, button =>
        {
            Assert.Equal(HorizontalAlignment.Stretch, button.HorizontalAlignment);
            Assert.True(double.IsNaN(button.Width));
            Assert.True(double.IsPositiveInfinity(button.MaxWidth));
        });
    }

    private static void AssertWidthWithinOnePhysicalPixel(double expected, double actual, Visual visual)
    {
        // Grid star allocation and ScrollContentPresenter round independently.
        // Their shared logical edge can differ by at most one device pixel.
        var tolerance = 1 / VisualTreeHelper.GetDpi(visual).DpiScaleX + 0.000001;
        Assert.InRange(Math.Abs(expected - actual), 0, tolerance);
    }

    private static void AssertColumnRatio(Grid root)
    {
        Assert.Equal(2, root.ColumnDefinitions[0].Width.Value);
        Assert.Equal(1, root.ColumnDefinitions[2].Width.Value);
        // Layout rounding may allocate one physical pixel to either star column.
        var tolerance = 1.5 / VisualTreeHelper.GetDpi(root).DpiScaleX;
        Assert.InRange(Math.Abs(root.ColumnDefinitions[0].ActualWidth - 2 * root.ColumnDefinitions[2].ActualWidth), 0, tolerance);
    }

    private static void ExpandSections(Window window)
    {
        foreach (var name in SectionNames) Element<Expander>(window, name).IsExpanded = true;
    }

    private static void SettleNativeLayout(Window window)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            window.UpdateLayout();
            DrainDispatcher();
        }
        window.UpdateLayout();
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint window, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeClientRect bounds);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeClientRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    internal static void LayoutWindow(Window window, Size clientSize)
    {
        // The second pass settles viewport-dependent minimum heights using the
        // same controller as the live window, without starting Steam or networking.
        for (var pass = 0; pass < 2; pass++)
        {
            AdaptiveLauncherLayout.Apply(window, clientSize.Width, clientSize.Height);
            var content = (FrameworkElement)window.Content;
            content.Measure(clientSize);
            content.Arrange(new Rect(new Point(), clientSize));
            content.UpdateLayout();
        }
    }

    internal static void AssertProgressLinesFit(
        Window window, Size clientSize, string barName, string textName, string buttonName, IEnumerable<string> lines)
    {
        ExpandSections(window);
        LayoutWindow(window, clientSize);
        var bar = Element<FrameworkElement>(window, barName);
        var text = Element<TextBlock>(window, textName);
        Assert.True(bar.ActualWidth > 0);
        Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
        var available = bar.ActualWidth;
        var measurements = lines.Select(line =>
        {
            text.Text = line;
            text.Measure(new Size(available, double.PositiveInfinity));
            Assert.True(text.DesiredSize.Width <= available + 0.5,
                $"{textName}: {line} is wider than the {available:0.#} DIP bar");
            return (line, size: text.DesiredSize);
        }).ToList();
        var tallest = measurements.OrderByDescending(measurement => measurement.size.Height).First();
        text.Text = tallest.line;
        LayoutWindow(window, clientSize);
        var root = Element<Grid>(window, "RootGrid");
        var bounds = Bounds(text, root);
        var barBounds = Bounds(bar, root);
        Assert.True(bounds.Left >= barBounds.Left - 0.5 && bounds.Right <= barBounds.Right + 0.5,
            $"{textName}: {tallest.line} extends beyond the bar horizontally");
        Assert.True(bounds.Top >= barBounds.Top - 0.5 && bounds.Bottom <= barBounds.Bottom + 0.5,
            $"{textName}: {tallest.line} is clipped vertically");
        Assert.True(text.ActualHeight >= tallest.size.Height - 0.5,
            $"{textName}: the bar has not grown to show the wrapped caption");
        var buttonBounds = Bounds(Element<Button>(window, buttonName), root);
        Assert.True(Bounds(text, root).Right <= buttonBounds.Left + 0.5,
            $"{textName} overlaps {buttonName}");
    }

    private static T Element<T>(Window window, string name) where T : FrameworkElement =>
        Assert.IsAssignableFrom<T>(window.FindName(name));

    private static Rect Bounds(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(new Point(), element.RenderSize));

    internal static Window LoadWindow()
    {
        var program = Path.GetDirectoryName(FindRepositoryFile("Program", "MainWindow.xaml"))!;
        var app = File.ReadAllText(Path.Combine(program, "App.xaml"));
        var resources = Regex.Match(app, @"<Application\.Resources>(.*)</Application\.Resources>", RegexOptions.Singleline)
            .Groups[1].Value;
        // The tokens use namespaces App.xaml declares on its root; the window
        // has to declare the same ones for the spliced resources to parse.
        var namespaces = string.Concat(Regex.Matches(app, @"\sxmlns:(?!x=)[A-Za-z0-9]+=""[^""]*""")
            .Select(match => match.Value));

        var xaml = File.ReadAllText(Path.Combine(program, "MainWindow.xaml"));
        xaml = Regex.Replace(xaml, @"x:Class=""[^""]*""", namespaces);
        // Handlers live in the code-behind this loose copy does not have.
        xaml = Regex.Replace(
            xaml,
            @"\s(Click|Loaded|Closing|SizeChanged|SelectionChanged|TextChanged|PreviewTextInput|LostFocus|KeyDown|DataObject\.Pasting)=""[^""]*""",
            "");
        xaml = xaml.Replace(@"Icon=""Minecraft.ico""", "");
        // A loose XAML resolves clr-namespace against the assembly parsing it.
        xaml = xaml.Replace(@"clr-namespace:Minecraft""", @"clr-namespace:Minecraft;assembly=LANMinecraft""");
        xaml = xaml.Replace("<Window.Resources>", "<Window.Resources>" + resources);
        var window = (Window)XamlReader.Parse(xaml);
        Element<TextBlock>(window, "MemoryEstimateText").Text = "Рекомендуется от 13 ГБ";
        return window;
    }

    /// <summary>
    /// One apartment at a time.
    ///
    /// Loading the window loads CenteredDropDown, whose InitializeComponent
    /// goes through Application.LoadComponent to the same packaged resource
    /// every time. WPF tracks that part's open streams in a plain List and
    /// prunes it without a lock, so two of these threads running at once could
    /// have one of them fail inside List.RemoveAt - about one run in five, in a
    /// test that has nothing to do with either window.
    /// </summary>
    private static readonly Lock StaGate = new();

    /// <summary>WPF objects belong to a single-threaded apartment; xUnit's is not one.</summary>
    internal static T OnAStaThread<T>(Func<T> work)
    {
        using var _ = StaGate.EnterScope();
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(1)), "the layout pass did not finish in a minute");
        if (failure is not null) throw new InvalidOperationException("The window could not be measured.", failure);
        return result;
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(relativeParts)}");
    }
}
