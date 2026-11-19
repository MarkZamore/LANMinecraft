using System.Net.Http;

namespace Minecraft;

/// <summary>Owns the services and work for one launcher window.</summary>
internal sealed class LauncherSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenSource _catalogLifetime = new();
    private readonly CancellationTokenSource _playLifetime = new();
    private readonly object _gate = new();
    private readonly Dictionary<Task, string> _operations = [];
    private bool _closing;
    private Task? _shutdown;
    private CoreServices? _core;
    private LauncherMaintenance? _maintenance;
    private PackHashService? _packHash;
    private SteamClientService? _steam;
    private SteamIdentityService? _identity;
    private PortableIdentityAdapterService? _identityAdapter;
    private SteamPeerTransport? _transport;
    private PeerConnectionRouter? _router;
    private SteamPeerDirectory? _directory;
    private PackInstanceService? _instances;
    private PackRuntimeService? _runtimes;
    private WaypointSyncService? _waypoints;
    private SkinService? _skin;
    private PortableIdentityRegistryService? _registry;
    private MinecraftProcessService? _client;
    private PackAutoManifestService? _autoManifest;
    private HttpClient? _autoManifestHttp;
    private WorldPlayerProfileService? _profiles;
    private WorldTransferService? _worldTransfer;
    private PackTransferService? _packTransfer;
    private PeerGreetingService? _greetings;

    internal LauncherSession()
    {
        LifetimeToken = _lifetime.Token;
        CatalogToken = _catalogLifetime.Token;
        PlayToken = _playLifetime.Token;
    }

    internal CancellationToken LifetimeToken { get; }
    internal CancellationToken CatalogToken { get; }
    internal CancellationToken PlayToken { get; }

    internal sealed record CoreServices(AppPaths Paths, Logger Logger, SettingsService Settings,
        PackHashService PackHash, PortablePackSyncService PackSync, LauncherMaintenance Maintenance,
        WorldMetadataService WorldMetadata, ControlsPresetService ControlsPreset);

    internal CoreServices InitializeCore(AppPaths paths, Logger logger, Func<bool> gameIsRunning,
        Func<string, bool> isPackInUse, Func<bool> updating, Action<int> reportProgress, Action<bool> reportActivity)
    {
        var settings = new SettingsService(paths, logger);
        _packHash = new PackHashService(paths);
        var sync = new PortablePackSyncService(paths, logger);
        _maintenance = new LauncherMaintenance(paths, logger,
            run => LauncherMaintenancePass.Run(paths, logger, sync, run, updating),
            gameIsRunning, isPackInUse, reportProgress, reportActivity);
        return _core = new CoreServices(paths, logger, settings, _packHash, sync, _maintenance,
            new WorldMetadataService(), new ControlsPresetService(logger));
    }

    internal async Task LoadRuntimeMetadataAsync()
    {
        var core = Core;
        var components = await Task.Run(() =>
        {
            LauncherStorageCleanup.ReconcileLayout(core.Paths, core.Logger, checkpoint: LifetimeToken.ThrowIfCancellationRequested);
            return new RuntimeComponentService(core.Paths, core.Logger).LoadAsync(refresh: false, LifetimeToken);
        }, LifetimeToken);
        LifetimeToken.ThrowIfCancellationRequested();
        RuntimeComponentContext.SetDefault(components);
    }

    internal (SteamClientService Client, SteamIdentityService Identity) InitializeSteam()
    {
        var core = Core;
        _steam = new SteamClientService(new SteamworksApiFacade(),
            new SteamNativeLibraryService(core.Paths, core.Logger), core.Logger);
        _identity = new SteamIdentityService(new SteamClientUserSource(_steam), core.Logger);
        _identityAdapter = new PortableIdentityAdapterService(core.Paths, core.Logger);
        return (_steam, _identity);
    }

    internal SteamPeerDirectory InitializePeers()
    {
        var steam = _steam ?? throw new InvalidOperationException("The Steam client was not initialized.");
        _transport = new SteamPeerTransport(steam, Core.Logger);
        _router = new PeerConnectionRouter(_transport, Core.Logger);
        return _directory = new SteamPeerDirectory(steam, _transport, Core.Logger);
    }

    internal sealed record GameServices(MinecraftProcessService Client, PlayLaunchCoordinator Launch,
        WorldPlayerProfileService Profiles, WaypointSyncService Waypoints, SkinService Skin,
        PortableIdentityRegistryService Registry);

    internal async Task<GameServices> InitializeGameAsync()
    {
        var core = Core;
        _profiles = new WorldPlayerProfileService(core.Paths, core.Logger);
        _instances = new PackInstanceService(core.Paths, core.Logger);
        _runtimes = new PackRuntimeService(core.Paths, core.Logger, packSync: core.PackSync);
        _waypoints = new WaypointSyncService(core.Paths, core.Logger, core.WorldMetadata, Transport);
        _skin = new SkinService(core.Paths, core.Logger, Transport);
        _registry = new PortableIdentityRegistryService(core.Paths, core.Logger);
        await _skin.StartAsync(LifetimeToken);
        LifetimeToken.ThrowIfCancellationRequested();

        _client = new MinecraftProcessService(core.Paths, core.Logger, _identity!, _identityAdapter!, _profiles,
            _instances, _runtimes, _waypoints, _skin, _registry, packSync: core.PackSync);
        _autoManifestHttp = new HttpClient();
        _autoManifest = new PackAutoManifestService(core.Paths, core.Logger, _autoManifestHttp);
        var launch = new PlayLaunchCoordinator(core.Paths, core.Settings,
            new PackPreparationDownloadService(core.Paths, core.PackSync), _autoManifest, core.PackHash,
            core.ControlsPreset, core.Logger, _client.StartClientAsync);
        return new GameServices(_client, launch, _profiles, _waypoints, _skin, _registry);
    }

    internal WorldTransferService InitializeWorldTransfer(IWorldTransferConfirmation confirmation,
        Func<string, string> compatibilityPathFor, Func<string, string> installationPathFor, Func<bool> mayStart)
    {
        var core = Core;
        _worldTransfer = new WorldTransferService(core.Paths, core.Logger, _client!, core.Settings,
            core.WorldMetadata, _identity!, _profiles!, _waypoints!, _skin!, Transport,
            runtimeOptions: null, confirmation: confirmation, recoverAtStartup: false)
        {
            CompatibilityPathFor = compatibilityPathFor,
            InstallationPathFor = installationPathFor,
            MayStartTransfer = mayStart
        };
        Router.RegisterFallback(_worldTransfer);
        return _worldTransfer;
    }

    internal PackTransferService InitializePackTransfer(Func<string, bool> isPersonal, Func<string, bool> isCatalog,
        Func<PackTransferOffer, CancellationToken, Task<bool>> confirmation, Func<bool> mayStart)
    {
        var core = Core;
        var archive = new PackArchiveService(core.Paths, isPersonal, isCatalog,
            prepareDescriptor: _autoManifest!.EnsureAsync);
        _packTransfer = new PackTransferService(core.Paths, Transport, archive, confirmation, mayStart, core.Logger);
        Router.Register(_packTransfer);
        return _packTransfer;
    }

    internal (BugReportService Reports, UpdateService Updates) InitializeDiagnostics(
        Func<string?> instanceDirectory, Func<BugReportContext> context,
        Func<CancellationToken, Task<SupportEnvironmentSnapshot>> captureEnvironment)
    {
        var core = Core;
        var reports = new BugReportService(core.Paths, core.Logger, Transport, instanceDirectory, context, captureEnvironment);
        reports.PruneStoredReports();
        Router.Register(reports);
        Router.Register(_waypoints!);
        Router.Register(_skin!);
        var directory = _directory!;
        _greetings = new PeerGreetingService(Transport, core.Logger);
        _greetings.Greeted += presence => directory.Introduce(presence);
        Router.Register(_greetings);
        return (reports, new UpdateService(core.Paths, core.Logger));
    }

    internal Task StartPeerListenerAsync() => Router.StartAsync(LifetimeToken);

    internal void GreetPeers(SteamPeerPresence presence)
    {
        if (_greetings is not { } greetings || _directory is not { } directory) return;
        greetings.SetLocalPresence(presence);
        _ = RunAsync("peer greeting", () => greetings.GreetNewAsync(directory.Peers, LifetimeToken));
    }

    internal Task RunAsync(string name, Func<Task> work)
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_closing) return Task.CompletedTask;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _operations.Add(completion.Task, name);
        }
        return RunAndSettleAsync(work, completion);
    }

    private async Task RunAndSettleAsync(Func<Task> work, TaskCompletionSource completion)
    {
        try { await work(); }
        finally
        {
            lock (_gate)
            {
                _operations.Remove(completion.Task);
                completion.SetResult();
            }
        }
    }

    internal void BeginClosing()
    {
        lock (_gate)
        {
            if (_closing) return;
            _closing = true;
        }
        TryStep("cancelling game launch", _playLifetime.Cancel);
        TryStep("cancelling catalog work", _catalogLifetime.Cancel);
        TryStep("stopping maintenance", () => _maintenance?.Stop());
    }

    internal Task CloseAsync(Action? publishDeparture = null)
    {
        BeginClosing();
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_shutdown is not null) return _shutdown;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdown = completion.Task;
        }
        _ = CloseCoreAsync(publishDeparture, completion);
        return completion.Task;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private async Task CloseCoreAsync(Action? publishDeparture, TaskCompletionSource completion)
    {
        try
        {
            if (publishDeparture is not null) TryStep("publishing Steam departure", publishDeparture);
            TryStep("cancelling launcher work", _lifetime.Cancel);
            Task[] pending;
            lock (_gate) pending = _operations.Keys.ToArray();
            await Task.WhenAll(pending);

            // Stop accepting and drain routed requests before their handlers are disposed.
            await TryDisposeAsync("peer router", _router);
            await TryDisposeAsync("pack transfer", _packTransfer);
            await TryDisposeAsync("world transfer", _worldTransfer);
            await TryDisposeAsync("waypoint sync", _waypoints);
            await TryDisposeAsync("skin service", _skin);
            await TryDisposeAsync("peer transport", _transport);
            await TryDisposeAsync("Steam client", _steam);
            TryStep("pack instances", () => _instances?.Dispose());
            TryStep("pack runtimes", () => _runtimes?.Dispose());
            TryStep("identity adapter", () => _identityAdapter?.Dispose());
            TryStep("pack hashes", () => _packHash?.Dispose());
            TryStep("manifest client", () => _autoManifestHttp?.Dispose());
        }
        finally
        {
            _playLifetime.Dispose();
            _catalogLifetime.Dispose();
            _lifetime.Dispose();
            completion.SetResult();
        }
    }

    internal void TryStep(string name, Action action)
    {
        try { action(); }
        catch (Exception ex) { LogShutdownFailure(name, ex); }
    }

    private async Task TryDisposeAsync(string name, IAsyncDisposable? service)
    {
        if (service is null) return;
        try { await service.DisposeAsync(); }
        catch (Exception ex) { LogShutdownFailure(name, ex); }
    }

    private void LogShutdownFailure(string name, Exception error)
    {
        // A log that cannot be written must not skip the remaining shutdown steps.
        try { _core?.Logger.Warn($"Launcher shutdown failed while {name}: {error.Message}"); }
        catch { }
    }

    private CoreServices Core => _core ?? throw new InvalidOperationException("Launcher core services were not initialized.");
    private SteamPeerTransport Transport => _transport ?? throw new InvalidOperationException("Steam transport unavailable.");
    private PeerConnectionRouter Router => _router ?? throw new InvalidOperationException("Steam connection router unavailable.");
}
