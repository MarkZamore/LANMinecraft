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

    /// <summary>
    /// How the collector is asked to behave on a heap this size.
    ///
    /// The pause goal replaces G1's default 200 ms, which is four frames; the
    /// reserve keeps a full heap from failing an evacuation; and the region
    /// size matters most - a 16 GB heap defaults to 8 MB regions, so every
    /// block-palette or LOD array over 4 MB becomes a "humongous" allocation on
    /// a slower path, and a modded world makes those constantly.
    ///
    /// Every entry must be a product option. An experimental one needs
    /// -XX:+UnlockExperimentalVMOptions written before it or the JVM refuses to
    /// start at all: G1NewSizePercent shipped here for one release and players
    /// got "Could not create the Java Virtual Machine" instead of a game. Check
    /// a new flag with `java &lt;flag&gt; -version` on the pinned runtime before
    /// adding it here.
    /// </summary>
    public static IReadOnlyList<string> HeapTuningArguments { get; } =
    [
        "-XX:MaxGCPauseMillis=40",
        "-XX:G1ReservePercent=15",
        "-XX:G1HeapRegionSize=32M",
        "-XX:+ExplicitGCInvokesConcurrent"
    ];

    /// <summary>
    /// At or below this heap the machine is short of memory rather than short
    /// of frames, and the tuning above stops being the right answer.
    /// </summary>
    public const int SmallHeapCeilingMb = 4 * 1024;

    /// <summary>
    /// How the collector is asked to behave when there is barely a heap.
    ///
    /// The list above is written for twelve and sixteen gigabytes, where the
    /// enemy is a pause somebody sees. Down here the enemy is the machine: a
    /// laptop of eight gigabytes running a heap of three has no room for a
    /// collector that keeps a large young generation and reserves a sixth of
    /// the heap on top, and every choice that buys smoothness there buys
    /// paging here - which is not a stutter but a game that stops for seconds.
    ///
    /// So the pause goal is relaxed from four frames to about nine, which lets
    /// G1 collect less often and less desperately; the young generation is
    /// pinned to a fifth of the heap rather than left to grow to three fifths;
    /// regions go back to eight megabytes, because thirty-two of them is a
    /// tenth of a small heap in a single region; and the concurrent cycle is
    /// started at a fifth rather than the default's much later, since a small
    /// heap fills between one cycle and the next.
    ///
    /// System.gc() is refused outright rather than made concurrent. A mod that
    /// calls it on a machine this tight buys a full collection nobody asked
    /// for, and there is no version of that which ends well.
    ///
    /// Two of these are experimental options, which is exactly the mistake this
    /// file has made before - so the unlock is written first, in this list, and
    /// the order is pinned by a test. The whole set was run through
    /// `java <flag> -version` on the pinned runtime before it was written down.
    /// </summary>
    public static IReadOnlyList<string> SmallHeapTuningArguments { get; } =
    [
        "-XX:+UseG1GC",
        "-XX:+ParallelRefProcEnabled",
        "-XX:MaxGCPauseMillis=150",
        "-XX:+UnlockExperimentalVMOptions",
        "-XX:G1NewSizePercent=20",
        "-XX:G1MaxNewSizePercent=40",
        "-XX:G1HeapRegionSize=8M",
        "-XX:G1ReservePercent=20",
        "-XX:InitiatingHeapOccupancyPercent=20",
        "-XX:+DisableExplicitGC"
    ];

    /// <summary>The tuning a heap of this size is started with.</summary>
    public static IReadOnlyList<string> HeapTuningArgumentsFor(int maximumRamMb) =>
        maximumRamMb <= SmallHeapCeilingMb ? SmallHeapTuningArguments : HeapTuningArguments;

    /// <summary>
    /// What the heap starts at, which is not always what it may reach.
    /// </summary>
    /// <remarks>
    /// A large heap starts at its maximum on purpose: one that starts small
    /// grows toward Xmx in live resize steps, and a 16 GB heap was measured
    /// doing that from 2 GB to 9.4 GB across one session, each step a stutter.
    /// The memory was already promised to the game.
    ///
    /// A small heap must not. On a machine with eight gigabytes, committing
    /// three and a half of them in the first instant - before a single chunk is
    /// drawn - is what takes the machine down, and the promise that made it
    /// harmless on a large machine is the opposite of harmless here: there was
    /// nothing spare to promise. It starts at a gigabyte and grows into what it
    /// is allowed, paying the resize stutters, because a stutter is a thing you
    /// play through and paging is not.
    /// </remarks>
    internal static int InitialHeapMbFor(int maximumRamMb) =>
        maximumRamMb <= SmallHeapCeilingMb
            ? Math.Min(1024, maximumRamMb)
            : maximumRamMb;

    private readonly GameLogConfigurationService _gameLogConfiguration;
    private readonly PackJvmArgumentsService _packJvmArguments;
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
    private readonly PortableIdentityRegistryService _identityRegistry;
    private readonly MinecraftWindowPlacementService _gameWindowPlacement;
    private readonly MeasuredMemoryStore _measuredMemory;
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

    /// <summary>
    /// The memory setting cannot work for the pack about to start, carrying
    /// what it is and the smallest number that can. Raised before the game
    /// starts, because once it has started the only thing left to say is that
    /// it died.
    /// </summary>
    public event Action<int, int>? ClientMemoryIsTooSmall;

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
        PortableIdentityRegistryService identityRegistry,
        ManagedComponentService? managedComponents = null)
    {
        _paths = paths;
        _logger = logger;
        _presence = new ClientPresenceService(paths, logger);
        _gameLogConfiguration = new GameLogConfigurationService(logger);
        _packJvmArguments = new PackJvmArgumentsService(logger);
        _identityService = identityService;
        _identityAdapter = identityAdapter;
        _playerProfiles = playerProfiles;
        _packInstances = packInstances;
        _packRuntimes = packRuntimes;
        _managedComponents = managedComponents ?? new ManagedComponentService(paths, logger);
        _waypointSync = waypointSync;
        _skinService = skinService;
        _identityRegistry = identityRegistry;
        _gameWindowPlacement = new MinecraftWindowPlacementService(paths, logger);
        _measuredMemory = new MeasuredMemoryStore(paths);
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
                .EnsureSteamTransportModAsync(instance, descriptor, token)
                .ConfigureAwait(false);
        }
        var identityJvmArguments = await _identityAdapter
            .PrepareJvmArgumentsAsync(runtime, instance.GameDirectory, token)
            .ConfigureAwait(false);
        var skinRegistryPath = _skinService.PrepareRegistry(settings, identityContext);
        // Who each name is. The adapter reads it when a profile is built, which
        // is the one moment every Minecraft version has in common.
        var identityRegistryPath = _identityRegistry.Prepare(identityContext);
        var gameDir = instance.GameDirectory;
        EnsureWorldsDirectoryAndSavesLink(gameDir, settings.ClientRelativePath);
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
        // Beside Temp rather than inside it: startup cleanup empties Temp, and
        // what a mod keeps in a home directory is meant to survive a restart.
        // One home for every pack, because what lands here is keyed by the
        // machine rather than by the build - e4steam's native libraries are the
        // same three files whichever pack asked for them.
        var javaHome = Path.Combine(_paths.Personal, "Home");
        _paths.EnsureUnderRoot(javaHome);
        Directory.CreateDirectory(javaHome);
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
        // And the card, because what does not fit in it the driver keeps in
        // system memory, and that copy is the game's too.
        var video = VideoMemoryProfile.Measure();
        // And what this pack was last seen holding on this machine, which beats
        // both of the above where it exists: the model was fitted to one pack
        // on one computer, and this is the pack in front of us on the computer
        // in front of us.
        var installedGb = MemorySizingService.GetInstalledMemoryGb();
        var measured = _measuredMemory.Recall(settings.ClientRelativePath, video, installedGb);
        // The number the player set, untouched: it is the heap, and it is what
        // -Xmx is about to be. Everything below is said about it rather than
        // taken out of it.
        var heapGb = settings.MaxHeapGb;
        var maximumRamMb = checked(heapGb * 1024);
        var reserveGb = MemorySizingService.GetNativeReserveGb(packMemory, video, measured);
        _logger.Info(
            $"Memory: a {heapGb} GB Java heap ({packMemory.ModCount} mods, " +
            $"Minecraft {descriptor.MinecraftVersion}), which is -Xmx exactly and what the game will report. " +
            $"This pack holds about {reserveGb} GB more beside it, so the machine is asked for about " +
            $"{heapGb + reserveGb} GB altogether, and the largest heap it offers this pack is " +
            $"{MemorySizingService.GetAllowedHeapGb()} GB. " +
            (measured.IsKnown
                ? $"The room beside it is held between {measured.AtLeastMb} and {measured.AtMostMb} MB over " +
                  $"{measured.Sessions} session(s) on this card and this much memory."
                : "The room beside it is estimated from the pack; no session here has been measured yet."));
        // Said every launch, not only when it costs something: a card that has
        // stopped being readable looks exactly like a card with room to spare
        // from the outside, and this line is the only place the difference
        // shows.
        var videoSpillGb = MemorySizingService.GetVideoSpillGb(packMemory, video);
        _logger.Info(video switch
        {
            // What the card cannot hold is charged once or not at all. Where
            // the pair has been measured it is already inside that measurement
            // - the driver's copy is part of what the process asked for - so
            // the line says the card is short without saying it costs heap
            // twice.
            { IsKnown: true } =>
                $"Video memory: {video.DedicatedGb} GB on the card, of which this pack outgrows {videoSpillGb} GB - " +
                (measured.IsKnown
                    ? "that much of what it draws is kept in system memory instead, and the measured room " +
                      "beside the heap already contains it."
                    : "that much of what it draws is kept in system memory instead, and out of the largest " +
                      "heap this machine offers."),
            // Read, and it answered that it has none: the processor's own
            // graphics, whose textures come out of system memory - the same pool
            // the heap is measured against. Not charged, because one machine is
            // not a calibration, but said plainly so the next measurement that
            // comes in over its budget can be attributed rather than guessed at.
            { HasMemorylessAdapter: true } =>
                $"Video memory: {video.MemorylessAdapter} reports none of its own, so what it draws comes " +
                "out of system memory. That is not charged separately, and a pack may go over its budget by it.",
            _ => "Video memory: the card could not be read, so nothing is kept out of the heap for it."
        });
        var wantedHeapGb = MemorySizingService.GetRecommendedHeapGb(packMemory);
        // There are two ways a budget can be too small and only one of them was
        // caught. Below the threshold the arithmetic itself fails: the room
        // beside the heap is more than the whole number, the heap is held at its
        // floor, and the game takes more than the number promised. AT the
        // threshold the arithmetic works perfectly and the heap IS the floor -
        // two gigabytes - which for a pack of ninety-four mods is not a heap,
        // it is a crash with the sums adding up. All The Fabric 3 was given
        // exactly the threshold, four gigabytes against a suggestion of five,
        // and ran out of them while generating a world; the note below already
        // said in as many words that this is what the threshold buys.
        //
        // So the test is the heap rather than the budget: does this number
        // leave the pack the heap the pack asked for.
        if (packMemory.IsKnown && heapGb < wantedHeapGb)
        {
            _logger.Warn(
                $"This pack wants a {wantedHeapGb} GB heap and holds about {reserveGb} GB outside it, " +
                $"so the {heapGb} GB set here is short of what it asks for.");
            // And said to the player, not only to the log. A number somebody
            // typed is theirs and is never moved for them - but it was typed
            // for whatever pack was selected that day, and it follows them onto
            // every pack after it. This is the one moment the launcher knows,
            // before the game starts, that the number cannot work for the pack
            // about to use it: it knew it twice for a three hundred mod pack on
            // four gigabytes, wrote this very line into its own log both times,
            // and let the game start and die of it anyway.
            //
            // What is offered is the recommendation, not the threshold above.
            // They are different numbers and only one of them is advice: the
            // threshold is where the heap stops being crushed below its floor,
            // and a player who sets exactly that gets the floor - two
            // gigabytes, which is the heap this pack has already died on twice.
            // Never advise less than the threshold either, since a small
            // machine can have its recommendation clamped below it.
            ClientMemoryIsTooSmall?.Invoke(
                heapGb,
                MemorySizingService.GetRecommendedMemoryGb(packMemory, video, measured));
        }
        // Held as text, not as MArgument, so the line logged below is the line
        // handed to the JVM. MArgument does not override ToString, so logging
        // the arguments themselves printed the type name sixty times over.
        var extraJvmArguments = new List<string>
        {
            "-Dfile.encoding=UTF-8",
            "-Djava.net.preferIPv4Stack=true",
            "-Djava.net.preferIPv6Addresses=false",
            // Five seconds to reach a host, and then give up. Java leaves this
            // unset, so a connection nobody answers waits on whatever the
            // operating system allows - about twenty-one seconds on Windows -
            // and a mod that opens a URL from the server tick freezes the world
            // for everyone in it for that long. Server Side Horror does exactly
            // that: it fetches a fake player's skin from api.mojang.com inside
            // ServerPlayer.tick, and on a machine whose route there is blocked
            // it stopped a shared world four times over, forty seconds at a
            // stretch, until ModernFix's watchdog started dumping threads. The
            // skin it was waiting for is not even used - the mod falls back to
            // the default one.
            //
            // A handshake that has not happened in five seconds was not going
            // to happen. The read timeout is deliberately left alone: reading
            // is where a real download lives, and a slow one is still a
            // download.
            "-Dsun.net.client.defaultConnectTimeout=5000",
            $"-Djava.io.tmpdir={javaTempDir}",
            // A home of its own, inside the folder, because things reach for
            // one. e4steam unpacks the three Steam native libraries into
            // "$user.home/.e4steam-steam-natives" and reads no property that
            // would move them, so on a machine where the launcher was meant to
            // leave nothing behind it left three DLLs in the user's profile.
            // Anything else that writes to a home directory lands here too,
            // which is the point: the folder is the whole installation.
            $"-Duser.home={javaHome}",
            $"-Dminecraft.portable.skin.registry={skinRegistryPath}",
            $"-Dminecraft.portable.identity.registry={identityRegistryPath}",
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
            "-XX:+ExitOnOutOfMemoryError"
            // G1 tuned for a big modded heap. Left alone it grew the committed
            // heap from 2 GB to 9.4 GB in one session in resize steps, each a
            // pause a player feels as a stutter; Xms=Xmx below ends that. The
            // 16 GB heap also defaults to 8 MB regions, which makes every
            // block-palette or LOD array over 4 MB a "humongous" allocation
            // with its own slow path - 32 MB regions put them back on the
            // normal one. The pause goal keeps mixed collections under a
            // frame and a half instead of the default 200 ms.
            //
            // Every flag here is a product option. G1NewSizePercent was here
            // for one release and is not any more: it is experimental, the JVM
            // refuses to start without -XX:+UnlockExperimentalVMOptions ahead
            // of it, and a player got "Could not create the Java Virtual
            // Machine" instead of a game. Unlocking experimental options for
            // one refinement is not worth it - the pause goal sizes the young
            // generation on its own. Anything added here must survive
            // `java <flag> -version` on the pinned runtime first.
        };
        // What the pack itself asks to be started with. ModernFix's lazy model
        // loading used to be added here for every pack, because it is worth a
        // great deal of heap and can only be set as a property - and that was a
        // decision about somebody else's mods, made for versions the launcher
        // had not met. It belongs to the pack, and now lives in its
        // launcher/jvm-args.txt.
        extraJvmArguments.AddRange(_packJvmArguments.Load(packDir));
        extraJvmArguments.AddRange(HeapTuningArgumentsFor(maximumRamMb));
        var gameLogArgument = _gameLogConfiguration.PrepareArgument(gameDir, packDir);
        if (gameLogArgument is not null) extraJvmArguments.Add(gameLogArgument);
        extraJvmArguments.AddRange(JavaCompatibilityArguments);
        extraJvmArguments.AddRange(identityJvmArguments);
        // What the game is actually started with. A release once went out with
        // an option the JVM refuses, and the reports that came back could not
        // say which options had been applied at all - none of the five logs a
        // report carries holds the command line. There are no secrets in this
        // list, and it is the first thing worth reading when a game will not
        // start.
        _logger.Info(
            $"Java: -Xms{InitialHeapMbFor(maximumRamMb)}M -Xmx{maximumRamMb}M " +
            string.Join(' ', extraJvmArguments));
        var launchOption = new MLaunchOption
        {
            Path = launchPath,
            JavaPath = runtime.JavaPath,
            Session = session,
            MaximumRamMb = maximumRamMb,
            // Equal to the maximum on a machine with room, lower on one
            // without; see InitialHeapMbFor.
            MinimumRamMb = InitialHeapMbFor(maximumRamMb),
            GameLauncherName = "LANMinecraft",
            GameLauncherVersion = "1",
            VersionType = $"{descriptor.Loader.Type} {descriptor.Loader.Version}".Trim(),
            FullScreen = false,
            ExtraJvmArguments = extraJvmArguments.Select(argument => new MArgument(argument)).ToList()
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
            minecraftProcess, settings.ClientRelativePath, exitCode, startupOutput, gameDir,
            heapGb, video, installedGb);

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
        int heapGb = 0,
        VideoMemoryProfile video = default,
        int installedGb = 0)
    {
        var processId = process.Id;
        // Timed here rather than from the process's own start time: a session
        // that lasted four minutes says nothing about what the pack holds, and
        // Windows will not answer for the start time of a process that has
        // already exited.
        var played = Stopwatch.StartNew();
        try
        {
            using var owned = process;
            using var placementCancellation = new CancellationTokenSource();
            var placementTask = _gameWindowPlacement.TrackAsync(processId, placementCancellation.Token);
            using var watchingMemory = new CancellationTokenSource();
            var heldMemory = WatchMemoryAsync(process, watchingMemory.Token);
            using var adoptingWorlds = new CancellationTokenSource();
            var adopted = AdoptClosedWorldsAsync(gameDir, adoptingWorlds.Token);
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            finally
            {
                adoptingWorlds.Cancel();
                await adopted.ConfigureAwait(false);
                watchingMemory.Cancel();
                var held = await heldMemory.ConfigureAwait(false);
                ReportMemoryHeld(held.Resident, held.Committed, heapGb);
                RememberMemoryHeld(
                    packRelativePath, video, installedGb, heapGb, played.Elapsed,
                    held.Resident, held.Committed);
                // A game that dies after the two second startup window used to
                // leave no trace at all: the window had closed, so nobody
                // reported the exit code and nobody kept what the process said
                // on its way out. Everything known about it goes to the log the
                // moment it happens.
                ReportUnexpectedExit(process, startupOutput, gameDir, heapGb);
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
        Process process, StartupOutputBuffer startupOutput, string gameDir, int heapGb)
    {
        try
        {
            if (process.ExitCode == 0) return;
            var console = startupOutput.Describe();
            var tail = ReadLatestLogTail(gameDir);
            _logger.Warn(
                $"Minecraft exited with code {process.ExitCode}." +
                console +
                tail);
            // Out of memory is the one ending a player can act on, and the one
            // the game itself would have answered with a dangerous offer, so it
            // is said in the window rather than left in the log.
            if (NamesOutOfMemory(console, tail))
            {
                _logger.Warn($"The game ran out of the {heapGb} GB heap it was given.");
                ClientRanOutOfMemory?.Invoke(heapGb);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            // A process whose exit code cannot be read tells us nothing worth
            // failing the cleanup over.
        }
    }

    /// <summary>How often the running game is asked how much it is holding.</summary>
    private static readonly TimeSpan MemorySampleInterval = TimeSpan.FromSeconds(30);
    /// <summary>
    /// How often the launcher looks for a world the player has finished with.
    /// Leaving a world is rare and costs a directory listing and one file open,
    /// so this is set by how soon the world should be in its place rather than
    /// by what the sweep costs.
    /// </summary>
    private static readonly TimeSpan ClosedWorldSweepInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The largest the game's process was ever seen at, in bytes: what it had
    /// resident, and what it had asked the system to commit. Zero for either if
    /// it could not be watched.
    /// </summary>
    /// <remarks>
    /// Sampled while it runs rather than read at the end, because Windows will
    /// not answer for a process that has exited and the peak counters go with
    /// it. Half a minute apart: this is not a profiler, it is one number per
    /// session, and the thing it measures - a modded client's footprint - moves
    /// over minutes.
    /// </remarks>
    /// <summary>
    /// Moves a world the player just made beside the others the moment they
    /// leave it, rather than when they close the game.
    /// </summary>
    /// <remarks>
    /// The game makes a new world as a real folder inside the instance, because
    /// that is where it is told to make it, and until it is moved the launcher
    /// cannot list it, hand it over or stamp it with the build that made it. It
    /// used to be moved at the end of the session, so a player who made a world
    /// and kept playing had it sitting in the wrong place for hours.
    ///
    /// It cannot be moved any earlier than this. Windows renames a folder out
    /// from under a held session.lock without complaint, but not one whose
    /// region files are open - and a world being played has both. Leaving the
    /// world closes those and drops the lock, which is exactly the moment this
    /// waits for.
    /// </remarks>
    private async Task AdoptClosedWorldsAsync(string gameDir, CancellationToken token)
    {
        try
        {
            var saves = new SavesFolderService(_logger);
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(ClosedWorldSweepInterval, token).ConfigureAwait(false);
                // Adopt already asks for a level.dat and skips a world the game
                // still holds, so the sweep is the same one that runs before a
                // launch and after it - only now it also runs between worlds.
                saves.Adopt(_paths.Worlds, gameDir);
            }
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            // The game ended, or the folder was busy this time round. The sweep
            // before the next launch catches whatever this one did not.
        }
    }

    private static async Task<(long Resident, long Committed)> WatchMemoryAsync(
        Process process, CancellationToken token)
    {
        long resident = 0;
        long committed = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                process.Refresh();
                resident = Math.Max(resident, process.WorkingSet64);
                committed = Math.Max(committed, process.PrivateMemorySize64);
                await Task.Delay(MemorySampleInterval, token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or InvalidOperationException or SystemException)
        {
            // The game ended between the refresh and the read, or Windows would
            // not answer for it. Whatever was seen before that is the answer.
        }
        return (resident, committed);
    }

    /// <summary>
    /// Writes down what the game actually held beside the heap it was given.
    /// </summary>
    /// <remarks>
    /// The whole memory model rests on a single measurement - Limitless 8, 874
    /// jars, about eight gigabytes outside a twelve gigabyte heap - and every
    /// other pack is an extrapolation from it. That was defensible while there
    /// was nothing else to go on, and it is what decides whether a three
    /// hundred mod pack is offered to an eight gigabyte machine at all. So
    /// every session that ends leaves the same measurement behind for the pack
    /// it ran, and the guess gets to be checked.
    ///
    /// The subtraction reads differently at the two ends, and is worth having
    /// at both. A large heap is started at its maximum, so it is committed from
    /// the first instant and everything above it is room beside it. A small one
    /// starts at a gigabyte and grows, so what is committed is the heap the
    /// session actually needed plus that room - which is the more useful of the
    /// two readings, and the one this measurement was added to get.
    ///
    /// Two numbers, and the commit is the one that answers the question. The
    /// working set is only what was resident when it was looked at, and a
    /// machine short of memory pages the rest out - so on exactly the small
    /// machines this measurement exists for, the working set understates what
    /// the pack wanted, and understates it worst where it matters most. The
    /// commit does not move under that pressure. The resident figure is kept
    /// beside it because the gap between them is itself the reading: far apart
    /// means the machine was paging.
    /// </remarks>
    private void ReportMemoryHeld(long residentBytes, long committedBytes, int heapGb)
    {
        var line = DescribeMemoryHeld(residentBytes, committedBytes, heapGb);
        if (line.Length > 0) _logger.Info(line);
    }

    /// <summary>
    /// Keeps that measurement, so the next launch of this pack on this machine
    /// can size itself by what happened rather than by what was modelled.
    /// </summary>
    /// <remarks>
    /// The line above has been written every session for a while and read by
    /// nobody: it is the one number that could tell the sizing rules they are
    /// wrong, and it went into a log file. It was wrong, too - Limitless 8 on a
    /// 24 GB budget was estimated at 12 GB beside its heap and left with a 12 GB
    /// heap that spark reported 11.5 GB of in use, while the log for the same
    /// pack said 7533 MB.
    ///
    /// Not every session is worth keeping, and <see cref="MemorySession"/> says
    /// which; a session that is thrown away says why in the log, because a pair
    /// that never gathers a measurement is otherwise indistinguishable from one
    /// nobody has played.
    /// </remarks>
    private void RememberMemoryHeld(
        string packRelativePath,
        VideoMemoryProfile video,
        int installedGb,
        int heapGb,
        TimeSpan played,
        long residentBytes,
        long committedBytes)
    {
        // A game this launcher did not start has no -Xmx of ours to subtract, so
        // there is nothing to measure the room beside the heap against. Silence
        // is honest here; "could not be measured" would not be, since the
        // process was measured perfectly well.
        if (heapGb <= 0) return;

        var session = new MemorySession(
            CommittedMb: (int)(committedBytes / (1024 * 1024)),
            ResidentMb: (int)(residentBytes / (1024 * 1024)),
            HeapMb: heapGb * 1024,
            Minutes: (int)played.TotalMinutes,
            When: DateTimeOffset.Now);
        if (!session.IsWorthKeeping)
        {
            if (session.CommittedMb > 0) _logger.Info($"Memory: not written down - {session.WhyNotKept}.");
            return;
        }

        var measured = _measuredMemory.Remember(packRelativePath, video, installedGb, session);
        if (!measured.IsKnown) return;
        _logger.Info(
            $"Memory: written down - between {session.BesideHeapAtLeastMb} and {session.BesideHeapAtMostMb} MB beside the heap over {session.Minutes} min. " +
            $"This pack on this machine now stands between {measured.AtLeastMb} and {measured.AtMostMb} MB " +
            $"over {measured.Sessions} session(s), which is what the next launch will keep out of the budget.");
    }

    /// <summary>That measurement as a line of log, or nothing to say.</summary>
    internal static string DescribeMemoryHeld(long residentBytes, long committedBytes, int heapGb)
    {
        var residentMb = residentBytes / (1024 * 1024);
        var committedMb = committedBytes / (1024 * 1024);
        var askedMb = committedMb > 0 ? committedMb : residentMb;
        if (askedMb <= 0) return "";

        var resident = residentMb > 0 && committedMb > 0 ? $", {residentMb} MB of it resident" : "";
        if (heapGb <= 0) return $"Memory: the game asked for {askedMb} MB at its largest{resident}.";

        var heapMb = heapGb * 1024;
        return $"Memory: the game asked for {askedMb} MB at its largest - a heap of {heapMb} MB " +
               $"and about {Math.Max(0, askedMb - heapMb)} MB beside it{resident}.";
    }

    /// <summary>
    /// Whether what the game left behind names an out-of-memory ending.
    /// </summary>
    /// <remarks>
    /// Both of the things the game left are read, and the console is the one
    /// that carries it. <c>-XX:+ExitOnOutOfMemoryError</c> is the whole point
    /// of this path and also the reason it was looking in the wrong place: the
    /// JVM dies on the spot, so its single parting line - "Terminating due to
    /// java.lang.OutOfMemoryError: Java heap space" - is written straight to
    /// stdout and log4j never sees it. latest.log ends mid-sentence instead,
    /// and Windows blames whatever native library a thread happened to be
    /// inside, which for a client is usually OpenAL.
    ///
    /// Read only from the tail, this said nothing for a pack that ran out of
    /// memory twice in a row - while the launcher wrote that very line into its
    /// own log, one statement earlier, in the same message.
    /// </remarks>
    internal static bool NamesOutOfMemory(string consoleOutput, string logTail) =>
        consoleOutput.Contains(OutOfMemoryMarker, StringComparison.Ordinal) ||
        logTail.Contains(OutOfMemoryMarker, StringComparison.Ordinal);

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
                _paths.CombineUnderInstances(session.PackRelativePath), heapGb: 0);
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

    /// <summary>
    /// Whether this folder is a pack the launcher will offer.
    /// </summary>
    /// <remarks>
    /// A folder with a manifest is one because it says so. A folder with mods
    /// in it is one because that is what somebody making a build of their own
    /// does: they make a folder, they put jars in it, and they expect to see it
    /// in the list. What the manifest would have said - which loader, which
    /// Minecraft, which build of that loader - is read out of those jars and
    /// written down before the pack is started, so the folder ends up with a
    /// manifest either way; it simply does not have to arrive with one.
    /// </remarks>
    public static bool HasPackData(string packDirectory) =>
        PackManifestService.HasManifest(packDirectory) ||
        (Directory.Exists(packDirectory) && Directory.Exists(Path.Combine(packDirectory, "mods")));

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

    /// <summary>
    /// Gives the game the worlds this build may open, and only those.
    ///
    /// The saves folder used to be one link to the shared Worlds folder, which
    /// meant every build listed every world - and opening a world under the
    /// wrong build is how the blocks of every mod that build does not have are
    /// lost. It holds a junction per world now; see <see cref="SavesFolderService"/>.
    /// </summary>
    private void EnsureWorldsDirectoryAndSavesLink(string clientDir, string packRelativePath)
    {
        try
        {
            new SavesFolderService(_logger).Prepare(_paths.Worlds, clientDir, packRelativePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.Warn($"The worlds of this build could not be laid out for the game: {ex.Message}");
        }
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

}
