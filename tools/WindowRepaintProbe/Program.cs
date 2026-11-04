using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Minecraft;

internal static class Probe
{
    private const string DirtySwitch = "Switch.System.Windows.Media.MediaContext.DisableDirtyRectangles";
    private static readonly List<object> Results = [];

    [STAThread]
    private static int Main(string[] args)
    {
        // Keep the bootstrap free of WPF types: the switch must precede its
        // cached first read, even when the probe is run using the launcher CLR.
        AppContext.SetSwitch(DirtySwitch, !args.Contains("--incremental"));
        return Run(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("Pass repository and artifacts output directory.");
        var repository = Path.GetFullPath(args[0]);
        var destination = Path.GetFullPath(args[1]);
        var artifacts = Path.GetFullPath(Path.Combine(repository, "artifacts")) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase) ||
            !AppPaths.ResolveApplicationRoot().StartsWith(artifacts, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run the probe executable and store its evidence inside repository artifacts.");
        Directory.CreateDirectory(destination);
        var opaque = args.Contains("--opaque-client");
        var renderMode = args.Contains("--hardware") ? RenderMode.Default : RenderMode.SoftwareOnly;
        var prepareBackdrop = !args.Contains("--original-backdrop");
        var screens = Option(args, "--screen") is { } screen ? new[] { screen } : new[] { "main", "overview", "detail" };
        if (screens.Any(value => value is not ("main" or "overview" or "detail"))) throw new ArgumentException("Unknown screen.");
        var app = new Minecraft.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        // Only this isolated diagnostic can override the production App policy.
        RenderOptions.ProcessRenderMode = renderMode;
        var theme = typeof(MainWindow).Assembly.GetType("Minecraft.WindowsThemeService", true)!;
        theme.GetMethod("Initialize", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, [app]);
        var attachTheme = theme.GetMethod("Attach", BindingFlags.Static | BindingFlags.NonPublic)!;
        var failures = new List<string>();
        try
        {
            foreach (var page in screens)
            {
                var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Topmost = true };
                RemoveStartupHandlers(window);
                var root = (Grid)window.FindName("RootGrid");
                var library = (PackLibraryView)window.FindName("LibraryView");
                using var queue = new UiPreloadQueue(window.Dispatcher, error => failures.Add(error.ToString()));
                library.Preloader = queue;
                var marker = new TextBlock { Width = 128, Height = 24, TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                    Foreground = Brushes.Black, FontSize = 14, Margin = new Thickness(8), IsHitTestVisible = false };
                Grid.SetRowSpan(marker, Math.Max(1, root.RowDefinitions.Count));
                Grid.SetColumnSpan(marker, Math.Max(1, root.ColumnDefinitions.Count));
                root.Children.Add(marker);
                var generation = 0;
                SetMarker();
                var initialRendered = false;
                window.ContentRendered += (_, _) => initialRendered = true;
                window.SourceInitialized += (_, _) =>
                {
                    attachTheme.Invoke(null, [window, opaque]);
                    ((HwndTarget)PresentationSource.FromVisual(window)!.CompositionTarget).RenderMode = renderMode;
                };
                var paths = new AppPaths(Path.Combine(destination, "fixture"));
                paths.Ensure();
                using var http = new HttpClient(new OfflineHandler());
                var catalog = new PackCatalogService(paths, new Logger(paths.LogFile), http);
                if (page != "main")
                {
                    foreach (UIElement element in root.Children)
                        if (!ReferenceEquals(element, library) && !ReferenceEquals(element, marker)) element.Visibility = Visibility.Hidden;
                    library.Visibility = Visibility.Visible;
                    var packs = Fixture(catalog.Packs);
                    library.SetCatalog(packs);
                    if (page == "detail") library.ShowDetails(packs[0]);
                }
                else
                {
                    ((Expander)window.FindName("ReportExpander")).IsExpanded = true;
                    ((Expander)window.FindName("WorldTransferExpander")).IsExpanded = true;
                }
                PreparedWindowBackdrop? backdrop = null;
                try
                {
                    var firstFrameClock = Stopwatch.StartNew();
                    window.Show();
                    Wait(() => initialRendered, "The first native frame was not rendered.");
                    var firstFrameMilliseconds = firstFrameClock.Elapsed.TotalMilliseconds;
                    // DWM's opening animation can still blend another window
                    // into the desktop after WPF's ContentRendered event.
                    Pump(600);
                    Capture("first-frame", firstFrameMilliseconds);
                    if (prepareBackdrop) backdrop = PreparedWindowBackdrop.Attach(window, queue);
                    Steady();
                    Capture("prepared", null);
                    var hwnd = new WindowInteropHelper(window).Handle;
                    var dpi = GetDpiForWindow(hwnd) / 96d;
                    var monitor = MonitorFromWindow(hwnd, 2);
                    var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                    if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception();
                    var normal = new Size(Math.Min(1104 * dpi, info.Work.Right - info.Work.Left - 64),
                        Math.Min(761 * dpi, info.Work.Bottom - info.Work.Top - 64));
                    var large = new Size(Math.Min(1504 * dpi, info.Work.Right - info.Work.Left - 32),
                        Math.Min(1001 * dpi, info.Work.Bottom - info.Work.Top - 32));
                    Transition("resize-normal", () => Resize(normal));
                    Transition("resize-large", () => Resize(large));
                    Transition("resize-small", () => Resize(normal));
                    for (var repeat = 0; repeat < 3; repeat++)
                    {
                        Transition("maximize-" + repeat, () => SendMessage(hwnd, 0x0112, (nint)0xF030, 0));
                        Transition("restore-" + repeat, () => SendMessage(hwnd, 0x0112, (nint)0xF120, 0));
                    }
                    SendMessage(hwnd, 0x0112, (nint)0xF020, 0);
                    Pump(100);
                    Transition("restore-minimized", () => SendMessage(hwnd, 0x0112, (nint)0xF120, 0));
                    var viewer = page == "main" ? (ScrollViewer)window.FindName("BodyScroll") :
                        (ScrollViewer)library.FindName(page == "detail" ? "DetailScroll" : "CatalogScroll")!;
                    if (page == "main")
                    {
                        ((Grid)window.FindName("BodyGrid")).MinHeight = viewer.ViewportHeight * 2;
                        window.UpdateLayout();
                    }
                    Transition("scroll-bottom", () => viewer.ScrollToBottom());
                    Transition("scroll-top", () => viewer.ScrollToTop());

                    void Resize(Size size)
                    {
                        if (!SetWindowPos(hwnd, 0, info.Work.Left + 16, info.Work.Top + 16,
                            (int)size.Width, (int)size.Height, 0x0004 | 0x0010)) throw new Win32Exception();
                    }
                    void Transition(string name, Action action)
                    {
                        SetMarker();
                        action();
                        Steady();
                        Capture(name, null);
                    }
                }
                finally
                {
                    backdrop?.Dispose(); library.Dispose(); window.Close(); Pump(30);
                }

                void SetMarker()
                {
                    marker.Text = "Frame " + ++generation;
                    marker.Background = new SolidColorBrush((generation % 3) switch
                    { 0 => Colors.Cyan, 1 => Colors.Magenta, _ => Colors.Yellow });
                }
                void Steady()
                {
                    Wait(() => queue.PendingKeys.Length == 0 && (page == "main" || library.IsOverviewReady) &&
                        !Descendants(window).OfType<PreparedImage>().Any(image => image.IsPreparationPending) &&
                        backdrop?.IsPreparing != true, "The native window did not settle.");
                    // Normal dispatcher/render work is allowed. Never invalidate,
                    // WM_PRINT or PrintWindow: these could repair the regression.
                    Pump(600);
                }
                void Capture(string transition, double? firstFrameMilliseconds)
                {
                    var handle = new WindowInteropHelper(window).Handle;
                    GetClientRect(handle, out var client);
                    var origin = new PointI();
                    if (!ClientToScreen(handle, ref origin)) throw new Win32Exception();
                    var width = client.Right; var height = client.Bottom;
                    EnsureUnoccluded(handle, origin, width, height);
                    _ = DwmFlush();
                    var actual = CaptureDesktop(origin, width, height);
                    // Capture the actual presented pixels BEFORE asking WPF for
                    // an independent oracle; its rendering cannot heal the frame.
                    var scale = VisualTreeHelper.GetDpi(window);
                    var expected = new RenderTargetBitmap(width, height, scale.PixelsPerInchX, scale.PixelsPerInchY, PixelFormats.Pbgra32);
                    expected.Render(window);
                    var expectedPixels = new byte[checked(width * height * 4)];
                    expected.CopyPixels(expectedPixels, width * 4, 0);
                    var measurement = Compare(actual, expectedPixels, width, height);
                    var markerOrigin = marker.PointToScreen(new Point(marker.ActualWidth / 2, marker.ActualHeight / 2));
                    var mx = (int)Math.Round(markerOrigin.X) - origin.X;
                    var my = (int)Math.Round(markerOrigin.Y) - origin.Y;
                    var markerColor = ((SolidColorBrush)marker.Background).Color;
                    // Check a plain part of the patch rather than its label ink.
                    mx -= (int)Math.Round(marker.ActualWidth * scale.DpiScaleX / 3);
                    var markerOffset = (my * width + mx) * 4;
                    var markerError = mx < 0 || mx >= width || my < 0 || my >= height ? 255 :
                        Math.Max(Math.Abs(actual[markerOffset] - markerColor.B),
                            Math.Max(Math.Abs(actual[markerOffset + 1] - markerColor.G), Math.Abs(actual[markerOffset + 2] - markerColor.R)));
                    var target = (HwndTarget)PresentationSource.FromVisual(window)!.CompositionTarget;
                    var nativeStyle = GetWindowLongPtr(handle, -16).ToInt64();
                    var frameStyle = (nativeStyle & 0x00C40000L) == 0x00C40000L && (nativeStyle & 0x00010000L) != 0;
                    var passed = measurement.MeanError <= 12 && measurement.TileP95 <= 25 && measurement.MissingLightFraction <= .005 &&
                        markerError <= 3 && frameStyle;
                    var prefix = page + "-" + transition;
                    SaveBitmap(expectedPixels, width, height, Path.Combine(destination, prefix + "-expected.png"));
                    // An incomplete/transparent presented frame can contain
                    // pixels from an unrelated application behind this fixture.
                    // Persist only verified fixture frames, never those pixels.
                    if (passed)
                    {
                        SaveBitmap(actual, width, height, Path.Combine(destination, prefix + "-actual.png"));
                        SaveBitmap(measurement.Difference, width, height, Path.Combine(destination, prefix + "-diff.png"));
                    }
                    var result = new
                    {
                        page, transition, generation, passed, width, height, dpi = GetDpiForWindow(handle),
                        layoutDpi = scale.DpiScaleX, windowState = window.WindowState.ToString(), opaqueCandidate = opaque,
                        targetClearColor = target.BackgroundColor.ToString(), processRenderMode = RenderOptions.ProcessRenderMode.ToString(),
                        dirtyRectanglesDisabled = AppContext.TryGetSwitch(DirtySwitch, out var disabled) && disabled,
                        firstFrameMilliseconds, measurement.MeanError, measurement.TileP95, measurement.MissingLightFraction,
                        actualPixelsSaved = passed, nativeAnimationSettleMilliseconds = 600,
                        markerError, systemFrameAndSnapStyles = frameStyle, window.Opacity, window.AllowsTransparency,
                        startupHandlersRemoved = true, capture = "DesktopBitBltBeforeOracle", failures = failures.ToArray()
                    };
                    Results.Add(result);
                    File.WriteAllText(Path.Combine(destination, "results.json"), JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
                    if (!passed) failures.Add(prefix + " did not match the native current frame.");
                }
            }
        }
        finally { app.Shutdown(); }
        Console.WriteLine(JsonSerializer.Serialize(new { captures = Results.Count, failures, destination }));
        return failures.Count == 0 ? 0 : 1;
    }

    private static void RemoveStartupHandlers(MainWindow window)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window,
            typeof(MainWindow).GetMethod("Window_Loaded", flags)!);
        window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window,
            typeof(MainWindow).GetMethod("Window_Closing", flags)!);
    }

    private static CatalogPack[] Fixture(IReadOnlyList<CatalogPack> originals)
    {
        if (originals.Count == 0) throw new InvalidOperationException("The embedded preview fixture is empty.");
        return Enumerable.Range(0, 36).Select(index =>
        {
            var original = originals[index % originals.Count];
            var pack = new CatalogPack
            {
                Entry = original.Entry with { Id = "repaint-" + index },
                Preview = original.Preview,
                Content = new CatalogDescription { Name = "Repaint fixture " + index, MinecraftVersion = "1.20.1",
                    Summary = "Native launcher controls and embedded preview.",
                    Description = string.Join(Environment.NewLine, Enumerable.Repeat("A native repaint fixture paragraph.", 100)),
                    Mods = Enumerable.Range(0, 12).Select(mod => new CatalogMod { Id = "fixture-" + mod, Name = "Fixture mod " + mod }).ToList() }
            };
            pack.BuildTree();
            return pack;
        }).ToArray();
    }

    private static (double MeanError, double TileP95, double MissingLightFraction, byte[] Difference) Compare(byte[] actual, byte[] expected, int width, int height)
    {
        long error = 0; long missing = 0; long light = 0;
        var tiles = new List<double>();
        var difference = new byte[actual.Length];
        for (var top = 0; top < height; top += 32)
        for (var left = 0; left < width; left += 32)
        {
            long tileError = 0; var samples = 0;
            for (var y = top; y < Math.Min(top + 32, height); y++)
            for (var x = left; x < Math.Min(left + 32, width); x++)
            {
                var offset = (y * width + x) * 4;
                var brightness = 0; var actualBrightness = 0;
                for (var channel = 0; channel < 3; channel++)
                {
                    var delta = Math.Abs(actual[offset + channel] - expected[offset + channel]);
                    error += delta; tileError += delta; samples++;
                    difference[offset + channel] = (byte)Math.Min(255, delta * 4);
                    brightness += expected[offset + channel]; actualBrightness += actual[offset + channel];
                }
                if (brightness > 120) { light++; if (actualBrightness < 15) missing++; }
            }
            tiles.Add((double)tileError / samples);
        }
        tiles.Sort();
        return ((double)error / (width * (double)height * 3), tiles[(int)((tiles.Count - 1) * .95)],
            light == 0 ? 0 : (double)missing / light, difference);
    }

    private static void SaveBitmap(byte[] pixels, int width, int height, string path)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static byte[] CaptureDesktop(PointI origin, int width, int height)
    {
        var screen = GetDC(0); var memory = CreateCompatibleDC(screen);
        var info = new BitmapInfo { Header = new BitmapHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 } };
        var bitmap = CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
        if (bitmap == 0 || memory == 0 || screen == 0) throw new Win32Exception();
        var previous = SelectObject(memory, bitmap);
        try
        {
            if (!BitBlt(memory, 0, 0, width, height, screen, origin.X, origin.Y, 0x40CC0020)) throw new Win32Exception();
            var pixels = new byte[checked(width * height * 4)]; Marshal.Copy(bits, pixels, 0, pixels.Length); return pixels;
        }
        finally { SelectObject(memory, previous); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(0, screen); }
    }

    private static void EnsureUnoccluded(nint hwnd, PointI origin, int width, int height)
    {
        for (var row = 0; row < 12; row++)
        for (var column = 0; column < 16; column++)
        {
            var sample = new PointI { X = origin.X + (column * 2 + 1) * width / 32,
                Y = origin.Y + (row * 2 + 1) * height / 24 };
            var owner = WindowFromPoint(sample);
            if (owner == 0 || GetAncestor(owner, 2) != hwnd)
                throw new InvalidOperationException("The actual client is obscured or outside the desktop; this capture is invalid.");
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject owner)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(owner); index++)
        {
            var child = VisualTreeHelper.GetChild(owner, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Wait(Func<bool> ready, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!ready() && clock.Elapsed.TotalSeconds < 30) Pump(16);
        if (!ready()) throw new TimeoutException(message);
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The repaint probe must not use the network.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct PointI { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public RectI Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, ImageSize; public int XPels, YPels; public uint Used, Important; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapHeader Header; public uint Colors; }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out RectI rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref PointI point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(PointI point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
