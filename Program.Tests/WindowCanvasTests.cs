using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace Minecraft.Tests;

/// <summary>
/// Measures the responsive launcher with its own controls and bundled font.
/// A finite client area is essential: measuring at infinity hides clipping
/// and prevents ScrollViewer from establishing a real viewport.
/// </summary>
public sealed class WindowCanvasTests
{
    [Theory]
    [InlineData(464, 321, false, true)]
    [InlineData(581, 681, false, true)]
    [InlineData(582, 681, false, false)]
    [InlineData(704, 501, false, false)]
    [InlineData(851, 681, false, false)]
    [InlineData(852, 681, true, false)]
    [InlineData(944, 1001, true, false)]
    [InlineData(1904, 1001, true, false)]
    public void TheLayout_ReflowsWithoutShrinkingOrLosingControls(
        double width, double height, bool wide, bool compact)
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            LayoutWindow(window, new Size(width, height));
            var root = Element<Grid>(window, "RootGrid");
            var main = Element<Grid>(window, "MainPanel");
            var side = Element<Grid>(window, "SidePanel");
            var body = Element<ScrollViewer>(window, "BodyScroll");
            var bodyGrid = Element<Grid>(window, "BodyGrid");
            var settings = Element<Grid>(window, "SettingsGrid");
            var host = Element<Border>(window, wide ? "WideSideHost" : "InlineSideHost");

            Assert.Same(host, VisualTreeHelper.GetParent(side));
            var mainBounds = Bounds(main, root);
            var sideBounds = Bounds(side, root);
            if (wide)
            {
                Assert.True(sideBounds.Left >= mainBounds.Right,
                    $"the side panel overlaps the main controls at {width} DIP");
                Assert.True(sideBounds.Top < mainBounds.Bottom);
            }
            else
            {
                Assert.True(sideBounds.Top >= mainBounds.Bottom,
                    $"the side panel must follow the main controls at {width} DIP");
            }

            var rowNames = new[] { "NameSettingsRow", "BuildSettingsRow", "MemorySettingsRow", "PresetSettingsRow" };
            var rows = rowNames.Select(name => Element<Grid>(window, name)).ToArray();
            if (compact)
            {
                Assert.Equal(new[] { 0, 1, 2, 3 }, rows.Select(Grid.GetRow).ToArray());
                Assert.All(rows, row => Assert.Equal(0, Grid.GetColumn(row)));
                Assert.Equal(4, settings.RowDefinitions.Count);
            }
            else
            {
                Assert.Equal(new[] { 0, 1, 0, 1 }, rows.Select(Grid.GetRow).ToArray());
                Assert.Equal(new[] { 0, 0, 1, 1 }, rows.Select(Grid.GetColumn).ToArray());
            }

            // Every body control stays within the horizontal extent. Vertical
            // overflow is deliberate and remains reachable through BodyScroll.
            string[] bodyControls =
            [
                "PlayerNameTextBox", "ChangePlayerNameButton", "BuildComboBox", "SkinButton",
                "MemoryTextBox", "DeleteBuildButton", "ControlsPresetButton", "WorldComboBox",
                "DeleteWorldButton", "OnlinePlayerComboBox", "TransferButton",
                "DiagnosticLogTargetComboBox", "SendBugReportButton", "BugReportMessageTextBox",
                "DiagnosticLogStatusText"
            ];
            foreach (var name in bodyControls)
            {
                var element = Element<FrameworkElement>(window, name);
                var bounds = Bounds(element, bodyGrid);
                Assert.Equal(Visibility.Visible, element.Visibility);
                Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0, $"{name} has no usable size");
                Assert.True(bounds.Left >= -0.5 && bounds.Right <= bodyGrid.ActualWidth + 0.5,
                    $"{name} is clipped horizontally at {width} DIP: {bounds}, body width {bodyGrid.ActualWidth}");
                var reachableHeight = Math.Max(body.ExtentHeight, body.ViewportHeight);
                Assert.True(bounds.Top >= -0.5 && bounds.Bottom <= reachableHeight + 0.5,
                    $"{name} lies outside the reachable content: {bounds}, height {reachableHeight}");
            }
            Assert.True(body.ViewportHeight > 0 && body.ViewportWidth > 0);
            Assert.True(body.ScrollableWidth <= 0.5, "the launcher must not require horizontal scrolling");

            var play = Element<Button>(window, "PlayButton");
            var footer = Element<Grid>(window, "FooterGrid");
            Assert.True(Bounds(play, root).Top >= -0.5);
            Assert.True(Bounds(footer, root).Bottom <= root.ActualHeight + 0.5,
                "the pinned footer runs out of the client area");
            Assert.True(Bounds(body, root).Bottom <= Bounds(footer, root).Top + 0.5);
            foreach (var name in new[] { "UpdateButton", "RetrySteamButton", "SidePanelToggleButton" })
            {
                var bounds = Bounds(Element<FrameworkElement>(window, name), root);
                Assert.True(bounds.Left >= -0.5 && bounds.Right <= root.ActualWidth + 0.5,
                    $"the footer control {name} is clipped at {width} DIP");
                Assert.True(bounds.Bottom <= root.ActualHeight + 0.5);
            }

            var player = Element<TextBox>(window, "PlayerNameTextBox");
            Assert.Equal((double)window.FindResource("Font.SizeBody"), player.FontSize);
            var transform = player.TransformToAncestor(root);
            Assert.Equal(100, transform.Transform(new Point(100, 0)).X - transform.Transform(new Point(0, 0)).X, 3);
            return true;
        });
    }

    [Fact]
    public void TheWindow_CanResizeToASnapFriendlyMinimum()
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            Assert.Equal(480, window.MinWidth);
            Assert.Equal(360, window.MinHeight);
            Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
            var root = Element<Grid>(window, "RootGrid");
            Assert.True(double.IsNaN(root.Width) && double.IsNaN(root.Height));
            Assert.DoesNotContain("<Viewbox", File.ReadAllText(FindRepositoryFile("Program", "MainWindow.xaml")), StringComparison.Ordinal);
            return true;
        });
    }

    [Fact]
    public void AShortWindow_KeepsTheEntireBodyReachableByScrolling()
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            var size = new Size(464, 321);
            LayoutWindow(window, size);
            var body = Element<ScrollViewer>(window, "BodyScroll");
            Assert.True(body.ScrollableHeight > 0);
            body.ScrollToEnd();
            LayoutWindow(window, size);
            Assert.Equal(body.ScrollableHeight, body.VerticalOffset, 1);
            var side = Bounds(Element<Grid>(window, "SidePanel"), body);
            Assert.True(side.Bottom <= body.ActualHeight + 0.5,
                "scrolling to the end must expose the lower edge of the side panel");
            return true;
        });
    }

    [Fact]
    public void ChangingLayout_PreservesTheControlsAndTheirState()
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            var player = Element<TextBox>(window, "PlayerNameTextBox");
            var report = Element<TextBox>(window, "BugReportMessageTextBox");
            var side = Element<Grid>(window, "SidePanel");
            player.Text = "MarkZamore";
            report.Text = "Описание проблемы остается при смене размера";
            Element<Grid>(window, "ChangelogPanel").Visibility = Visibility.Visible;
            Element<Grid>(window, "ChatPanel").Visibility = Visibility.Collapsed;
            foreach (var size in new[] { new Size(944, 681), new Size(464, 321), new Size(704, 501), new Size(944, 1001) })
            {
                LayoutWindow(window, size);
                Assert.Same(player, window.FindName("PlayerNameTextBox"));
                Assert.Same(report, window.FindName("BugReportMessageTextBox"));
                Assert.Same(side, window.FindName("SidePanel"));
                Assert.Equal("MarkZamore", player.Text);
                Assert.Equal("Описание проблемы остается при смене размера", report.Text);
                Assert.Equal(Visibility.Visible, Element<Grid>(window, "ChangelogPanel").Visibility);
                Assert.Equal(Visibility.Collapsed, Element<Grid>(window, "ChatPanel").Visibility);
            }
            return true;
        });
    }

    [Fact]
    public void MoreHeight_ExpandsTheReportAndSidePanelWhileControlsKeepTheirSize()
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            var report = Element<TextBox>(window, "BugReportMessageTextBox");
            var play = Element<Button>(window, "PlayButton");
            var side = Element<Grid>(window, "SidePanel");
            LayoutWindow(window, new Size(944, 681));
            var reportHeight = report.ActualHeight;
            var playHeight = play.ActualHeight;
            var sideHeight = side.ActualHeight;
            LayoutWindow(window, new Size(944, 1001));
            Assert.True(report.ActualHeight > reportHeight + 200,
                "additional height should make the report useful instead of leaving a blank band");
            Assert.True(side.ActualHeight > sideHeight + 200);
            Assert.Equal(playHeight, play.ActualHeight, 3);
            Assert.Equal((double)window.FindResource("Font.SizeBody"), report.FontSize);
            return true;
        });
    }

    [Fact]
    public void TheNativeClient_ReflowsAfterMaximizeAndRestoreWithoutLosingSelection()
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
            // Exercise the real HWND while keeping the test invisible and
            // inactive. Content measurements alone cannot catch a stale root
            // width keeping the wide columns after a native restore.
            window.Width = 720;
            window.Height = 540;
            window.Opacity = 0;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(window).Handle;
                const int extendedStyleIndex = -20;
                const int noActivate = 0x08000000;
                _ = SetWindowLong(handle, extendedStyleIndex, GetWindowLong(handle, extendedStyleIndex) | noActivate);
            };
            window.SizeChanged += (_, _) => AdaptiveLauncherLayout.Apply(window);
            var foreground = GetForegroundWindow();
            window.Activated += (_, _) => { if (foreground != 0) _ = SetForegroundWindow(foreground); };
            try
            {
                window.Show();
                SettleNativeLayout(window);
                var report = Element<TextBox>(window, "BugReportMessageTextBox");
                report.Text = "Сохранить выделенный текст после изменения окна";
                report.Select(10, 10);
                FocusManager.SetFocusedElement(window, report);
                var inlineHost = Element<Border>(window, "InlineSideHost");
                var wideHost = Element<Border>(window, "WideSideHost");
                var side = Element<Grid>(window, "SidePanel");
                Assert.Same(inlineHost, VisualTreeHelper.GetParent(side));

                window.WindowState = WindowState.Maximized;
                if (foreground != 0) _ = SetForegroundWindow(foreground);
                SettleNativeLayout(window);
                Assert.Same(wideHost, VisualTreeHelper.GetParent(side));
                window.WindowState = WindowState.Normal;
                if (foreground != 0) _ = SetForegroundWindow(foreground);
                SettleNativeLayout(window);
                Assert.Equal(720, window.Width, 1);
                Assert.Same(inlineHost, VisualTreeHelper.GetParent(side));
                Assert.True(Element<Grid>(window, "RootGrid").ActualWidth < 820,
                    "restoring a narrow native window must release the wide column minimums");

                window.Width = 960;
                SettleNativeLayout(window);
                Assert.Same(wideHost, VisualTreeHelper.GetParent(side));
                Assert.Equal("Сохранить выделенный текст после изменения окна", report.Text);
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
    [InlineData(944, 681)]
    [InlineData(704, 501)]
    [InlineData(464, 321)]
    public void ALongReport_ScrollsInsideItsFieldWithoutGrowingThePage(double width, double height)
    {
        OnAStaThread(() =>
        {
            var window = LoadWindow();
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
            Assert.True(report.VerticalOffset > 0, "the long description must have its own scrollable viewport");
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
            var dropdown = Element<CenteredDropDown>(window, controlName);
            const string caption = "Выбранный элемент для проверки цвета";
            var item = new { Name = caption, DisplayName = caption };
            dropdown.ItemsSource = new[] { item };
            dropdown.SelectedItem = item;
            var size = new Size(944, 681);
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
        // The typeface's pack URI is served only inside an Application; the
        // same files are read from the repository, so the metrics are the real ones.
        var fonts = new Uri(Path.Combine(program, "Fonts") + Path.DirectorySeparatorChar).AbsoluteUri;
        resources = resources.Replace(
            "pack://application:,,,/LANMinecraft;component/Fonts/#Montserrat", fonts + "#Montserrat");
        xaml = xaml.Replace("<Window.Resources>", "<Window.Resources>" + resources);
        return (Window)XamlReader.Parse(xaml);
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
