using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

internal static class Program
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Stopwatch ManagedClock = Stopwatch.StartNew();
    private static readonly DateTime ProcessStartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    [STAThread]
    private static int Main(string[] args)
    {
        string? output = null;
        Application? application = null;
        Window? window = null;
        object? library = null;
        string? installationRoot = null;
        Dictionary<string, string>? personalFilesBefore = null;
        var report = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["scope"] = "native host and actual WPF shell; OnStartup, Loaded and Closing services excluded",
            ["processId"] = Environment.ProcessId,
            ["processStartedUtc"] = ProcessStartedUtc,
            ["managedEntryAgeMs"] = AgeMilliseconds(),
            ["runtimeVersion"] = Environment.Version.ToString(),
            ["runtimeDirectory"] = RuntimeEnvironment.GetRuntimeDirectory(),
            ["frameworkDescription"] = RuntimeInformation.FrameworkDescription,
            ["osDescription"] = RuntimeInformation.OSDescription,
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["logicalProcessors"] = Environment.ProcessorCount,
            ["nativeBootstrapContext"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LANMINECRAFT_BOOTSTRAP_CONTEXT")),
            ["status"] = "failed"
        };
        try
        {
            var options = ParseArguments(args);
            output = RequiredAbsolute(options, "output");
            var appPath = options.TryGetValue("application", out var requested)
                ? Path.GetFullPath(requested)
                : Path.Combine(AppContext.BaseDirectory, "LANMinecraft.dll");
            var stateDirectory = options.TryGetValue("state-directory", out var state)
                ? Path.GetFullPath(state) : Path.Combine(Path.GetDirectoryName(output)!, "ui-fixture");
            if (!File.Exists(appPath)) throw new FileNotFoundException("The actual launcher assembly is missing.", appPath);
            report["label"] = options.GetValueOrDefault("label", "unspecified");
            report["applicationPath"] = appPath;
            report["executablePath"] = Environment.ProcessPath;
            installationRoot = Path.GetDirectoryName(Environment.ProcessPath!)!;
            personalFilesBefore = PersonalFiles(installationRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);

            // Load the launcher into the default WPF context, while resolving its
            // own dependencies from its original published deps file/directory.
            var resolver = new AssemblyDependencyResolver(appPath);
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                var path = resolver.ResolveAssemblyToPath(name);
                var local = Path.Combine(Path.GetDirectoryName(appPath)!, name.Name + ".dll");
                return path is not null ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) :
                    File.Exists(local) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(local) : null;
            };
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
                resolver.ResolveUnmanagedDllToPath(name) is { } path ? NativeLibrary.Load(path) : IntPtr.Zero;
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(appPath);
            report["applicationVersion"] = assembly.GetName().Version?.ToString();
            var appType = assembly.GetType("Minecraft.App", throwOnError: true)!;
            var windowType = assembly.GetType("Minecraft.MainWindow", throwOnError: true)!;

            // WPF queues OnStartup from Application's constructor even without
            // Run. Abort only that exact constructor delegate before any pump.
            // Keep the actual App type: a derived type breaks WPF's compiled
            // resource cross-assembly check in InitializeComponent.
            var startupOperations = new List<DispatcherOperation>();
            var callbacks = new List<string>();
            var callbackField = typeof(DispatcherOperation).GetField("_method", InstanceMembers)
                ?? throw new MissingFieldException(typeof(DispatcherOperation).FullName, "_method");
            var hooks = Dispatcher.CurrentDispatcher.Hooks;
            DispatcherHookEventHandler interceptStartup = (_, posted) =>
            {
                if (posted.Operation.Priority != DispatcherPriority.Send) return;
                var callback = (Delegate)callbackField.GetValue(posted.Operation)!;
                if (callback.Method.DeclaringType != typeof(Application) ||
                    !callback.Method.Name.StartsWith("<.ctor>", StringComparison.Ordinal)) return;
                if (!posted.Operation.Abort() || posted.Operation.Status != DispatcherOperationStatus.Aborted)
                    throw new InvalidOperationException("The constructor's queued Application startup could not be aborted.");
                startupOperations.Add(posted.Operation);
                callbacks.Add(callback.Method.DeclaringType.FullName + "." + callback.Method.Name);
            };
            hooks.OperationPosted += interceptStartup;
            try { application = (Application)Activator.CreateInstance(appType)!; }
            finally { hooks.OperationPosted -= interceptStartup; }
            if (startupOperations.Count != 1)
                throw new InvalidOperationException("Exactly one known WPF constructor startup operation must be aborted before any dispatcher pump.");
            report["startupCallbackSuppressed"] = true;
            report["abortedStartupOperations"] = startupOperations.Count;
            report["abortedStartupCallbacks"] = callbacks;
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            appType.GetMethod("InitializeComponent", InstanceMembers)!.Invoke(application, null);
            report["applicationResourcesMs"] = ManagedClock.Elapsed.TotalMilliseconds;
            window = (Window)Activator.CreateInstance(windowType)!;
            var loaded = (RoutedEventHandler)windowType.GetMethod("Window_Loaded", InstanceMembers)!
                .CreateDelegate(typeof(RoutedEventHandler), window);
            var closing = (CancelEventHandler)windowType.GetMethod("Window_Closing", InstanceMembers)!
                .CreateDelegate(typeof(CancelEventHandler), window);
            window.Loaded -= loaded;
            window.Closing -= closing;
            report["serviceHandlersRemoved"] = new[] { "Window_Loaded", "Window_Closing" };
            report["windowConstructedMs"] = ManagedClock.Elapsed.TotalMilliseconds;
            application.MainWindow = window;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            // ShowLibrary restores focus to a launcher button. This probe uses
            // routed input/control edits, so block native keyboard focus for
            // every descendant instead of activating an invisible window.
            window.AddHandler(Keyboard.PreviewGotKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler((_, change) => change.Handled = true), true);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.WindowState = WindowState.Normal;
            window.Left = -32000;
            window.Top = -32000;
            var wasActivated = false;
            var becameForeground = false;
            var uiPhase = "show";
            window.Activated += (_, _) =>
            {
                wasActivated = true;
                becameForeground |= GetForegroundWindow() == new WindowInteropHelper(window).Handle;
                report["activationPhase"] = uiPhase;
            };
            // WindowPlacementService has an earlier SourceInitialized callback.
            // This later callback overrides its restoration before Show returns.
            window.SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(window).Handle;
                // Navigation restores logical focus to the library button.
                // Keep that local focus change from activating this HWND.
                var style = GetWindowLongPtr(handle, -20).ToInt64();
                _ = SetWindowLongPtr(handle, -20, new IntPtr(style | 0x08000000L)); // WS_EX_NOACTIVATE
                MoveOffscreen(window);
            };
            var firstFrame = false;
            window.ContentRendered += (_, _) =>
            {
                if (firstFrame) return;
                report["firstFrameMs"] = AgeMilliseconds();
                report["managedToFirstFrameMs"] = ManagedClock.Elapsed.TotalMilliseconds;
                firstFrame = true;
            };
            // Create the source while Visibility is still Hidden. Otherwise
            // the placement callback can see IsVisible=true inside Show and
            // request SW_SHOWNORMAL before ShowActivated=false takes effect.
            _ = new WindowInteropHelper(window).EnsureHandle();
            window.Show();
            MoveOffscreen(window);
            PumpUntil(() => firstFrame);
            report["startupOperationStatusAfterFrame"] = startupOperations[0].Status.ToString();
            report["applicationWindowCountAfterFrame"] = application.Windows.Count;
            if (startupOperations[0].Status != DispatcherOperationStatus.Aborted || application.Windows.Count != 1)
                throw new InvalidOperationException("Application startup isolation was not preserved through the first frame.");
            becameForeground |= GetForegroundWindow() == new WindowInteropHelper(window).Handle;
            report["applicationSha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(appPath)));
            if (window.ActualWidth <= 0 || window.ActualHeight <= 0)
                throw new InvalidOperationException("The real window did not lay out.");
            report["dpi"] = VisualTreeHelper.GetDpi(window).DpiScaleX;

            // Production records its prepared set after the first frame. Keep
            // that real validation here, but outside the first-frame metric.
            if (assembly.GetType("Minecraft.DesktopBootstrapCache") is { } desktopCache)
            {
                var commitClock = Stopwatch.StartNew();
                var installationPaths = Activator.CreateInstance(assembly.GetType("Minecraft.AppPaths", true)!,
                    Path.GetDirectoryName(Environment.ProcessPath!)!)!;
                const BindingFlags staticMembers = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                desktopCache.GetMethod("CommitSuccessful", staticMembers)!.Invoke(null, [installationPaths, null]);
                var committed = (bool)desktopCache.GetMethod("HasSuccessfulSet", staticMembers)!.Invoke(null, [installationPaths])!;
                report["successfulSetCommitMs"] = commitClock.Elapsed.TotalMilliseconds;
                report["successfulSetCommitted"] = committed;
                if (!committed) throw new InvalidOperationException("The actual bootstrap successful set was not committed.");
                report["firstFrameAndSuccessfulSetReadyMs"] = AgeMilliseconds();
            }

            // Decode only the launcher's own offline library seed. A denying
            // HTTP handler makes accidental network work fail immediately.
            var prepare = Stopwatch.StartNew();
            using var http = new HttpClient(new RejectNetwork());
            var paths = Activator.CreateInstance(assembly.GetType("Minecraft.AppPaths", true)!, stateDirectory)!;
            var logger = Activator.CreateInstance(assembly.GetType("Minecraft.Logger", true)!, Path.Combine(stateDirectory, "probe.log"))!;
            var catalog = Activator.CreateInstance(assembly.GetType("Minecraft.PackCatalogService", true)!,
                paths, logger, http, CancellationToken.None, false)!;
            var packs = catalog.GetType().GetProperty("Packs")!.GetValue(catalog)!;
            var packCount = ((IEnumerable)packs).Cast<object>().Count();
            library = window.FindName("LibraryView") ?? throw new InvalidOperationException("LibraryView was not found.");
            library.GetType().GetMethod("SetCatalog")!.Invoke(library, [packs, false]);
            report["offlineLibraryPreparationMs"] = prepare.Elapsed.TotalMilliseconds;
            report["offlineLibraryCards"] = packCount;
            if (packCount == 0) throw new InvalidOperationException("The actual offline library seed is empty; this run would not compare real cards.");
            var libraryButton = (Button)window.FindName("LibraryButton");
            var showLibrary = windowType.GetMethod("ShowLibrary", InstanceMembers)!;
            var name = (TextBox)window.FindName("PlayerNameTextBox");
            name.IsEnabled = true;
            name.IsReadOnly = false;
            var samples = new List<object>();
            for (var sample = 0; sample < 8; sample++)
            {
                uiPhase = "input-" + sample;
                var input = MeasureUi(window, () =>
                {
                    var text = "Probe" + sample;
                    var composition = new TextComposition(InputManager.Current, name, text);
                    var inputEvent = new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice, composition)
                    { RoutedEvent = TextCompositionManager.PreviewTextInputEvent };
                    name.RaiseEvent(inputEvent);
                    if (inputEvent.Handled) throw new InvalidOperationException("The real input validator rejected the probe nickname.");
                    name.SelectAll();
                    name.SelectedText = text;
                    if (name.Text != text) throw new InvalidOperationException("The real nickname control did not accept text.");
                });
                becameForeground |= GetForegroundWindow() == new WindowInteropHelper(window).Handle;
                uiPhase = "library-open-" + sample;
                var open = MeasureUi(window, () =>
                {
                    libraryButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, libraryButton));
                    if (((UIElement)library).Visibility != Visibility.Visible)
                        throw new InvalidOperationException("Actual library navigation did not open the view.");
                });
                becameForeground |= GetForegroundWindow() == new WindowInteropHelper(window).Handle;
                uiPhase = "library-return-" + sample;
                var back = MeasureUi(window, () => showLibrary.Invoke(window, [false]));
                becameForeground |= GetForegroundWindow() == new WindowInteropHelper(window).Handle;
                uiPhase = "resize-" + sample;
                var resize = MeasureUi(window, () =>
                {
                    window.Width = sample % 2 == 0 ? 1100 : 820;
                    window.Height = sample % 2 == 0 ? 740 : 549;
                    MoveOffscreen(window);
                });
                becameForeground |= GetForegroundWindow() == new WindowInteropHelper(window).Handle;
                samples.Add(new { sample, input, libraryOpen = open, libraryReturn = back, resize });
            }
            report["uiSamples"] = samples;
            report["windowWasActivated"] = wasActivated;
            report["windowBecameForeground"] = becameForeground;
            if (becameForeground) throw new InvalidOperationException("The isolated window became foreground; the run is invalid.");
            report["uiMeasurement"] = "programmatic routed input/edit and real library navigation; Input-priority dispatch through layout and ContextIdle after the render queue; no hardware-input or display-scanout claim";
            report["status"] = "passed";
            return 0;
        }
        catch (Exception error)
        {
            report["error"] = error.ToString();
            return 1;
        }
        finally
        {
            try
            {
                if (library is not null) library.GetType().GetMethod("Dispose")?.Invoke(library, null);
                window?.Close();
                application?.Shutdown();
            }
            catch (Exception error) { report["cleanupError"] = error.ToString(); report["status"] = "failed"; }
            if (installationRoot is not null && personalFilesBefore is not null)
            {
                var after = PersonalFiles(installationRoot);
                var unchanged = personalFilesBefore.Count == after.Count && personalFilesBefore.All(
                    entry => after.TryGetValue(entry.Key, out var digest) && entry.Value == digest);
                report["personalServiceFilesUnchanged"] = unchanged;
                if (!unchanged) { report["status"] = "failed"; report["serviceStateError"] = "Owned personal service files changed during the isolated probe."; }
            }
            using var process = Process.GetCurrentProcess();
            report["peakWorkingSetBytes"] = process.PeakWorkingSet64;
            report["totalProcessorMs"] = process.TotalProcessorTime.TotalMilliseconds;
            if (output is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, JsonSerializer.Serialize(report, JsonOptions));
            }
        }
    }

    private static UiTiming MeasureUi(Window window, Action action)
    {
        var timer = Stopwatch.StartNew();
        var done = false;
        Exception? failure = null;
        double dispatch = 0, elapsed = 0;
        _ = window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            dispatch = timer.Elapsed.TotalMilliseconds;
            try { action(); window.UpdateLayout(); }
            catch (Exception error) { failure = error; done = true; return; }
            _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                elapsed = timer.Elapsed.TotalMilliseconds;
                done = true;
            }));
        }));
        PumpUntil(() => done);
        if (failure is not null) throw new InvalidOperationException("UI operation failed.", failure);
        return new(Math.Round(dispatch, 3), Math.Round(elapsed, 3));
    }

    private static void PumpUntil(Func<bool> completed)
    {
        if (completed()) return;
        var frame = new DispatcherFrame();
        var limit = Stopwatch.StartNew();
        var poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1) };
        poll.Tick += (_, _) => { if (completed() || limit.Elapsed > TimeSpan.FromSeconds(30)) frame.Continue = false; };
        poll.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { poll.Stop(); }
        if (!completed()) throw new TimeoutException("The isolated WPF probe did not complete within 30 seconds.");
    }

    private static double AgeMilliseconds() => (DateTime.UtcNow - ProcessStartedUtc).TotalMilliseconds;
    private static Dictionary<string, string> PersonalFiles(string root)
    {
        var directory = Path.Combine(root, "Minecraft", "Personal");
        if (!Directory.Exists(directory)) return new(StringComparer.Ordinal);
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(
            file => Path.GetRelativePath(directory, file),
            file => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))), StringComparer.Ordinal);
    }
    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) ||
                args[i] is not ("--application" or "--output" or "--state-directory" or "--label") ||
                !result.TryAdd(args[i][2..], args[i + 1])) throw new ArgumentException("Invalid or duplicate probe arguments.");
        }
        return result;
    }
    private static string RequiredAbsolute(Dictionary<string, string> options, string key)
    {
        if (!options.TryGetValue(key, out var path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("--" + key + " must be an absolute path.");
        return Path.GetFullPath(path);
    }
    private static void MoveOffscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.WindowState = WindowState.Normal;
        window.Left = -32000;
        window.Top = -32000;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero && !SetWindowPos(handle, IntPtr.Zero, -32000, -32000, 0, 0, 0x0015))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    private sealed record UiTiming(double DispatchMs, double LayoutIdleMs);
    private sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Network is forbidden in the isolated desktop UI probe: " + request.RequestUri);
    }
}
