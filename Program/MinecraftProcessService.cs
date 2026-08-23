using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Collections.Concurrent;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;

namespace Minecraft;

public sealed class MinecraftProcessService
{
    /// <summary>
    /// Options the modded stack needs on Java 24 and later. JEP 472 warns on every
    /// JNI call from unenabled code (LWJGL, JNA and Netty all use it) and JEP 498
    /// warns once per sun.misc.Unsafe call site, which a 450-mod stack turns into a
    /// wall of log noise. The runtime service also probes these before installing,
    /// so a runtime that rejects them fails with a message instead of a silent exit.
    /// </summary>
    /// <summary>
    /// Prepares the environment the game inherits from the launcher.
    ///
    /// The launcher turns Steam's overlay off for itself: a WPF window gives it
    /// nothing to hook. That setting is process-wide, and a child process gets a
    /// copy of it - which silently took Shift+Tab away from the game, where the
    /// overlay is the whole point, because inviting a friend into a Steam
    /// session happens through it.
    /// </summary>
    internal static void ConfigureChildEnvironment(IDictionary<string, string?> environment, string javaTempDir)
    {
        environment["TEMP"] = javaTempDir;
        environment["TMP"] = javaTempDir;
        environment.Remove(SteamworksApiFacade.NoOverlayVariable);
    }

    /// <summary>
    /// The options one feature release of Java needs to run a modded game, and
    /// none it does not: a JVM refuses to start on an option it never heard of,
    /// so this list follows the pinned runtime rather than leading it.
    /// </summary>
    /// <param name="javaMajorVersion">The feature release the game will run on.</param>
    public static IReadOnlyList<string> CompatibilityArgumentsFor(int javaMajorVersion)
    {
        var arguments = new List<string>();
        if (javaMajorVersion >= 24)
        {
            // JEP 472 and JEP 498 turned the native calls and the sun.misc.Unsafe
            // memory access a modded stack lives on into warnings, then errors.
            arguments.Add("--illegal-native-access=allow");
            arguments.Add("--enable-native-access=ALL-UNNAMED");
            arguments.Add("--sun-misc-unsafe-memory-access=allow");
        }
        if (javaMajorVersion >= 25)
        {
            // Product flag since JDK 25 (JEP 519): smaller object headers typically
            // cut a modded heap by 10-20%, which means fewer and shorter G1 cycles.
            arguments.Add("-XX:+UseCompactObjectHeaders");
        }
        return arguments.AsReadOnly();
    }

    /// <summary>
    /// What the game is launched with on the pinned runtime, and what the
    /// install-time flag probe checks that runtime against. Java 21 is the
    /// release 1.21.1 and its mods were built for and needs none of the above.
    /// </summary>
    public static IReadOnlyList<string> JavaCompatibilityArguments { get; } =
        CompatibilityArgumentsFor(PortableJavaRuntimeService.PinnedMajorVersion);

    private readonly GameLogConfigurationService _gameLogConfiguration;
    private readonly AppPaths _paths;
    private readonly ClientPresenceService _presence;
    private readonly Logger _logger;
    private readonly IIdentityService _identityService;
    private readonly PortableIdentityAdapterService _identityAdapter;
    private readonly WorldPlayerProfileService _playerProfiles;
    private readonly PackInstanceService _packInstances;
    private readonly PackRuntimeService _packRuntimes;
    private readonly ManagedComponentService _managedComponents;
    private readonly WaypointSyncService _waypointSync;
    private readonly SkinService _skinService;
    private readonly MinecraftWindowPlacementService _gameWindowPlacement;
    private readonly ConcurrentDictionary<int, byte> _activeClientProcesses = new();
    private int _clientPreparing;
    private string _lastJavaPath = "";
    private string _lastGameVersion = "";
    private string _lastProfileId = "";

    public bool IsClientRunning => !_activeClientProcesses.IsEmpty;
    public bool IsClientPreparing => Volatile.Read(ref _clientPreparing) != 0;
    public string DiagnosticJavaPath => Volatile.Read(ref _lastJavaPath);
    public string DiagnosticGameVersion => Volatile.Read(ref _lastGameVersion);
    public string DiagnosticProfileId => Volatile.Read(ref _lastProfileId);
    public event Action<bool>? ClientRunningChanged;

    /// <summary>
    /// The game died for want of memory, carrying the size it was given. Worth
    /// its own event: it is the one ending the player can do something about.
    /// </summary>
    public event Action<int>? ClientRanOutOfMemory;

    /// <summary>What the log says when the heap is spent.</summary>
    private const string OutOfMemoryMarker = "OutOfMemoryError";
    public event Action<bool>? ClientPreparingChanged;

    public MinecraftProcessService(
        AppPaths paths,
        Logger logger,
        IIdentityService identityService,
        PortableIdentityAdapterService identityAdapter,
        WorldPlayerProfileService playerProfiles,
        PackInstanceService packInstances,
        PackRuntimeService packRuntimes,
        WaypointSyncService waypointSync,
        SkinService skinService,
        ManagedComponentService? managedComponents = null)
    {
        _paths = paths;
        _logger = logger;
        _presence = new ClientPresenceService(paths, logger);
        _gameLogConfiguration = new GameLogConfigurationService(logger);
        _identityService = identityService;
        _identityAdapter = identityAdapter;
        _playerProfiles = playerProfiles;
        _packInstances = packInstances;
        _packRuntimes = packRuntimes;
        _managedComponents = managedComponents ?? new ManagedComponentService(paths, logger);
        _waypointSync = waypointSync;
        _skinService = skinService;
        _gameWindowPlacement = new MinecraftWindowPlacementService(paths, logger);
    }

    public async Task StartClientAsync(
        AppSettings settings,
        IProgress<RuntimePreparationProgress>? runtimeProgress = null,
        CancellationToken token = default)
    {
        if (IsClientRunning)
        {
            throw new InvalidOperationException("Minecraft is already running from this application.");
        }
        if (Interlocked.CompareExchange(ref _clientPreparing, 1, 0) != 0)
        {
            throw new InvalidOperationException("Minecraft is already being prepared.");
        }

        NotifyClientPreparingChanged(true);
        try
        {
            await StartClientCoreAsync(settings, runtimeProgress, token).ConfigureAwait(false);
        }
        finally
        {
            _packRuntimes.CleanupLaunchTemporaryFiles();
            Interlocked.Exchange(ref _clientPreparing, 0);
            NotifyClientPreparingChanged(false);
        }
    }

    private async Task StartClientCoreAsync(
        AppSettings settings,
        IProgress<RuntimePreparationProgress>? runtimeProgress,
        CancellationToken token)
    {
        if (IsClientRunning)
        {
            throw new InvalidOperationException("Minecraft is already running from this application.");
        }
        var packDir = _paths.CombineUnderPacks(settings.ClientRelativePath);
        if (!HasPackData(packDir))
        {
            throw new DirectoryNotFoundException($"Minecraft pack folder has no {PackManifestService.ManifestFileName}: {packDir}");
        }

        var descriptor = PackManifestService.Load(packDir);
        var identityContext = _identityService.ResolveContext(settings);
        var runtime = await _packRuntimes.PrepareAsync(settings.ClientRelativePath, runtimeProgress, token);
        Volatile.Write(ref _lastJavaPath, runtime.JavaPath);
        Volatile.Write(ref _lastGameVersion, runtime.Descriptor.MinecraftVersion);
        Volatile.Write(ref _lastProfileId, runtime.ProfileId);
        if (!string.Equals(runtime.Descriptor.DescriptorHash, descriptor.DescriptorHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Pack manifest changed while its runtime was being prepared. Start the game again.");
        }
        var instance = await _packInstances.PrepareAsync(settings.ClientRelativePath, token);
        // PackInstanceService mirrors the pack over the instance mods folder and
        // deletes anything the pack does not carry, so launcher-owned JARs have
        // to be (re)installed after every prepare - the order here is load-bearing.
        if (SteamPlayPolicy.IsSupported(descriptor))
        {
            await _managedComponents
                .EnsureSteamTransportModAsync(instance, token)
                .ConfigureAwait(false);
        }
        var identityJvmArguments = await _identityAdapter
            .PrepareJvmArgumentsAsync(runtime, instance.GameDirectory, token)
            .ConfigureAwait(false);
        var skinRegistryPath = _skinService.PrepareRegistry(settings, identityContext);
        var gameDir = instance.GameDirectory;
        EnsureWorldsDirectoryAndSavesLink(gameDir);
        ValidatePackCompatibility(packDir);
        EnsureModernFixShutdownWorkaround(gameDir);
        _playerProfiles.PrepareWorldsForLaunch(_paths.Worlds, identityContext);
        // After the profiles are written: the reset edits playerdata and the
        // Player compound of level.dat in place, and the game reads them on join.
        ResetPlayerModelsIfAsked(packDir);
        await _waypointSync.PrepareForLaunchAsync(settings.ClientRelativePath, identityContext, token).ConfigureAwait(false);

        var launcher = _packRuntimes.CreateLocalLauncher(runtime);
        var profile = await launcher.GetVersionAsync(runtime.ProfileId, token);
        var runtimePath = launcher.MinecraftPath;
        var launchPath = new MinecraftPath(gameDir)
        {
            Library = runtimePath.Library,
            Versions = runtimePath.Versions,
            Assets = runtimePath.Assets,
            Resource = runtimePath.Resource,
            Runtime = runtimePath.Runtime
        };
        launchPath.CreateDirs();

        var javaTempDir = Path.Combine(_paths.Personal, "Temp", "Java", settings.ClientRelativePath);
        _paths.EnsureUnderRoot(javaTempDir);
        Directory.CreateDirectory(javaTempDir);
        var session = MSession.CreateOfflineSession(identityContext.IdentityName);
        session.UUID = identityContext.MinecraftUuid;
        session.AccessToken = identityContext.SessionAccessToken;
        session.UserType = "mojang";
        session.Xuid = "";
        // The setting is everything the game may take. The heap gets what is
        // left after the room this pack keeps beside it - class data, compiled
        // code, thread stacks, and the buffers Sodium hands the driver - and
        // that room is weighed from the pack on disk rather than assumed, so
        // vanilla on an old version and a pack of nine hundred mods each get
        // the split they deserve out of the same number.
        var packMemory = PackMemoryProfile.Measure(packDir);
        var heapGb = MemorySizingService.GetHeapGb(packMemory, settings.MaxMemoryGb);
        var maximumRamMb = checked(heapGb * 1024);
        _logger.Info(
            $"Memory: {settings.MaxMemoryGb} GB for the game ({packMemory.ModCount} mods, " +
            $"Minecraft {descriptor.MinecraftVersion}), of which {heapGb} GB is the Java heap.");
        var smallestUsefulBudgetGb = MemorySizingService.GetSmallestUsefulBudgetGb(packMemory);
        if (settings.MaxMemoryGb < smallestUsefulBudgetGb)
        {
            _logger.Warn(
                $"This pack holds about {MemorySizingService.GetNativeReserveGb(packMemory)} GB outside its heap, " +
                $"so {settings.MaxMemoryGb} GB is under the {smallestUsefulBudgetGb} GB it takes to stay inside that number.");
        }
        var extraJvmArguments = new List<MArgument>
        {
            new("-Dfile.encoding=UTF-8"),
            new("-Djava.net.preferIPv4Stack=true"),
            new("-Djava.net.preferIPv6Addresses=false"),
            new($"-Djava.io.tmpdir={javaTempDir}"),
            new($"-Dminecraft.portable.skin.registry={skinRegistryPath}"),
            // ModernFix's lazy model loading, as a JVM property. Without it a
            // player on 8 GB of heap ran out of memory before the world had
            // finished loading its data packs - the vanilla path holds every
            // model of 849 mods live at once. It was taken out once on the
            // suspicion that it live-locked resource loading; a thread dump
            // later put that on Lightspeed's parallel lookup alone.
            //
            // A property rather than the config file because ModernFix rewrites
            // that file on every launch, so a pack copy can never reach an
            // instance without the sync flagging it as a local edit.
            new("-Dmodernfix.config.mixin.perf.dynamic_resources=true"),
            // Die at the first OutOfMemoryError instead of carrying on.
            //
            // The game's own answer to running out of memory while a world's
            // data packs load is a screen offering to open that world with the
            // vanilla data pack alone - and a modded world opened without its
            // data packs loses every block, item and entity they define the
            // moment it saves. The offer arrives seconds before the "out of
            // memory" screen does, so the dangerous button is the one a player
            // sees first. A JVM that exits on the spot never shows either, and
            // the launcher says what happened instead.
            new("-XX:+ExitOnOutOfMemoryError"),
            // G1 tuned for a big modded heap. Left alone it grew the committed
            // heap from 2 GB to 9.4 GB in one session in resize steps, each a
            // pause a player feels as a stutter; Xms=Xmx below ends that. The
            // 16 GB heap also defaults to 8 MB regions, which makes every
            // block-palette or LOD array over 4 MB a "humongous" allocation
            // with its own slow path - 32 MB regions put them back on the
            // normal one. The pause goal keeps mixed collections under a
            // frame and a half instead of the default 200 ms.
            new("-XX:MaxGCPauseMillis=40"),
            new("-XX:G1NewSizePercent=20"),
            new("-XX:G1ReservePercent=15"),
            new("-XX:G1HeapRegionSize=32M"),
            // A mod calling System.gc() gets a concurrent cycle, not a
            // stop-the-world full collection. None is caught doing it in the
            // logs; this is insurance priced at one flag.
            new("-XX:+ExplicitGCInvokesConcurrent")
        };
        var gameLogArgument = _gameLogConfiguration.PrepareArgument(gameDir, packDir);
        if (gameLogArgument is not null) extraJvmArguments.Add(new MArgument(gameLogArgument));
        extraJvmArguments.AddRange(JavaCompatibilityArguments.Select(argument => new MArgument(argument)));
        extraJvmArguments.AddRange(identityJvmArguments.Select(argument => new MArgument(argument)));
        var launchOption = new MLaunchOption
        {
            Path = launchPath,
            JavaPath = runtime.JavaPath,
            Session = session,
            MaximumRamMb = maximumRamMb,
            // Equal to the maximum on purpose: a heap that starts small grows
            // toward Xmx in live resize steps, and a 16 GB heap was measured
            // doing that from 2 GB to 9.4 GB across one session, each step a
            // stutter. The memory was already promised to the game; committing
            // it up front costs nothing the player had not agreed to.
            MinimumRamMb = maximumRamMb,
            GameLauncherName = "LANMinecraft",
            GameLauncherVersion = "1",
            VersionType = $"{descriptor.Loader.Type} {descriptor.Loader.Version}".Trim(),
            FullScreen = false,
            ExtraJvmArguments = extraJvmArguments
        };
        var minecraftProcess = launcher.BuildProcess(profile, launchOption);
        minecraftProcess.StartInfo.WorkingDirectory = gameDir;
        minecraftProcess.StartInfo.UseShellExecute = false;
        minecraftProcess.StartInfo.CreateNoWindow = true;
        minecraftProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
        // A JVM that refuses an option writes to stderr before log4j exists, so
        // without these pipes the only trace of the failure would be an exit code.
        minecraftProcess.StartInfo.RedirectStandardOutput = true;
        minecraftProcess.StartInfo.RedirectStandardError = true;
        ConfigureChildEnvironment(minecraftProcess.StartInfo.Environment, javaTempDir);
        var startupOutput = new StartupOutputBuffer();
        startupOutput.MirrorTo(gameDir);
        minecraftProcess.OutputDataReceived += (_, e) => startupOutput.Append(e.Data);
        minecraftProcess.ErrorDataReceived += (_, e) => startupOutput.Append(e.Data);

        var started = false;
        try
        {
            if (!minecraftProcess.Start())
            {
                throw new InvalidOperationException("Minecraft process could not be started.");
            }
            // The pipes must be drained for the whole session or the game blocks
            // once a buffer fills, so the process outlives this method.
            minecraftProcess.BeginOutputReadLine();
            minecraftProcess.BeginErrorReadLine();
            started = true;
        }
        finally
        {
            if (!started) minecraftProcess.Dispose();
        }

        var processId = minecraftProcess.Id;
        // Written down before anything else can go wrong: a launcher restarted
        // while this game plays reads it and knows not to offer another.
        _presence.Remember(minecraftProcess, settings.ClientRelativePath);
        if (_activeClientProcesses.TryAdd(processId, 0) && _activeClientProcesses.Count == 1)
        {
            NotifyClientRunningChanged(true);
        }
        // The monitor owns the Process object from here on and may dispose it
        // before the startup window closes, so the exit code travels through
        // the completion source instead of the Process.
        var exitCode = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = MonitorClientExitAsync(
            minecraftProcess, settings.ClientRelativePath, exitCode, startupOutput, gameDir, settings.MaxMemoryGb);

        await Task.WhenAny(exitCode.Task, Task.Delay(TimeSpan.FromSeconds(2), token));
        token.ThrowIfCancellationRequested();
        if (exitCode.Task.IsCompletedSuccessfully && exitCode.Task.Result != 0)
        {
            throw new InvalidOperationException(
                $"Minecraft exited during startup with code {exitCode.Task.Result}." +
                startupOutput.Describe() +
                ReadLatestLogTail(gameDir));
        }

        _logger.Info($"Minecraft client started with profile {runtime.ProfileId}.");
    }

    private async Task MonitorClientExitAsync(
        Process process,
        string packRelativePath,
        TaskCompletionSource<int> exitCode,
        StartupOutputBuffer startupOutput,
        string gameDir,
        int maxMemoryGb)
    {
        var processId = process.Id;
        try
        {
            using var owned = process;
            using var placementCancellation = new CancellationTokenSource();
            var placementTask = _gameWindowPlacement.TrackAsync(processId, placementCancellation.Token);
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            finally
            {
                // A game that dies after the two second startup window used to
                // leave no trace at all: the window had closed, so nobody
                // reported the exit code and nobody kept what the process said
                // on its way out. Everything known about it goes to the log the
                // moment it happens.
                ReportUnexpectedExit(process, startupOutput, gameDir, maxMemoryGb);
                // The pipes are done; what they carried is on disk for a report.
                startupOutput.Close();
                // Published before any cleanup so the launch method never has
                // to read the Process object this monitor is about to dispose.
                TryPublishExitCode(process, exitCode);
                placementCancellation.Cancel();
                await placementTask.ConfigureAwait(false);
            }
            await _waypointSync.FlushAsync().ConfigureAwait(false);
            await _packInstances.CleanupGeneratedLocalArtifactsAsync(packRelativePath, process.ExitCode == 0).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            await _waypointSync.FlushAsync().ConfigureAwait(false);
            await _packInstances.CleanupGeneratedLocalArtifactsAsync(packRelativePath, removeSessionLogs: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not clean generated local instance data after Minecraft exited: {ex.Message}");
        }
        finally
        {
            // A monitor that failed before publishing must still release the
            // startup wait; 0 keeps the crash path silent rather than inventing
            // an exit code for a process whose state is unknown.
            exitCode.TrySetResult(0);
            _presence.Forget(processId);
            CleanupJavaTemporaryDirectory(packRelativePath);
            if (_activeClientProcesses.TryRemove(processId, out _) && _activeClientProcesses.IsEmpty)
            {
                NotifyClientRunningChanged(false);
            }
        }
    }

    private void ReportUnexpectedExit(
        Process process, StartupOutputBuffer startupOutput, string gameDir, int maxMemoryGb)
    {
        try
        {
            if (process.ExitCode == 0) return;
            var tail = ReadLatestLogTail(gameDir);
            _logger.Warn(
                $"Minecraft exited with code {process.ExitCode}." +
                startupOutput.Describe() +
                tail);
            // Out of memory is the one ending a player can act on, and the one
            // the game itself would have answered with a dangerous offer, so it
            // is said in the window rather than left in the log.
            if (tail.Contains(OutOfMemoryMarker, StringComparison.Ordinal))
            {
                _logger.Warn($"The game ran out of the {maxMemoryGb} GB it was given.");
                ClientRanOutOfMemory?.Invoke(maxMemoryGb);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            // A process whose exit code cannot be read tells us nothing worth
            // failing the cleanup over.
        }
    }

    private static void TryPublishExitCode(Process process, TaskCompletionSource<int> exitCode)
    {
        try
        {
            exitCode.TrySetResult(process.ExitCode);
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Keeps the tail of the game's own console output so a JVM that dies before
    /// log4j starts still explains itself. Bounded because the game runs for hours.
    /// </summary>
    private sealed class StartupOutputBuffer
    {
        private const int MaximumCharacters = 8 * 1024;

        /// <summary>
        /// Everything the game wrote to its console, kept beside its own logs.
        ///
        /// log4j only starts once the JVM is up and the loader is running; a
        /// heap setting the JVM refuses, a missing native, a loader that dies
        /// before it opens latest.log all happen before that and used to leave
        /// nothing but an exit code. The pipes were already being drained to
        /// keep the game from blocking, so this only writes down what was
        /// passing through, and a report carries it.
        /// </summary>
        internal const string FileName = "launcher-console.log";

        /// <summary>Enough for any startup; a session that talks more than this loses the oldest.</summary>
        private const long MaximumFileBytes = 4L * 1024 * 1024;

        private readonly Lock _gate = new();
        private readonly StringBuilder _lines = new();
        private StreamWriter? _file;
        private long _written;

        /// <summary>Starts the file copy; failure to open one is never fatal to a launch.</summary>
        public void MirrorTo(string gameDirectory)
        {
            try
            {
                var logs = Path.Combine(gameDirectory, "logs");
                Directory.CreateDirectory(logs);
                var writer = new StreamWriter(
                    new FileStream(
                        Path.Combine(logs, FileName),
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.ReadWrite),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true
                };
                lock (_gate) _file = writer;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // The game still runs and still logs; only this copy is missing.
            }
        }

        /// <summary>Closes the file copy once the process that fed it is gone.</summary>
        public void Close()
        {
            StreamWriter? writer;
            lock (_gate)
            {
                writer = _file;
                _file = null;
            }

            try
            {
                writer?.Dispose();
            }
            catch (IOException)
            {
            }
        }

        public void Append(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (_gate)
            {
                _lines.AppendLine(line);
                if (_lines.Length > MaximumCharacters)
                {
                    _lines.Remove(0, _lines.Length - MaximumCharacters);
                }

                if (_file is null || _written >= MaximumFileBytes) return;
                try
                {
                    _file.WriteLine(line);
                    _written += line.Length + 2;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    _file = null;
                }
            }
        }

        public string Describe()
        {
            lock (_gate)
            {
                return _lines.Length == 0
                    ? ""
                    : Environment.NewLine + _lines.ToString().TrimEnd();
            }
        }
    }

    private void CleanupJavaTemporaryDirectory(string packRelativePath)
    {
        try
        {
            var tempRoot = Path.GetFullPath(Path.Combine(_paths.Personal, "Temp"));
            var javaRoot = Path.GetFullPath(Path.Combine(tempRoot, "Java"));
            var target = Path.GetFullPath(Path.Combine(javaRoot, packRelativePath));
            if (!target.StartsWith(javaRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            TryDeleteDirectoryIfEmpty(javaRoot);
            TryDeleteDirectoryIfEmpty(tempRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Minecraft temporary files could not be removed: {ex.Message}");
        }
    }

    private static void TryDeleteDirectoryIfEmpty(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
    }

    /// <summary>
    /// Picks up the games a previous launcher left running, so this one shows
    /// "Игра запущена" instead of offering to start a second client over the
    /// first. Returns how many were found.
    /// </summary>
    public int AdoptRunningClients()
    {
        var adopted = 0;
        foreach (var session in _presence.ReadLiveSessions())
        {
            Process process;
            try
            {
                process = Process.GetProcessById(session.ProcessId);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                continue;
            }
            if (!_activeClientProcesses.TryAdd(session.ProcessId, 0)) { process.Dispose(); continue; }
            adopted++;
            _logger.Info($"A game started by an earlier launcher is still running (process {session.ProcessId}, {session.PackRelativePath}).");
            var exitCode = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            // A game this launcher did not start: its size was another
            // launcher's setting, so 0 stands for "not known" and the ending is
            // still named, without a number that might be someone else's.
            _ = MonitorClientExitAsync(process, session.PackRelativePath, exitCode, new StartupOutputBuffer(),
                _paths.CombineUnderInstances(session.PackRelativePath), maxMemoryGb: 0);
        }
        if (adopted > 0) NotifyClientRunningChanged(true);
        return adopted;
    }

    private void NotifyClientRunningChanged(bool isRunning)
    {
        try
        {
            ClientRunningChanged?.Invoke(isRunning);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Minecraft process state listener failed: {ex.Message}");
        }
    }

    private void NotifyClientPreparingChanged(bool isPreparing)
    {
        try
        {
            ClientPreparingChanged?.Invoke(isPreparing);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Minecraft preparation state listener failed: {ex.Message}");
        }
    }

    public static bool HasPackData(string packDirectory) => PackManifestService.HasManifest(packDirectory);

    /// <summary>
    /// The pack can ask, once, that every player of every world go back to the
    /// plain Steve model; the mod keeps that choice on the player, so a file in
    /// the pack cannot make it - the launcher has to.
    /// </summary>
    private void ResetPlayerModelsIfAsked(string packDir)
    {
        if (!Directory.Exists(_paths.Worlds)) return;
        var reset = new PlayerModelResetService(_logger);
        foreach (var world in Directory.EnumerateDirectories(_paths.Worlds))
        {
            if (!File.Exists(Path.Combine(world, "level.dat"))) continue;
            reset.Apply(packDir, world);
        }
    }

    private void EnsureWorldsDirectoryAndSavesLink(string clientDir)
    {
        Directory.CreateDirectory(_paths.Worlds);
        var savesDir = Path.Combine(clientDir, "saves");
        if (TryGetAttributes(savesDir, out var attributes))
        {
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                var target = new DirectoryInfo(savesDir).LinkTarget;
                if (IsSamePath(target, _paths.Worlds, clientDir)) return;
                Directory.Delete(savesDir);
            }
            else if (Directory.Exists(savesDir) && !Directory.EnumerateFileSystemEntries(savesDir).Any())
            {
                Directory.Delete(savesDir);
            }
            else
            {
                _logger.Warn("Minecraft saves folder already exists and is not a link; leaving it unchanged to avoid moving worlds.");
                return;
            }
        }

        CreateJunction(savesDir, _paths.Worlds);
        _logger.Info("Minecraft saves folder linked to portable Worlds folder.");
    }

    private static void ValidatePackCompatibility(string packDir)
    {
        ValidateKubeJsArsNouveauPackMetadata(packDir);
    }

    private static void ValidateKubeJsArsNouveauPackMetadata(string packDir)
    {
        var modsDir = Path.Combine(packDir, "mods");
        if (!Directory.Exists(modsDir)) return;
        foreach (var jarPath in Directory.EnumerateFiles(modsDir, "kubejsarsnouveau-*.jar", SearchOption.TopDirectoryOnly))
        {
            using var archive = ZipFile.OpenRead(jarPath);
            var metadataEntry = archive.GetEntry("pack.mcmeta");
            if (metadataEntry is null) continue;
            using var reader = new StreamReader(metadataEntry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var metadata = reader.ReadToEnd();
            if (metadata.Contains("${pack_format_number}", StringComparison.Ordinal) ||
                metadata.Contains("${mod_id}", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Pack preparation is required: unresolved pack.mcmeta placeholders in {Path.GetFileName(jarPath)}.");
            }
        }
    }

    private void EnsureModernFixShutdownWorkaround(string gameDir)
    {
        var configPath = Path.Combine(gameDir, "config", "modernfix-mixins.properties");
        if (!File.Exists(configPath)) return;
        const string key = "mixin.perf.dedicated_reload_executor";
        const string expected = key + "=false";
        var lines = File.ReadAllLines(configPath).ToList();
        var found = false;
        var changed = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('#') || !line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) continue;
            found = true;
            if (string.Equals(line, expected, StringComparison.OrdinalIgnoreCase)) continue;
            lines[i] = expected;
            changed = true;
        }
        if (!found)
        {
            lines.Add(expected);
            changed = true;
        }
        if (!changed) return;
        File.WriteAllLines(configPath, lines);
        _logger.Info("ModernFix dedicated reload executor disabled for reliable singleplayer shutdown.");
    }

    private static string ReadLatestLogTail(string gameDir)
    {
        try
        {
            var path = Path.Combine(gameDir, "logs", "latest.log");
            if (!File.Exists(path)) return "";
            var lines = File.ReadLines(path).TakeLast(30);
            var details = string.Join(Environment.NewLine, lines);
            return details.Length == 0 ? "" : Environment.NewLine + details;
        }
        catch
        {
            return "";
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (IOException)
        {
            attributes = default;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            attributes = default;
            return false;
        }
    }

    private static bool IsSamePath(string? linkTarget, string expectedTarget, string linkParent)
    {
        if (string.IsNullOrWhiteSpace(linkTarget)) return false;
        var resolvedTarget = Path.IsPathRooted(linkTarget)
            ? Path.GetFullPath(linkTarget)
            : Path.GetFullPath(Path.Combine(linkParent, linkTarget));
        return string.Equals(
            resolvedTarget.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(expectedTarget).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start mklink.");
        process.WaitForExit();
        if (process.ExitCode == 0) return;
        throw new InvalidOperationException(
            $"Could not create directory link: {process.StandardError.ReadToEnd()}{process.StandardOutput.ReadToEnd()}".Trim());
    }
}
