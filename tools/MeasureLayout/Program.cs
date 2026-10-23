using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Minecraft;

internal static class Program
{
    private static string Repository = "";
    private static readonly BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FindRepository(string[] args)
    {
        if (args.Length > 0) return Path.GetFullPath(args[0]);
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Program", "Minecraft.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("Launcher repository was not found. Pass its path as the first argument.");
    }

    [STAThread]
    private static void Main(string[] args)
    {
        // Match the final launcher's process-local WPF renderer without running
        // its real startup handlers or initializing Steam/network services.
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Repository = FindRepository(args);
        var artifactRoot = Path.GetFullPath(Path.Combine(Repository, "artifacts", "MeasureLayout"));
        var output = Path.GetFullPath(Path.Combine(artifactRoot, args.Length > 1 ? args[1] : "current"));
        if (!output.StartsWith(artifactRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Measurements must remain inside artifacts/MeasureLayout.");
        Directory.CreateDirectory(output);
        var all = new List<object>();
        var scales = new[] { 1d, 1.25d, 1.5d, 2d };
        foreach (var viewport in new[] { ("minimum", 804d, 521d), ("initial", 820d, 549d), ("maximized", 1424d, 861d) })
        foreach (var mode in new[] { "collapsed", "expanded", "long-chat", "library", "pack-detail", "pack-mods" })
        foreach (var dpi in scales)
        {
            var expanded = mode == "expanded";
            if (args.Contains("--library-only") && mode is not ("library" or "pack-detail" or "pack-mods")) continue;
            if (args.Contains("--probe-library") && (viewport.Item1 != "minimum" || mode != "library" || dpi != 1)) continue;
            if (args.Contains("--probe") && (viewport.Item1 != "minimum" || mode != "collapsed" || dpi != 1)) continue;
            var window = LoadWindow();
            window.Tag = dpi;
            var content = (FrameworkElement)window.Content;
            VisualTreeHelper.SetRootDpi(window, new DpiScale(dpi, dpi));
            VisualTreeHelper.SetRootDpi(content, new DpiScale(dpi, dpi));
            Populate(window);
            if (mode is "library" or "pack-detail" or "pack-mods")
            {
                var view = (PackLibraryView)window.FindName("LibraryView");
                var catalog = new PackCatalogService(new AppPaths(Path.Combine(output, "catalog-cache")), new Logger(Path.Combine(output, "catalog.log")));
                view.SetCatalog(catalog.Packs);
                view.IsAdded = path => path == "LL8 Extended";
                foreach (UIElement child in ((Grid)window.FindName("RootGrid")).Children)
                    if (child != view) child.Visibility = Visibility.Hidden;
                view.Visibility = Visibility.Visible;
                if (mode is "pack-detail" or "pack-mods")
                {
                    var pack = catalog.Find("LL8 Extended")!;
                    pack.Mods.First(m => m.HasChildren).IsExpanded = true;
                    view.ShowDetails(pack);
                }
            }
            var expanders = LogicalDescendants<Expander>(window).Distinct().ToArray();
            foreach (var expander in expanders)
            {
                expander.IsExpanded = expanded;
                if (args.Contains("--force-top")) expander.VerticalAlignment = VerticalAlignment.Top;
            }
            if (expanded) ShowNews(window);
            LayoutWindow(window, new Size(viewport.Item2, viewport.Item3));
            DrainDispatcher();
            LayoutWindow(window, new Size(viewport.Item2, viewport.Item3));
            if (mode == "pack-mods")
            {
                var library = (PackLibraryView)window.FindName("LibraryView");
                ((ScrollViewer)library.FindName("DetailScroll")).ScrollToVerticalOffset(900);
                LayoutWindow(window, new Size(viewport.Item2, viewport.Item3));
                DrainDispatcher();
                LayoutWindow(window, new Size(viewport.Item2, viewport.Item3));
            }
            ChatBaseline? chatBaseline = null;
            double? chatEndOffset = null;
            if (mode == "long-chat" && window.FindName("ChatMessageTextBox") is TextBox chatInput)
            {
                chatBaseline = new ChatBaseline(((FrameworkElement)window.FindName("SidePanelCard")).ActualHeight,
                    ((ScrollViewer)window.FindName("BodyScroll")).ExtentHeight, chatInput.ActualHeight);
                var draft = string.Concat(Enumerable.Range(1, 160).Select(line => $"Тестовый вопрос {line}: поясни настройку сборки Minecraft.\n"));
                if (window.FindName("ChatComposerControl") is { } composer && composer.GetType().GetProperty("Prompt") is { } prompt)
                    prompt.SetValue(composer, draft);
                else chatInput.Text = draft;
                LayoutWindow(window, new Size(viewport.Item2, viewport.Item3));
                DrainDispatcher();
                LayoutWindow(window, new Size(viewport.Item2, viewport.Item3));
            }
            var id = $"{viewport.Item1}-{mode}-{dpi.ToString("0.##", CultureInfo.InvariantCulture)}x";
            var png = Path.Combine(output, id + ".png");
            Render(window, png, viewport.Item2, viewport.Item3, dpi);
            object? scrolledBody = null;
            if (expanded && window.FindName("BodyScroll") is ScrollViewer body)
            {
                var playBefore = RootBounds(window, "PlayButton");
                body.ScrollToEnd();
                DrainDispatcher();
                LayoutWindow(window, new Size(viewport.Item2, viewport.Item3));
                var playAfter = RootBounds(window, "PlayButton");
                scrolledBody = new { body.VerticalOffset, body.ScrollableHeight, PlayBefore = playBefore, PlayAfter = playAfter, Pinned = playBefore.Equals(playAfter) };
                Render(window, Path.Combine(output, id + "-bottom.png"), viewport.Item2, viewport.Item3, dpi);
                body.ScrollToTop();
                DrainDispatcher();
                LayoutWindow(window, new Size(viewport.Item2, viewport.Item3));
            }
            if (chatBaseline is not null && window.FindName("ChatMessageTextBox") is TextBox longInput)
            {
                // TextBox lazily formats long text. Repeat against the updated extent
                // instead of assuming the first ScrollToEnd used its final height.
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    longInput.ScrollToEnd();
                    DrainDispatcher();
                    LayoutWindow(window, new Size(viewport.Item2, viewport.Item3));
                    if (Math.Abs(longInput.VerticalOffset - Math.Max(0, longInput.ExtentHeight - longInput.ViewportHeight)) <= 1) break;
                }
                chatEndOffset = longInput.VerticalOffset;
                Render(window, Path.Combine(output, id + "-input-bottom.png"), viewport.Item2, viewport.Item3, dpi);
            }
            var report = MakeReport(window, id, viewport.Item2, viewport.Item3, dpi, expanders, chatBaseline, chatEndOffset, scrolledBody);
            File.WriteAllText(Path.Combine(output, id + ".json"), JsonSerializer.Serialize(report, JsonOptions));
            all.Add(report);
            Console.WriteLine($"Rendered {id}: {content.ActualWidth:0.##} × {content.ActualHeight:0.##}, {expanders.Length} expanders");
            window.Close();
        }
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(all, JsonOptions));
        var assembly = typeof(CenteredDropDown).Assembly;
        var publishedExecutable = args.FirstOrDefault(arg => arg.StartsWith("--exe=", StringComparison.Ordinal))?[6..]
            ?? Path.Combine(Repository, "Program.Tests", "PortabilityAudit", "Artifacts", "recheck-build", "LANMinecraft.exe");
        var inputs = new[] { assembly.Location, Path.Combine(Repository, "Program", "Minecraft.csproj"), Path.Combine(Repository, "Program", "App.xaml.cs"), Path.Combine(Repository, "Program", "App.xaml"), Path.Combine(Repository, "Program", "MainWindow.xaml"), Path.Combine(Repository, "Program", "AdaptiveLauncherLayout.cs"), Path.Combine(Repository, "Program", "CenteredDropDown.xaml"), Path.Combine(Repository, "Program", "ControlMotion.cs"), Path.Combine(Repository, "Program", "WindowsThemeService.cs"), Path.Combine(Repository, "Program", "MainWindow.xaml.cs"), Path.Combine(Repository, "Program", "ChatComposer.cs"), publishedExecutable };
        inputs = inputs.Concat(new[] { Assembly.GetExecutingAssembly().Location, Path.Combine(AppContext.BaseDirectory, "MeasureLayout.runtimeconfig.json"), Path.Combine(Repository, "tools", "MeasureLayout", "Program.cs"), Path.Combine(Repository, "tools", "MeasureLayout", "MeasureLayout.csproj") }).ToArray();
        var hashes = inputs.Where(File.Exists).Select(path => new { Path = path, SHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }).ToArray();
        var dirtyRectangleSwitchPresent = AppContext.TryGetSwitch("Switch.System.Windows.Media.MediaContext.DisableDirtyRectangles", out var dirtyRectanglesDisabled);
        var manifest = new { CapturedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), AssemblyConfiguration = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration, AssemblyVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, NativeControls = true, StartupHandlersRemoved = true, ProcessRenderMode = RenderOptions.ProcessRenderMode.ToString(), DirtyRectangleSwitchPresent = dirtyRectangleSwitchPresent, DirtyRectanglesDisabled = dirtyRectanglesDisabled, Notes = "Content-only native WPF render; window background included. No Steam/network startup. DPI set on native visual roots before measure. SoftwareOnly is set before WPF objects, matching the final launcher. Dirty-rectangle switch is supplied through the helper runtime configuration as in production. Native resize repaint, frame, Snap and live NameMarquee are not tested by this offscreen renderer.", Hashes = hashes };
        File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));
    }

    private static void Render(Window window, string path, double width, double height, double dpi)
    {
        var content = (FrameworkElement)window.Content;
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            // The library scrollbar paints into the right margin. An automatic
            // VisualBrush viewbox would squeeze those extra pixels into the root.
            var size = new Size(content.ActualWidth + content.Margin.Right, content.ActualHeight);
            var bounds = new Rect(new Point((width - content.ActualWidth) / 2, content.Margin.Top), size);
            context.DrawRectangle(new VisualBrush(content)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(new Point(content.Margin.Left, content.Margin.Top), size), Stretch = Stretch.Fill
            }, null, bounds);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi), (int)Math.Ceiling(height * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
    private static Window LoadWindow()
    {
        var program = Path.Combine(Repository, "Program");
        var app = File.ReadAllText(Path.Combine(program, "App.xaml"));
        var resources = Regex.Match(app, @"<Application\.Resources>(.*)</Application\.Resources>", RegexOptions.Singleline).Groups[1].Value;
        var namespaces = string.Concat(Regex.Matches(app, @"\sxmlns:(?!x=)[A-Za-z0-9]+=""[^""]*""").Select(match => match.Value));
        var xaml = File.ReadAllText(Path.Combine(program, "MainWindow.xaml"));
        xaml = Regex.Replace(xaml, @"x:Class=""[^""]*""", namespaces);
        xaml = Regex.Replace(xaml, @"\s(Click|Loaded|Closing|SizeChanged|SelectionChanged|TextChanged|PreviewTextInput|LostFocus|KeyDown|DataObject\.Pasting)=""[^""]*""", "");
        xaml = xaml.Replace(@"Icon=""Minecraft.ico""", "").Replace(@"clr-namespace:Minecraft""", @"clr-namespace:Minecraft;assembly=LANMinecraft""");
        resources = resources.Replace(@"clr-namespace:Minecraft""", @"clr-namespace:Minecraft;assembly=LANMinecraft""");
        xaml = xaml.Replace("<Window.Resources>", "<Window.Resources>" + resources);
        return (Window)XamlReader.Parse(xaml);
    }

    private static void LayoutWindow(Window window, Size client)
    {
        var controller = typeof(CenteredDropDown).Assembly.GetType("Minecraft.AdaptiveLauncherLayout");
        var apply = controller?.GetMethod("Apply", AnyStatic, [typeof(Window), typeof(double), typeof(double)]);
        for (var pass = 0; pass < 3; pass++)
        {
            apply?.Invoke(null, [window, client.Width, client.Height]);
            var content = (FrameworkElement)window.Content;
            if (window.Tag is double dpi) VisualTreeHelper.SetRootDpi(content, new DpiScale(dpi, dpi));
            content.Measure(client);
            content.Arrange(new Rect(new Point(), client));
            content.UpdateLayout();
        }
    }

    private static void Populate(Window window)
    {
        if (window.FindName("PlayerNameTextBox") is TextBox player) player.Text = "MarkZamore";
        if (window.FindName("MemoryTextBox") is TextBox memory) memory.Text = "8";
        if (window.FindName("MemoryEstimateText") is TextBlock estimate) estimate.Text = "Рекомендуется от 13 ГБ";
        if (window.FindName("BugReportMessageTextBox") is TextBox report) report.Text = "Опишите проблему: что произошло и что должно было произойти.\nТекст сохраняется при сворачивании секции.";
        foreach (var name in new[] { "BuildComboBox", "WorldComboBox", "OnlinePlayerComboBox", "DiagnosticLogTargetComboBox" })
        {
            if (window.FindName(name) is not CenteredDropDown dropdown) continue;
            var text = name switch { "BuildComboBox" => "Minecraft 1.21.1 NeoForge", "WorldComboBox" => "Мой мир", "OnlinePlayerComboBox" => "Игрок в локальной сети", _ => "Разработчик" };
            var item = new SampleItem(text, text);
            dropdown.ItemsSource = new[] { item };
            dropdown.SelectedItem = item;
        }
        if (window.FindName("ChangelogList") is ItemsControl changes)
            changes.ItemsSource = ChangelogService.Load().Take(12).Select(entry => new { Title = $"Версия {entry.Version}", entry.Text }).ToArray();
    }

    private static void ShowNews(Window window)
    {
        if (window.FindName("ChangelogPanel") is Grid changelog) changelog.Visibility = Visibility.Visible;
        if (window.FindName("ChatPanel") is Grid chat) chat.Visibility = Visibility.Collapsed;
        if (window.FindName("SidePanelTitle") is TextBlock title) title.Text = "Что нового";
        if (window.FindName("SidePanelToggleButton") is Button toggle) toggle.Content = "Чат";
    }
    private static object MakeReport(Window window, string id, double width, double height, double dpi, Expander[] expanders,
        ChatBaseline? chatBaseline, double? chatEndOffset, object? scrolledBody)
    {
        var root = (Grid)window.FindName("RootGrid");
        var names = Regex.Matches(File.ReadAllText(Path.Combine(Repository, "Program", "MainWindow.xaml")), @"x:Name=""([^""]+)""").Select(m => m.Groups[1].Value).Distinct();
        var controls = names.Select(name => window.FindName(name)).OfType<FrameworkElement>().Select(element =>
        {
            Rect? bounds = null;
            try { bounds = element.TransformToAncestor(root).TransformBounds(new Rect(new Point(), element.RenderSize)); } catch (InvalidOperationException) { }
            return new { element.Name, Type = element.GetType().Name, Dpi = VisualTreeHelper.GetDpi(element).DpiScaleX, Visible = element.IsVisible, Visibility = element.Visibility.ToString(), Width = element.ActualWidth, Height = element.ActualHeight, DesiredWidth = element.DesiredSize.Width, DesiredHeight = element.DesiredSize.Height, Bounds = bounds.HasValue ? new { bounds.Value.X, bounds.Value.Y, bounds.Value.Width, bounds.Value.Height } : null, GridRow = Grid.GetRow(element), GridColumn = Grid.GetColumn(element), GridRowSpan = Grid.GetRowSpan(element), GridColumnSpan = Grid.GetColumnSpan(element), Font = element is Control control ? control.FontFamily.ToString() : null, FontSize = element is Control c ? c.FontSize : (double?)null };
        }).ToArray();
        var headerMetrics = expanders.Select(e => new { e.Name, e.ActualHeight, Dpi = VisualTreeHelper.GetDpi(e).DpiScaleX, HeaderToggleDpi = VisualTreeHelper.GetDpi((FrameworkElement)e.Template.FindName("HeaderToggle", e)).DpiScaleX, HeaderToggleHeight = (e.Template.FindName("HeaderToggle", e) as FrameworkElement)?.ActualHeight, HeaderToggleDesiredHeight = (e.Template.FindName("HeaderToggle", e) as FrameworkElement)?.DesiredSize.Height }).ToArray();
        var texts = VisualDescendants<TextBlock>(root).Where(t => !string.IsNullOrEmpty(t.Text)).Select(t => new { t.Text, Width = t.ActualWidth, Height = t.ActualHeight, DesiredWidth = t.DesiredSize.Width, DesiredHeight = t.DesiredSize.Height, Font = t.FontFamily.ToString(), t.FontSize, Trimming = t.TextTrimming.ToString(), Wrapping = t.TextWrapping.ToString() }).ToArray();
        var scrolls = LogicalDescendants<ScrollViewer>(window).Select(s => new { s.Name, s.ActualWidth, s.ActualHeight, s.ViewportWidth, s.ViewportHeight, s.ExtentWidth, s.ExtentHeight, s.ScrollableWidth, s.ScrollableHeight }).ToArray();
        double Bottom(string name)
        {
            var element = (FrameworkElement)window.FindName(name);
            return element.TransformToAncestor(root).TransformBounds(new Rect(new Point(), element.RenderSize)).Bottom;
        }
        var anchors = new { SidebarBottom = Bottom("SidePanelCard"), UpdateBarBottom = Bottom("UpdateProgressBar"), Difference = Bottom("SidePanelCard") - Bottom("UpdateProgressBar") };
        var input = window.FindName("ChatMessageTextBox") as TextBox;
        var longChat = chatBaseline is null || input is null ? null : new
        {
            Baseline = chatBaseline, InputHeight = input.ActualHeight, input.ExtentHeight,
            input.ViewportHeight, input.VerticalOffset, EndOffset = chatEndOffset,
            ReachedActualEnd = chatEndOffset is not null && Math.Abs(chatEndOffset.Value - Math.Max(0, input.ExtentHeight - input.ViewportHeight)) <= 1,
            CardHeight = ((FrameworkElement)window.FindName("SidePanelCard")).ActualHeight,
            BodyExtentHeight = ((ScrollViewer)window.FindName("BodyScroll")).ExtentHeight,
            VerticalScrollBar = input.VerticalScrollBarVisibility.ToString()
        };
        return new { id, WindowDefaults = new { window.Width, window.Height, window.MinWidth, window.MinHeight, ResizeMode = window.ResizeMode.ToString(), WindowStyle = window.WindowStyle.ToString() }, ClientWidth = width, ClientHeight = height, DpiScale = dpi, ActualRootDpiScale = VisualTreeHelper.GetDpi(root).DpiScaleX, RootWidth = root.ActualWidth, RootHeight = root.ActualHeight, RootMargin = new { root.Margin.Left, root.Margin.Top, root.Margin.Right, root.Margin.Bottom }, RootMaxWidth = double.IsInfinity(root.MaxWidth) ? (double?)null : root.MaxWidth, RootHorizontalAlignment = root.HorizontalAlignment.ToString(), Columns = root.ColumnDefinitions.Select(c => new { Width = c.Width.ToString(), c.ActualWidth, c.MinWidth, MaxWidth = double.IsInfinity(c.MaxWidth) ? (double?)null : c.MaxWidth }).ToArray(), Font = window.FindResource("Font.Family").ToString(), Expanders = expanders.Select(e => new { e.Name, e.IsExpanded, Header = e.Header?.ToString() }).ToArray(), Controls = controls, Headers = headerMetrics, Text = texts, Scrolls = scrolls, Anchors = anchors, LongChat = longChat, ScrolledBody = scrolledBody };
    }

    private static IEnumerable<DependencyObject> LogicalAncestors(DependencyObject child)
    {
        for (var parent = LogicalTreeHelper.GetParent(child); parent is not null; parent = LogicalTreeHelper.GetParent(parent))
            yield return parent;
    }

    private static object RootBounds(Window window, string name)
    {
        var root = (FrameworkElement)window.FindName("RootGrid");
        var element = (FrameworkElement)window.FindName(name);
        var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(new Point(), element.RenderSize));
        return new { bounds.X, bounds.Y, bounds.Width, bounds.Height };
    }

    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var next in LogicalDescendants<T>(child)) yield return next;
        }
    }
    private static IEnumerable<T> VisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var next in VisualDescendants<T>(child)) yield return next;
        }
    }
    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    public sealed record SampleItem(string Name, string DisplayName);
    private sealed record ChatBaseline(double CardHeight, double BodyExtentHeight, double InputHeight);
}













