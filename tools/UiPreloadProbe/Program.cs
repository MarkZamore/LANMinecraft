using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Minecraft;

internal static class Probe
{
    [STAThread]
    static void Main(string[] args)
    {
        AppContext.SetSwitch("Switch.System.Windows.Media.MediaContext.DisableDirtyRectangles", !args.Contains("--incremental"));
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var output = new List<object>();
        var repository = Path.GetFullPath(args[0]);
        var destination = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var resourceSource = File.ReadAllText(Path.Combine(repository, "Program/App.xaml"));
        var resourceBody = Regex.Match(resourceSource, @"<Application\.Resources>(.*)</Application\.Resources>", RegexOptions.Singleline).Groups[1].Value;
        app.Resources = (ResourceDictionary)XamlReader.Parse("<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:sys=\"clr-namespace:System;assembly=System.Runtime\" xmlns:bridge=\"clr-namespace:Minecraft;assembly=LANMinecraft\">" + resourceBody + "</ResourceDictionary>");
        if (args.Contains("--dialogs-close"))
        {
            var cache = typeof(MainWindow).Assembly.GetType("Minecraft.DialogPreloadCache", true)!;
            foreach (var name in new[] { "PreparePreset", "Prepare" })
                foreach (var step in (IEnumerable<Action>)cache.GetMethod(name)!.Invoke(null, null)!) step();
            var before = app.Windows.Count;
            cache.GetMethod("Clear")!.Invoke(null, null);
            var after = app.Windows.Count;
            output.Add(new { preparedWindows = before, remainingWindows = after });
            if (before != 5 || after != 0) throw new InvalidOperationException("Prepared dialogs outlived their cache.");
            app.Shutdown();
        }
        else if (args.Contains("--first-frame"))
        {
            var clock = Stopwatch.StartNew();
            var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false };
            bool smallIcon = false, bigIcon = false;
            window.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                smallIcon = SendMessage(hwnd, 0x7F, new IntPtr(0), IntPtr.Zero) != IntPtr.Zero;
                bigIcon = SendMessage(hwnd, 0x7F, new IntPtr(1), IntPtr.Zero) != IntPtr.Zero;
            };
            window.ContentRendered += (_, _) =>
            {
                output.Add(new { firstFrameMilliseconds = clock.Elapsed.TotalMilliseconds, smallIcon, bigIcon });
                File.WriteAllText(destination, JsonSerializer.Serialize(output));
                window.Close();
            };
            window.Show();
            while (output.Count == 0 && clock.Elapsed.TotalSeconds < 10) Pump(16);
            Pump(100); app.Shutdown();
        }
        else
        {
            RunScrollProbe(args, repository, destination, output);
            app.Shutdown();
        }
        File.WriteAllText(destination, JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void RunScrollProbe(string[] args, string repository, string destination, List<object> output)
    {
        var paths = new AppPaths(Path.Combine(Path.GetDirectoryName(destination)!, "fixture")); paths.Ensure();
        var catalog = new PackCatalogService(paths, new Logger(paths.LogFile));
        var repeats = int.Parse(Option(args, "--repeat") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
        var ticks = int.Parse(Option(args, "--ticks") ?? "60", System.Globalization.CultureInfo.InvariantCulture);
        var requestedCount = int.Parse(Option(args, "--overview-count") ?? "24", System.Globalization.CultureInfo.InvariantCulture);
        if (repeats < 1 || repeats > 10 || ticks < 1 || ticks > 600 || requestedCount < 1 || requestedCount > 500)
            throw new ArgumentException("Probe repeat/tick/card count is outside its bounded range.");
#if BASELINE
        var modes = new[] { false };
#else
        var selectedMode = Option(args, "--backdrop") ?? "prepared";
        var modes = selectedMode switch { "original" => new[] { false }, "prepared" => new[] { true },
            "both" => new[] { false, true }, _ => throw new ArgumentException("--backdrop is original, prepared, or both.") };
#endif
        var scales = args.Contains("--native-dpi") ? new[] { double.NaN } : new[] { 1d, 1.5d, 2d };
        var screens = (Option(args, "--screen") ?? "library") switch
        {
            "main" => new[] { "main" }, "overview" => new[] { "overview" }, "detail" => new[] { "detail" },
            "library" => new[] { "overview", "detail" }, "all" => new[] { "main", "overview", "detail" },
            _ => throw new ArgumentException("--screen is main, overview, detail, library, or all.")
        };
        var diagnostic = Option(args, "--diagnostic") ?? "none";
        var surfaceChoices = (Option(args, "--surfaces") ?? "prepared") switch
        {
            "original" => new[] { false }, "prepared" => new[] { true }, "both" => new[] { false, true },
            _ => throw new ArgumentException("--surfaces is original, prepared, or both.")
        };
#if BASELINE
        var themeClear = Option(args, "--theme-clear") ?? "none";
        if (themeClear != "none") throw new ArgumentException("A legacy baseline supports only --theme-clear none.");
#else
        var themeClear = Option(args, "--theme-clear") ?? "transparent";
#endif
        if (themeClear is not ("none" or "transparent" or "opaque")) throw new ArgumentException("--theme-clear is none, transparent, or opaque.");
        double? anchorLeft = Option(args, "--left") is { } left ? double.Parse(left, System.Globalization.CultureInfo.InvariantCulture) : null;
        double? anchorTop = Option(args, "--top") is { } top ? double.Parse(top, System.Globalization.CultureInfo.InvariantCulture) : null;
        if (anchorLeft is not null || anchorTop is not null) { anchorLeft ??= 200; anchorTop ??= 120; }
        for (var repeat = 0; repeat < repeats; repeat++)
        foreach (var requestedScale in scales)
        foreach (var screen in screens)
        foreach (var prepareBackdrop in repeat % 2 == 0 ? modes : modes.Reverse())
        foreach (var prepareSurfaces in !prepareBackdrop ? new[] { false } : repeat % 2 == 0 ? surfaceChoices : surfaceChoices.Reverse())
        {
            var detail = screen == "detail";
            var main = screen == "main";
            var window = LoadWindow(repository);
            window.Topmost = !args.Contains("--allow-occluded");
#if !BASELINE
            if (themeClear != "none") window.SourceInitialized += (_, _) => typeof(MainWindow).Assembly
                .GetType("Minecraft.WindowsThemeService", true)!.GetMethod("Attach",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [window, themeClear == "opaque"]);
#endif
            if (anchorLeft is not null)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = anchorLeft.Value; window.Top = anchorTop!.Value;
            }
            window.ShowInTaskbar = false; window.ShowActivated = false;
            if (Option(args, "--width") is { } width) window.Width = double.Parse(width, System.Globalization.CultureInfo.InvariantCulture);
            if (Option(args, "--height") is { } height) window.Height = double.Parse(height, System.Globalization.CultureInfo.InvariantCulture);
            var view = (PackLibraryView)window.FindName("LibraryView");
            if (!main)
            {
                foreach (UIElement child in ((Grid)window.FindName("RootGrid")).Children)
                    if (child != view) child.Visibility = Visibility.Hidden;
                view.Visibility = Visibility.Visible;
            }
            else
            {
                view.Visibility = Visibility.Collapsed;
                ((Expander)window.FindName("WorldTransferExpander")).IsExpanded = true;
                ((Expander)window.FindName("ReportExpander")).IsExpanded = true;
                ((TextBox)window.FindName("PlayerNameTextBox")).Text = "Probe player";
                ((TextBox)window.FindName("MemoryTextBox")).Text = "8192";
                ((TextBox)window.FindName("BugReportMessageTextBox")).Text = "Reproducible isolated launcher fixture.";
            }
            var failures = new List<string>();
            var removedTransparentBackgrounds = 0;
            EventHandler? diagnosticLayout = null;
            ClipSuppression? suppressedClips = null;
#if !BASELINE
            using var queue = new UiPreloadQueue(window.Dispatcher, error => failures.Add(error.ToString()));
            view.Preloader = queue;
            using var backdrop = prepareBackdrop ? PreparedWindowBackdrop.Attach(window, queue) : null;
            if (backdrop is not null) backdrop.PrepareSurfaceBackgrounds = prepareSurfaces;
#endif
            try
            {
                window.Show();
                anchorLeft ??= window.Left; anchorTop ??= window.Top;
                ((HwndTarget)PresentationSource.FromVisual(window)!.CompositionTarget).RenderMode = RenderMode.SoftwareOnly;
                if (args.Contains("--maximized") || args.Contains("--restore"))
                {
                    window.WindowState = WindowState.Maximized;
                    Pump(100);
                    if (args.Contains("--restore")) { window.WindowState = WindowState.Normal; Pump(100); }
                }
                if (!double.IsNaN(requestedScale)) VisualTreeHelper.SetRootDpi(window, new DpiScale(requestedScale, requestedScale));
                window.UpdateLayout();
#if !BASELINE
                backdrop?.Refresh();
#endif
                // More than one viewport of fixed-size cards is essential. Six
                // real cards can fit one row when maximized and never scroll.
                var columns = Math.Max(1, (int)Math.Ceiling(Math.Max(1, view.ActualWidth) / 240));
                var rows = Math.Max(2, (int)Math.Ceiling(Math.Max(1, view.ActualHeight) / 180) + 2);
                var fixtureCount = Math.Min(500, Math.Max(requestedCount, columns * rows));
                var packs = Fixture(catalog.Packs, fixtureCount, Math.Max(20, (int)Math.Ceiling(view.ActualHeight / 16) + 20));
                if (!main) view.SetCatalog(packs);
                if (detail) view.ShowDetails(packs[0]);
                var clock = Stopwatch.StartNew();
                var preloadInputs = new List<double>();
#if !BASELINE
                bool Ready() => (main || view.IsOverviewReady) &&
                    (!detail || queue.IsComplete("detail:" + packs[0].Entry.Id)) &&
                    (!prepareBackdrop || backdrop!.IsReady && !backdrop.IsSurfacePreparationPending);
                while (!Ready() && failures.Count == 0 && backdrop?.LastError is null && clock.Elapsed.TotalSeconds < 30)
                {
                    PostInputSample(window.Dispatcher, preloadInputs);
                    Pump(16);
                }
                var ready = Ready();
                var pendingKeys = queue.PendingKeys;
                var executedSteps = queue.ExecutedSteps;
#else
                Pump(200);
                var ready = true;
                var pendingKeys = Array.Empty<string>();
                long executedSteps = 0;
#endif
                window.UpdateLayout(); Pump(200);
                var preloadMilliseconds = clock.Elapsed.TotalMilliseconds;
                var imagesSettled = Stopwatch.StartNew();
#if !BASELINE
                while ((Descendants(window).OfType<PreparedImage>().Any(image => image.IsPreparationPending) ||
                    backdrop?.IsSurfacePreparationPending == true) && imagesSettled.Elapsed.TotalSeconds < 10)
                    Pump(16);
#endif
                Pump(1000);
                var imageSettleMilliseconds = imagesSettled.Elapsed.TotalMilliseconds;
                var viewer = main ? (ScrollViewer)window.FindName("BodyScroll") :
                    (ScrollViewer)view.FindName(detail ? "DetailScroll" : "CatalogScroll")!;
                if (main)
                {
                    // Keep the real controls and their original geometry. Extra
                    // trailing fixture space supplies actual scroll movement even
                    // when the monitor can fit every expanded launcher section.
                    ((Grid)window.FindName("BodyGrid")).MinHeight = Math.Max(viewer.ViewportHeight * 2, viewer.ExtentHeight);
                    window.UpdateLayout(); Pump(100);
                }
                if (diagnostic == "flat") window.Background = Brushes.DarkSlateGray;
                else if (diagnostic == "no-images")
                    foreach (var element in Descendants(window).OfType<Image>()) element.Visibility = Visibility.Hidden;
                else if (diagnostic == "no-transparent")
                {
                    foreach (var element in Descendants(window).Prepend(window))
                    {
                        var property = element switch { Border => Border.BackgroundProperty, Panel => Panel.BackgroundProperty,
                            Control => Control.BackgroundProperty, _ => null };
                        if (property is not null && element.GetValue(property) is SolidColorBrush brush && brush.Color.A == 0)
                        { element.SetCurrentValue(property, null); removedTransparentBackgrounds++; }
                    }
                }
                else if (diagnostic == "no-clips")
                {
                    // Observe the DP itself: an Arrange pass can occur again
                    // after Rendering and reinstall a clip after LayoutUpdated.
                    suppressedClips = new ClipSuppression();
                    diagnosticLayout = (_, _) => suppressedClips.Observe(window);
                    window.LayoutUpdated += diagnosticLayout;
                    diagnosticLayout(null, EventArgs.Empty);
                }
                else if (diagnostic != "none") throw new ArgumentException("Unknown diagnostic rendering mode.");
                window.UpdateLayout(); Pump(100);
                var cachedElements = args.Contains("--bounded-cache") ? CacheBoundedElements(window) : 0;
                if (cachedElements > 0) Pump(1000);
                var overflow = viewer.ScrollableHeight;
                if (!ready || overflow <= 1)
                    throw new InvalidOperationException($"The probe was not ready or had no genuine overflow: ready={ready}, overflow={overflow}, pending={string.Join(',', pendingKeys)}.");
                var frameIntervals = new List<double>();
                var scrollingInputs = new List<double>();
                var offsets = new List<double>();
                var pixelPhases = new List<double>();
                var clipPropertySamples = new List<double>();
                var roundedVisualClipSamples = new List<double>();
                var phaseImage = Descendants(viewer).OfType<Image>().FirstOrDefault(image => image.IsVisible);
                long previous = 0;
                var previousFrame = TimeSpan.MinValue;
                var layoutUpdates = 0;
                EventHandler layout = (_, _) => layoutUpdates++;
                EventHandler rendering = (_, e) =>
                {
                    var stamp = ((RenderingEventArgs)e).RenderingTime;
                    if (stamp == previousFrame) return;
                    previousFrame = stamp;
                    var now = Stopwatch.GetTimestamp();
                    if (previous != 0) frameIntervals.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
                    previous = now;
                    offsets.Add(viewer.VerticalOffset);
                    var clippedChildren = RoundedClipChildren(window).ToArray();
                    clipPropertySamples.Add(clippedChildren.Count(child => child!.Clip is not null));
                    roundedVisualClipSamples.Add(clippedChildren.Count(child =>
                        VisualTreeHelper.GetClip(child!) is { } clip &&
                        clip is not RectangleGeometry { RadiusX: 0, RadiusY: 0 }));
                    if (phaseImage is not null)
                    {
                        var origin = phaseImage.TransformToAncestor(window).Transform(new Point());
                        var pixelY = origin.Y * VisualTreeHelper.GetDpi(window).DpiScaleY;
                        pixelPhases.Add(Math.Abs(pixelY - Math.Round(pixelY)));
                    }
                };
                using var process = Process.GetCurrentProcess();
                var cpuStart = process.TotalProcessorTime;
                var scrollingClock = Stopwatch.StartNew();
                var direction = -1;
                viewer.LayoutUpdated += layout;
                CompositionTarget.Rendering += rendering;
                try
                {
                    for (var tick = 0; tick < ticks; tick++)
                    {
                        var target = SmoothScrollingController.Attach(viewer).TargetOffset;
                        if (target >= viewer.ScrollableHeight - 108) direction = 1;
                        else if (target <= 108) direction = -1;
                        PostInputSample(window.Dispatcher, scrollingInputs);
                        viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, direction * 360)
                            { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                        Pump(40);
                    }
                    Pump(200);
                }
                finally
                {
                    CompositionTarget.Rendering -= rendering;
                    viewer.LayoutUpdated -= layout;
                }
                var scrollingMilliseconds = scrollingClock.Elapsed.TotalMilliseconds;
                var cpuMilliseconds = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
                process.Refresh();
                var movement = offsets.Count == 0 ? 0 : offsets.Max() - offsets.Min();
                if (movement < 10) throw new InvalidOperationException("The real viewport did not move during wheel input.");
                var hwnd = new WindowInteropHelper(window).Handle;
                _ = GetClientRect(hwnd, out var client);
                var dpi = VisualTreeHelper.GetDpi(window);
#if !BASELINE
                object? backdropStats = backdrop is null ? null : new { backdrop.IsReady, backdrop.TargetPixelWidth,
                    backdrop.TargetPixelHeight, backdrop.PreparationCount, backdrop.AppliedCount, backdrop.LastError };
#else
                object? backdropStats = null;
#endif
                output.Add(new
                {
                    repeat, screen, detail, diagnostic, backdropMode = prepareBackdrop ? "prepared" : "original", fixtureCount,
                    removedTransparentBackgrounds,
                    surfaceMode = prepareSurfaces ? "prepared" : "original",
#if !BASELINE
                    preparedSurfaces = Descendants(window).OfType<BackdropSurfaceBorder>().Count(border => border.IsBackdropPrepared),
                    markedSurfaces = Descendants(window).OfType<BackdropSurfaceBorder>().Count(border => border.IsBackdropSurface),
#else
                    preparedSurfaces = 0, markedSurfaces = 0,
#endif
                    themeClear, targetClearAlpha = ((HwndTarget)PresentationSource.FromVisual(window)!.CompositionTarget).BackgroundColor.A,
                    disableDirtyRectangles = !args.Contains("--incremental"),
                    boundedCache = args.Contains("--bounded-cache"), cachedElements,
#if !BASELINE
                    surfacePending = backdrop?.IsSurfacePreparationPending, surfacePreparations = backdrop?.SurfacePreparationCount,
                    surfaceError = backdrop?.LastSurfaceError,
#else
                    surfacePending = (bool?)null, surfacePreparations = (long?)null, surfaceError = (string?)null,
#endif
                    wpfCachedDisableDirtyRectangles = typeof(RenderOptions).Assembly.GetType("MS.Internal.CoreAppContextSwitches", true)!
                        .GetProperty("DisableDirtyRectangles", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.GetValue(null),
                    fixtureMods = packs[0].Content.Mods.Count, windowState = window.WindowState.ToString(),
                    clientPixelWidth = client.Right - client.Left, clientPixelHeight = client.Bottom - client.Top,
                    nativeDpi = GetDpiForWindow(hwnd), layoutDpiX = dpi.DpiScaleX, layoutDpiY = dpi.DpiScaleY,
                    processRenderMode = RenderOptions.ProcessRenderMode.ToString(), renderingTier = RenderCapability.Tier >> 16,
                    targetRenderMode = ((HwndTarget)PresentationSource.FromVisual(window)!.CompositionTarget).RenderMode.ToString(),
                    windowRenderWidth = window.RenderSize.Width, windowRenderHeight = window.RenderSize.Height,
                    backdropBorders = Descendants(window).OfType<Border>().Where(border => ReferenceEquals(border.Background, window.Background))
                        .Select(border => new { border.ActualWidth, border.ActualHeight, physicalWidth = border.RenderSize.Width * dpi.DpiScaleX,
                            physicalHeight = border.RenderSize.Height * dpi.DpiScaleY }).ToArray(),
                    window.Topmost, nativeVisible = IsWindowVisible(hwnd), minimized = IsIconic(hwnd),
                    foregroundIsProbe = GetForegroundWindow() == hwnd,
                    ready, preloadMilliseconds, pendingKeys, executedSteps,
                    imageSettleMilliseconds,
#if !BASELINE
                    pendingImages = Descendants(window).OfType<PreparedImage>().Count(image => image.IsPreparationPending),
#else
                    pendingImages = (int?)null,
#endif
                    imageVariants = Descendants(window).OfType<Image>().Where(image => image.IsVisible)
                        .Select(image => new { image.ActualWidth, image.ActualHeight,
                            format = DrawnBitmap(image)?.Format.ToString() })
                        .Distinct().ToArray(),
                    preloadInputP95 = Percentile(preloadInputs, .95), preloadInputMax = Maximum(preloadInputs),
                    scrollInputP95 = Percentile(scrollingInputs, .95), scrollInputMax = Maximum(scrollingInputs),
                    frameP50 = Percentile(frameIntervals, .50), frameP95 = Percentile(frameIntervals, .95),
                    frameP99 = Percentile(frameIntervals, .99), frameMax = Maximum(frameIntervals),
                    framesOver33ms = frameIntervals.Count(value => value > 33.34), frames = frameIntervals.Count,
                    scrollingMilliseconds, cpuMilliseconds, cpuCoreShare = cpuMilliseconds / scrollingMilliseconds,
                    layoutUpdates, overflow, observedOffsetMovement = movement,
                    imagePixelPhaseP95 = Percentile(pixelPhases, .95), imagePixelPhaseMax = Maximum(pixelPhases),
                    activeRoundedClips = RoundedClipChildren(window).Count(child => child.Clip is not null),
                    activeRoundedClipPropertyMax = Maximum(clipPropertySamples),
                    activeRoundedVisualClipMax = Maximum(roundedVisualClipSamples),
                    actualVisualClipKinds = RoundedClipChildren(window).Select(VisualTreeHelper.GetClip)
                        .Where(clip => clip is not null).GroupBy(clip => clip!.GetType().Name)
                        .Select(group => new { kind = group.Key, count = group.Count() }).ToArray(),
                    suppressedClipAssignments = suppressedClips?.Assignments ?? 0,
                    workingSetBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64,
                    backdropStats, failures
                });
                File.WriteAllText(destination, JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally
            {
                if (diagnosticLayout is not null) window.LayoutUpdated -= diagnosticLayout;
                suppressedClips?.Dispose();
#if !BASELINE
                view.Dispose();
#endif
                window.Close(); Pump(20);
            }
        }
    }

    private sealed class ClipSuppression : IDisposable
    {
        private static readonly DependencyPropertyDescriptor Descriptor =
            DependencyPropertyDescriptor.FromProperty(UIElement.ClipProperty, typeof(UIElement));
        private readonly Dictionary<UIElement, (EventHandler Handler, Func<Geometry?> Original)> observed = [];
        public int Assignments { get; private set; }
        public void Observe(DependencyObject root)
        {
            foreach (var child in RoundedClipChildren(root))
            {
                if (observed.ContainsKey(child)) continue;
                var clearing = false;
                Geometry? original = child.Clip;
                void Clear()
                {
                    if (clearing || child.Clip is null) return;
                    original = child.Clip;
                    Assignments++;
                    clearing = true;
                    try { child.SetCurrentValue(UIElement.ClipProperty, null); }
                    finally { clearing = false; }
                }
                EventHandler handler = (_, _) => Clear();
                observed.Add(child, (handler, () => original));
                Descriptor.AddValueChanged(child, handler);
                Clear();
            }
        }
        public void Dispose()
        {
            foreach (var (child, entry) in observed)
            {
                Descriptor.RemoveValueChanged(child, entry.Handler);
                child.SetCurrentValue(UIElement.ClipProperty, entry.Original());
            }
            observed.Clear();
        }
    }

    private static IEnumerable<UIElement> RoundedClipChildren(DependencyObject root) => Descendants(root).OfType<Border>()
        .Where(border => border.GetType().Name == "RoundedClipBorder").Select(border => border.Child).OfType<UIElement>();

    private static System.Windows.Media.Imaging.BitmapSource? DrawnBitmap(Image image) => VisualTreeHelper.GetDrawing(image)?.Children
        .Select(drawing => drawing is ImageDrawing picture ? picture.ImageSource : (drawing as GeometryDrawing)?.Brush is ImageBrush brush ? brush.ImageSource : null)
        .OfType<System.Windows.Media.Imaging.BitmapSource>().FirstOrDefault();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject owner)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(owner); index++)
        {
            var child = VisualTreeHelper.GetChild(owner, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static int CacheBoundedElements(Window window)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var primaryScale = GetSystemMetrics(0) / SystemParameters.PrimaryScreenWidth;
        var scale = dpi.DpiScaleX / primaryScale;
        var selected = new HashSet<DependencyObject>();
        long pixels = 0;
        foreach (var element in Descendants(window).OfType<FrameworkElement>().Where(item => item.IsVisible))
        {
            var parent = VisualTreeHelper.GetParent(element);
            var covered = false;
            while (parent is not null)
            {
                if (selected.Contains(parent)) { covered = true; break; }
                parent = VisualTreeHelper.GetParent(parent);
            }
            if (covered) continue;
            if (!(element is Border && element.GetType().Name == "RoundedClipBorder") && element is not TreeView and not TextBlock &&
                element.Name is not ("MainPanel" or "LaunchActions" or "SidePanelCard" or "UpdateArea" or "FooterActions")) continue;
            var bounds = VisualTreeHelper.GetDescendantBounds(element);
            var width = Math.Ceiling(bounds.Width * dpi.DpiScaleX);
            var height = Math.Ceiling(bounds.Height * dpi.DpiScaleY);
            if (width < 1 || height < 1 || width > 2000 || height > 2000 || pixels + width * height > 16L * 1024 * 1024) continue;
            foreach (var text in Descendants(element).OfType<TextBlock>().Prepend(element as TextBlock).OfType<TextBlock>())
            {
                TextOptions.SetTextRenderingMode(text, TextOptions.GetTextRenderingMode(text));
                TextOptions.SetTextFormattingMode(text, TextOptions.GetTextFormattingMode(text));
                TextOptions.SetTextHintingMode(text, TextOptions.GetTextHintingMode(text));
            }
            element.CacheMode = new BitmapCache(scale) { SnapsToDevicePixels = true, EnableClearType = false };
            selected.Add(element); pixels += (long)(width * height);
        }
        return selected.Count;
    }

    private static CatalogPack[] Fixture(IReadOnlyList<CatalogPack> embedded, int count, int descriptionLines)
    {
        if (embedded.Count == 0) throw new InvalidOperationException("The embedded snapshot has no preview fixture.");
        return Enumerable.Range(0, count).Select(index =>
        {
            var source = embedded[index % embedded.Count];
            var pack = new CatalogPack
            {
                Entry = new CatalogSource("probe-pack-" + index, "Probe pack " + index, source.Entry.Source),
                Content = new CatalogDescription
                {
                    Name = "Probe pack " + index, MinecraftVersion = "1.20.1",
                    Summary = "A reproducible preview fixture keeps the complete summary visible. " +
                        "Cards and background use the real launcher renderer and controls.",
                    Description = string.Join(Environment.NewLine, Enumerable.Repeat(
                        "A short-mod-list fixture paragraph keeps this page scrollable at the current viewport.", descriptionLines)),
                    Mods = Enumerable.Range(0, 12).Select(mod => new CatalogMod { Id = "probe-mod-" + mod, Name = "Probe mod " + mod }).ToList()
                },
                Preview = source.Preview
            };
            pack.BuildTree();
            return pack;
        }).ToArray();
    }

    private static void PostInputSample(Dispatcher dispatcher, List<double> values)
    {
        var posted = Stopwatch.GetTimestamp();
        dispatcher.BeginInvoke(DispatcherPriority.Input,
            new Action(() => values.Add(Stopwatch.GetElapsedTime(posted).TotalMilliseconds)));
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index >= 0 && index + 1 < args.Length) return args[index + 1];
        return args.FirstOrDefault(value => value.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out ClientRectangle rectangle);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    static double Percentile(List<double> values, double percentile = .95) => values.Count == 0 ? 0 :
        values.Order().ElementAt((int)((values.Count - 1) * percentile));
    private static double Maximum(List<double> values) => values.Count == 0 ? 0 : values.Max();

    static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    static Window LoadWindow(string repository)
    {
        var app = File.ReadAllText(Path.Combine(repository, "Program/App.xaml"));
        var resources = Regex.Match(app, @"<Application\.Resources>(.*)</Application\.Resources>", RegexOptions.Singleline).Groups[1].Value;
        var namespaces = string.Concat(Regex.Matches(app, @"\sxmlns:(?!x=)[A-Za-z0-9]+=""[^""]*""").Select(m => m.Value));
        var xaml = File.ReadAllText(Path.Combine(repository, "Program/MainWindow.xaml"));
        xaml = Regex.Replace(xaml, @"x:Class=""[^""]*""", namespaces);
        xaml = Regex.Replace(xaml, @"\s(Click|Loaded|Closing|SizeChanged|SelectionChanged|TextChanged|PreviewTextInput|LostFocus|KeyDown|DataObject\.Pasting)=""[^""]*""", "");
        xaml = xaml.Replace("Icon=\"Minecraft.ico\"", "").Replace("clr-namespace:Minecraft\"", "clr-namespace:Minecraft;assembly=LANMinecraft\"");
        xaml = xaml.Replace("<Window.Resources>", "<Window.Resources>" + resources);
        return (Window)XamlReader.Parse(xaml);
    }
}
