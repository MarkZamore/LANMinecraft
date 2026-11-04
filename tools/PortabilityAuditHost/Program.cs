using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using Minecraft;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--rendering-probe")
        {
            const string switchName = "Switch.System.Windows.Media.MediaContext.DisableDirtyRectangles";
            var switchDefined = AppContext.TryGetSwitch(switchName, out var switchEnabled);
            var switches = typeof(RenderOptions).Assembly.GetType("MS.Internal.CoreAppContextSwitches", throwOnError: true)!;
            var property = switches.GetProperty("DisableDirtyRectangles", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMemberException(switches.FullName, "DisableDirtyRectangles");
            var wpfCachedDisableDirtyRectangles = (bool)property.GetValue(null)!;
            var processRenderModeBeforeApp = RenderOptions.ProcessRenderMode;
            var modeBeforeInitializeComponent = System.Windows.Interop.RenderMode.Default;
            var modeAfterInitializeComponent = System.Windows.Interop.RenderMode.Default;
            var windowsBeforeInitializeComponent = -1;
            var windowsAfterInitializeComponent = -1;
            var preparedDialogWindows = -1;
            var windowsAfterDialogCleanup = -1;
            Exception? applicationFailure = null;
            var applicationThread = new Thread(() =>
            {
                try
                {
                    // Exercise the production constructor, without Run/OnStartup:
                    // no Steam, persistence, single-instance guard or launcher window.
                    var application = new Minecraft.App();
                    modeBeforeInitializeComponent = RenderOptions.ProcessRenderMode;
                    windowsBeforeInitializeComponent = application.Windows.Count;
                    application.InitializeComponent();
                    modeAfterInitializeComponent = RenderOptions.ProcessRenderMode;
                    windowsAfterInitializeComponent = application.Windows.Count;
                    var dialogCache = typeof(Minecraft.App).Assembly.GetType("Minecraft.DialogPreloadCache", throwOnError: true)!;
                    foreach (var name in new[] { "PreparePreset", "Prepare" })
                        foreach (var step in (IEnumerable<Action>)dialogCache.GetMethod(name)!.Invoke(null, null)!) step();
                    preparedDialogWindows = application.Windows.Count;
                    dialogCache.GetMethod("Clear")!.Invoke(null, null);
                    windowsAfterDialogCleanup = application.Windows.Count;
                    application.Shutdown();
                }
                catch (Exception error) { applicationFailure = error; }
            });
            applicationThread.SetApartmentState(ApartmentState.STA);
            applicationThread.Start();
            if (!applicationThread.Join(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The windowless application rendering probe did not finish.");
            if (applicationFailure is not null)
                throw new InvalidOperationException("The windowless application rendering probe failed.", applicationFailure);
            var processRenderMode = RenderOptions.ProcessRenderMode;
            using var process = Process.GetCurrentProcess();
            var runtimeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "wpfgfx_cor3.dll", "PresentationNative_cor3.dll"
            };
            var runtimeModules = process.Modules.Cast<ProcessModule>().Where(module => runtimeNames.Contains(module.ModuleName))
                .ToDictionary(module => module.ModuleName, module => module.FileName, StringComparer.OrdinalIgnoreCase);
            var storage = args.Contains("--probe-storage", StringComparer.Ordinal) ? ProbeStorage() : null;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                switchDefined, switchEnabled, wpfCachedDisableDirtyRectangles,
                processRenderModeBeforeApp = processRenderModeBeforeApp.ToString(),
                modeBeforeInitializeComponent = modeBeforeInitializeComponent.ToString(),
                modeAfterInitializeComponent = modeAfterInitializeComponent.ToString(),
                windowsBeforeInitializeComponent, windowsAfterInitializeComponent, preparedDialogWindows, windowsAfterDialogCleanup,
                processRenderMode = processRenderMode.ToString(),
                processId = Environment.ProcessId,
                processPath = Environment.ProcessPath,
                applicationRoot = AppPaths.ResolveApplicationRoot(),
                appBaseDirectory = AppContext.BaseDirectory,
                runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location),
                entryApartment = Thread.CurrentThread.GetApartmentState().ToString(),
                temporaryDirectory = Path.GetTempPath(),
                temporaryEnvironment = Environment.GetEnvironmentVariable("TEMP"),
                temporaryEnvironmentAlias = Environment.GetEnvironmentVariable("TMP"),
                bundleExtractEnvironment = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR"),
                args,
                runtimeModules,
                storage
            }));
            Console.Out.Flush();
            // A fixture-only hold lets tests observe overlapping managed entries and
            // the native host's read-only DLL handles. It is never a launcher option.
            HoldForFixture(args);
            return switchDefined && switchEnabled && wpfCachedDisableDirtyRectangles &&
                modeBeforeInitializeComponent == System.Windows.Interop.RenderMode.SoftwareOnly &&
                modeAfterInitializeComponent == System.Windows.Interop.RenderMode.SoftwareOnly &&
                processRenderMode == System.Windows.Interop.RenderMode.SoftwareOnly &&
                windowsBeforeInitializeComponent == 0 && windowsAfterInitializeComponent == 0 &&
                preparedDialogWindows == 5 && windowsAfterDialogCleanup == 0 ? 0 : 1;
        }

        // A short-lived caller for the production updater. It is never included in the
        // launcher and never contacts Steam, GitHub, or another player. When an updater
        // restarts the disposable target, the default command records that fact and exits.
        if (args.Length == 0 || args[0].StartsWith("--skip-prestart-update=", StringComparison.Ordinal))
        {
            File.WriteAllText(Path.Combine(AppPaths.ResolveApplicationRoot(), "audit-restarted.txt"),
                JsonSerializer.Serialize(new { processId = Environment.ProcessId, processPath = Environment.ProcessPath,
                    applicationRoot = AppPaths.ResolveApplicationRoot(), appBaseDirectory = AppContext.BaseDirectory,
                    utc = DateTimeOffset.UtcNow, args }));
            return 0;
        }

        var installArguments = args.Where(argument => !argument.StartsWith("--probe-hold-ms=", StringComparison.Ordinal)).ToArray();
        if (installArguments is not ["install", var root, var currentExecutable, var manifestFile, var preparedExecutable, var mode])
            return 2;

        var paths = new AppPaths(root);
        paths.Ensure();
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(manifestFile),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing audit manifest.");
        var service = new UpdateService(paths, new Logger(paths.LogFile),
            currentCommitSha: new string('a', 40), currentExecutablePath: currentExecutable);
        Process? helper = null;
        // Observe the real helper that production StartInstall starts, without
        // changing its command, environment or ordering.
        typeof(UpdateService).GetProperty("StartPowerShellHelper", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, (Func<ProcessStartInfo, Process?>)(start => helper = Process.Start(start)));
        try
        {
            service.StartInstall(new PreparedUpdate(manifest, preparedExecutable), Enum.Parse<UpdateInstallMode>(mode));
            Console.WriteLine(JsonSerializer.Serialize(new { processId = Environment.ProcessId,
                processPath = Environment.ProcessPath, applicationRoot = AppPaths.ResolveApplicationRoot(),
                helperProcessId = helper!.Id, helperStartUtc = helper.StartTime.ToUniversalTime(), args }));
            Console.Out.Flush();
            HoldForFixture(args);
        }
        finally { helper?.Dispose(); }
        return 0;
    }

    private static void HoldForFixture(string[] args)
    {
        foreach (var argument in args)
            if (argument.StartsWith("--probe-hold-ms=", StringComparison.Ordinal) &&
                int.TryParse(argument.AsSpan("--probe-hold-ms=".Length), out var hold) && hold is >= 0 and <= 10000)
                Thread.Sleep(hold);
    }

    private static object ProbeStorage()
    {
        var paths = new AppPaths(AppPaths.ResolveApplicationRoot());
        paths.Ensure();
        var assembly = typeof(Minecraft.App).Assembly;
        var writer = assembly.GetType("Minecraft.AtomicFile", throwOnError: true)!
            .GetMethod("WriteAllText", BindingFlags.Static | BindingFlags.Public)!;
        var directories = new[] { Path.GetTempPath(), paths.Personal, Path.Combine(paths.Launcher, "Library"),
            Path.Combine(paths.Launcher, "ComponentCatalog"), Path.Combine(paths.Personal, "Updates") };
        var written = new List<string>();
        foreach (var directory in directories)
        {
            var destination = Path.Combine(directory, "native-storage-probe.txt");
            // Both publication and replacement exercise the actual atomic writer
            // while native ancestor and runtime DLL handles remain held.
            writer.Invoke(null, [destination, "first", null]);
            writer.Invoke(null, [destination, "replacement", null]);
            if (File.ReadAllText(destination) != "replacement") throw new IOException("The native fixture storage replacement failed.");
            written.Add(destination);
        }
        var runtime = Path.Combine(paths.Launcher, "Runtime");
        var generations = new List<string>();
        for (var index = 0; index < 4; index++)
        {
            var hash = new string((char)('b' + index), 64);
            var generation = Path.Combine(runtime, hash);
            Directory.CreateDirectory(generation);
            File.WriteAllText(Path.Combine(generation, "owned-fixture.txt"), "Disposable owned generation.");
            File.WriteAllText(generation + ".lease", $"LMHOST01\n{hash}\n", new UTF8Encoding(false));
            Directory.SetLastWriteTimeUtc(generation, DateTime.UtcNow.AddDays(-10 - index));
            generations.Add(generation);
        }
        assembly.GetType("Minecraft.PortableRuntimeCache", throwOnError: true)!
            .GetMethod("Cleanup", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [paths]);
        return new
        {
            written,
            retained = generations.Select(Directory.Exists).ToArray(),
            ownershipLeasesRetained = generations.All(path => File.Exists(path + ".lease")),
            activeGenerationRetained = Directory.Exists(AppContext.BaseDirectory) && File.Exists(typeof(object).Assembly.Location)
        };
    }
}
