using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media;
using Minecraft;

if (args is ["--rendering-probe"])
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
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        switchDefined, switchEnabled, wpfCachedDisableDirtyRectangles,
        processRenderModeBeforeApp = processRenderModeBeforeApp.ToString(),
        modeBeforeInitializeComponent = modeBeforeInitializeComponent.ToString(),
        modeAfterInitializeComponent = modeAfterInitializeComponent.ToString(),
        windowsBeforeInitializeComponent, windowsAfterInitializeComponent,
        processRenderMode = processRenderMode.ToString()
    }));
    return switchDefined && switchEnabled && wpfCachedDisableDirtyRectangles &&
        modeBeforeInitializeComponent == System.Windows.Interop.RenderMode.SoftwareOnly &&
        modeAfterInitializeComponent == System.Windows.Interop.RenderMode.SoftwareOnly &&
        processRenderMode == System.Windows.Interop.RenderMode.SoftwareOnly &&
        windowsBeforeInitializeComponent == 0 && windowsAfterInitializeComponent == 0 ? 0 : 1;
}

// A short-lived caller for the production updater. It is never included in the
// launcher and never contacts Steam, GitHub, or another player. When an updater
// restarts the disposable target, the default command records that fact and exits.
if (args.Length == 0 || args[0].StartsWith("--skip-prestart-update=", StringComparison.Ordinal))
{
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "audit-restarted.txt"),
        JsonSerializer.Serialize(new { processId = Environment.ProcessId, utc = DateTimeOffset.UtcNow, args }));
    return 0;
}

if (args is not ["install", var root, var currentExecutable, var manifestFile, var preparedExecutable, var mode])
    return 2;

var paths = new AppPaths(root);
paths.Ensure();
var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(manifestFile),
    new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing audit manifest.");
var service = new UpdateService(paths, new Logger(paths.LogFile),
    currentCommitSha: new string('a', 40), currentExecutablePath: currentExecutable);
service.StartInstall(new PreparedUpdate(manifest, preparedExecutable), Enum.Parse<UpdateInstallMode>(mode));
return 0;
