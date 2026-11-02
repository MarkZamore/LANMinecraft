using System.IO;
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
            var paths = new AppPaths(Path.Combine(Path.GetDirectoryName(destination)!, "fixture")); paths.Ensure();
            var catalog = new PackCatalogService(paths, new Logger(paths.LogFile));
            foreach (var scale in new[] { 1d, 1.5d, 2d })
            foreach (var detail in new[] { false, true })
            {
                var window = LoadWindow(repository);
                window.ShowInTaskbar = false; window.ShowActivated = false;
                var view = (PackLibraryView)window.FindName("LibraryView");
                foreach (UIElement child in ((Grid)window.FindName("RootGrid")).Children) if (child != view) child.Visibility = Visibility.Hidden;
                view.Visibility = Visibility.Visible;
                var failures = new List<string>();
#if !BASELINE
                using var queue = new UiPreloadQueue(window.Dispatcher, ex => failures.Add(ex.ToString()));
                view.Preloader = queue;
#endif
                window.Show();
                VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
                view.SetCatalog(catalog.Packs);
                var clock = Stopwatch.StartNew();
                var latencies = new List<double>();
#if !BASELINE
                while (!catalog.Packs.All(p => queue.IsComplete("detail:" + p.Entry.Id)) && clock.Elapsed.TotalSeconds < 30)
                {
                    var posted = Stopwatch.GetTimestamp();
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => latencies.Add(Stopwatch.GetElapsedTime(posted).TotalMilliseconds)));
                    Pump(16);
                }
                var ready = catalog.Packs.All(p => queue.IsComplete("detail:" + p.Entry.Id));
                var pendingKeys = queue.PendingKeys;
                var executedSteps = queue.ExecutedSteps;
#else
                Pump(200);
                var ready = true;
                var pendingKeys = Array.Empty<string>();
                long executedSteps = 0;
#endif
                if (detail) view.ShowDetails(catalog.Packs.OrderByDescending(p => p.Content.Mods.Count).First());
                window.UpdateLayout(); Pump(200);
                var preloadMilliseconds = clock.Elapsed.TotalMilliseconds;
                var viewer = (ScrollViewer)view.FindName(detail ? "DetailScroll" : "CatalogScroll")!;
                var intervals = new List<double>(); long previous = 0; TimeSpan previousFrame = TimeSpan.MinValue;
                EventHandler rendering = (_, e) => { var stamp = ((RenderingEventArgs)e).RenderingTime; if (stamp == previousFrame) return; previousFrame = stamp; var now = Stopwatch.GetTimestamp(); if (previous != 0) intervals.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds); previous = now; };
                CompositionTarget.Rendering += rendering;
                for (int tick = 0; tick < 40; tick++)
                {
                    viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                    Pump(40);
                }
                CompositionTarget.Rendering -= rendering;
                output.Add(new { scale, detail, ready, preloadMilliseconds,
                    pendingKeys, executedSteps, inputP95 = Percentile(latencies), frameP95 = Percentile(intervals), frames = intervals.Count, failures });
                File.WriteAllText(destination, JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
#if !BASELINE
                view.Dispose();
#endif
                window.Close(); Pump(20);
            }
            app.Shutdown();
        }
        File.WriteAllText(destination, JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
    }
    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    static double Percentile(List<double> values) => values.Count == 0 ? 0 : values.Order().ElementAt((int)((values.Count - 1) * .95));
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
