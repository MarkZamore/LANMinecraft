using System.Collections.ObjectModel;
using System.IO;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Minecraft;

[SuppressMessage("Design", "CA1001", Justification = "WPF owns the window lifetime; disposable services are released by the coordinated Closing handler.")]
public partial class MainWindow : Window
{
    private const int MinHeapGb = MemorySizingService.MinHeapGb;
    private static readonly TimeSpan PeerTtl = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan DiagnosticTargetTtl = TimeSpan.FromMinutes(3);
    // EB59 is a half-size badge glyph; this maps its ink bounds onto EA18's full shield bounds.

    private readonly ObservableCollection<PeerViewModel> _peers = new();
    private readonly ObservableCollection<WorldViewModel> _worlds = new();
    private readonly ObservableCollection<ClientBuildViewModel> _builds = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly TransferRateTracker _transferRate = new();
    private readonly TransferRateTracker _updateRate = new();
    private readonly TransferRateTracker _runtimeRate = new();

    private AppPaths? _paths;
    private AppSettings? _settings;
    private SettingsService? _settingsService;
    private Logger? _logger;
    private PackHashService? _packHash;
    private PortablePackSyncService? _packSync;
    private PackAutoManifestService? _autoManifest;
    private WorldMetadataService? _worldMetadata;
    private WorldPlayerProfileService? _worldPlayerProfiles;
    private SteamClientService? _steamClient;
    private SteamIdentityService? _identityService;
    private PortableIdentityAdapterService? _identityAdapter;
    private PackInstanceService? _packInstances;
    private PackRuntimeService? _packRuntimes;
    private WaypointSyncService? _waypointSync;
    private SkinService? _skinService;
    private PortableIdentityRegistryService? _identityRegistry;

    [SuppressMessage(
        "Performance",
        "CA1859",
        Justification = "The concrete transport is chosen at startup; every consumer takes the seam.")]
    private IPeerTransport? _peerTransport;
    private PeerConnectionRouter? _peerRouter;
    private SteamPeerDirectory? _peerDirectory;
    private MinecraftProcessService? _minecraft;
    private WorldTransferService? _transfer;
    private UpdateService? _updateService;
    private BugReportService? _bugReports;
    private PeerGreetingService? _greetings;
    private string _localPackHash = "";
    private string _state = "Starting";
    private bool _busy;
    private bool _suppressTextPersistence;
    private bool _suppressBuildPersistence;
    private bool _suppressMemoryTextChanged;
    private DispatcherTimer? _memoryEstimateWait;
    private int _memoryEstimateWaitStep;
    private int _packMemoryGeneration;
    /// <summary>False on every launch: the column opens on the assistant.</summary>
    private bool _sidePanelShowsNews;
    private bool _bugReportSending;
    private string _bugReportStatus = string.Empty;
    private readonly TransferRateTracker _bugReportRate = new();
    private long _transferBytesCurrent;
    private long _transferBytesTotal;
    private double _lastTransferSpeedBytesPerSecond;
    private string _transferStage = "";
    private bool _transferActive;
    private TransferPacingStore? _transferPacingStore;
    private TransferPacing _transferPacing = new();
    private TransferRun? _transferRun;
    private TimeSpan? _transferRemaining;
    private bool _updateBusy;
    private bool _isEditingPlayerName;
    private NameMarquee? _playerNameMarquee;
    private ChangelogPager? _changelogPager;
    private readonly ObservableCollection<ChangelogEntryViewModel> _changelogShown = [];
    private bool _startupComplete;
    private bool _minecraftRunning;
    private bool _minecraftPreparing;
    private string? _playProgressText;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private SteamPeerPresence? _lastPublishedPresence;
    private DateTimeOffset? _sessionStartedUtc;
    private bool _restartAfterUpdateOnExit;
    private PreparedUpdate? _preparedUpdate;
    private readonly WindowPlacementService _windowPlacement;
    private ControlsPresetService? _controlsPreset;
    private ResourcePackDefaultsService? _resourcePackDefaults;
    private OptionsDefaultsService? _optionsDefaults;
    private MinimapResetService? _minimapReset;
    private ControlsPresetStatus _controlsPresetStatus;
    private string? _controlsPresetStamp;

    /// <summary>The shape of the canvas the Viewbox scales, margins included.</summary>
    private double ClientAspect()
    {
        var width = RootGrid.Width + RootGrid.Margin.Left + RootGrid.Margin.Right;
        var height = RootGrid.Height + RootGrid.Margin.Top + RootGrid.Margin.Bottom;
        return height > 0 ? width / height : 1;
    }

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _windowPlacement = new WindowPlacementService(new AppPaths(AppPaths.ResolveApplicationRoot()));
        _windowPlacement.Apply(this, ClientAspect());
        BuildComboBox.ItemsSource = _builds;
        OnlinePlayerComboBox.ItemsSource = _peers;
        WorldComboBox.ItemsSource = _worlds;
        _uiTimer.Tick += (_, _) =>
        {
            RefreshBuilds();
            RefreshWorlds();
            RefreshSteamPeers();
            PruneStalePeers();
            RefreshLocalPresence();
            // The preset changes under the launcher's feet: a pack sync brings a
            // new one, the game rewrites the options on exit. Asking here means
            // the button offers itself the moment there is something to apply,
            // instead of waiting for the next launch to notice.
            RefreshControlsPresetStatus();
            RefreshUi();
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _paths = new AppPaths(AppPaths.ResolveApplicationRoot());
            _paths.Ensure();
            LogCleanupService.RunCleanup(_paths);
            _logger = new Logger(_paths.LogFile);
            // A name too long for its field walks itself so the whole of it can
            // be read; it holds still while the field is being edited. It is
            // built here rather than in the constructor because it reports what
            // it measures, and there is no logger to report to until now.
            _playerNameMarquee = new NameMarquee(PlayerNameTextBox, _logger);
            LoadChangelog();
            ShowSidePanel(news: false);
            DeprecatedFileCleanupService.Run(_paths, _logger);
            // And the structural pass: runtimes for builds that are gone, and
            // the copies of the game a build kept before the game was shared.
            // It runs after the one above because that one is what removes the
            // folders this one would otherwise have to reason about.
            StructureCleanupService.Run(_paths, _logger);
            _settingsService = new SettingsService(_paths, _logger);
            _settings = _settingsService.Load();
            _logger.LineWritten += line => PostToUi(() => AppendLog(line));
            _packHash = new PackHashService(_paths);
            _packSync = new PortablePackSyncService(_paths, _logger);
            _autoManifest = new PackAutoManifestService(_paths, _logger, new System.Net.Http.HttpClient());
            _transferPacingStore = new TransferPacingStore(_paths);
            _transferPacing = _transferPacingStore.Load();
            _worldMetadata = new WorldMetadataService();
            _controlsPreset = new ControlsPresetService(_logger);
            _resourcePackDefaults = new ResourcePackDefaultsService(_logger);
            _optionsDefaults = new OptionsDefaultsService(_logger);
            _minimapReset = new MinimapResetService(_logger);
            _steamClient = new SteamClientService(
                new SteamworksApiFacade(),
                new SteamNativeLibraryService(_paths, _logger),
                _logger);
            _steamClient.StatusChanged += (_, status) => PostToUi(() => ApplySteamStatus(status));
            _identityService = new SteamIdentityService(new SteamClientUserSource(_steamClient), _logger);
            _identityAdapter = new PortableIdentityAdapterService(_paths, _logger);
            await ConnectSteamAndBindIdentityAsync();
            _peerTransport = new SteamPeerTransport(_steamClient, _logger);
            _peerRouter = new PeerConnectionRouter(_peerTransport, _logger);
            _peerDirectory = new SteamPeerDirectory(_steamClient, _peerTransport, _logger);
            _peerDirectory.PeersChanged += (_, peers) => PostToUi(() => ApplyPeers(peers));
            _worldPlayerProfiles = new WorldPlayerProfileService(_paths, _logger);
            _packInstances = new PackInstanceService(_paths, _logger);
            _packRuntimes = new PackRuntimeService(_paths, _logger);
            _waypointSync = new WaypointSyncService(_paths, _logger, _worldMetadata, _peerTransport);
            _skinService = new SkinService(_paths, _logger, _peerTransport);
            _identityRegistry = new PortableIdentityRegistryService(_paths, _logger);
            await _skinService.StartAsync(_lifetimeCts.Token);
            _minecraft = new MinecraftProcessService(_paths, _logger, _identityService, _identityAdapter, _worldPlayerProfiles, _packInstances, _packRuntimes, _waypointSync, _skinService, _identityRegistry);
            _minecraft.ClientRunningChanged += OnMinecraftClientRunningChanged;
            _minecraft.ClientRanOutOfMemory += OnMinecraftRanOutOfMemory;
            _minecraft.ClientMemoryIsTooSmall += OnMinecraftMemoryIsTooSmall;
            _minecraft.ClientPreparingChanged += OnMinecraftClientPreparingChanged;
            // A launcher closed while the game plays leaves the game behind.
            // This one picks it up, so the button says "Игра запущена" instead
            // of offering a second client over the first.
            _minecraft.AdoptRunningClients();
            _transfer = new WorldTransferService(_paths, _logger, _minecraft, _settingsService, _worldMetadata, _identityService, _worldPlayerProfiles, _waypointSync, _skinService, _peerTransport,
                runtimeOptions: null,
                confirmation: new WpfWorldTransferConfirmation(this));
            _bugReports = new BugReportService(
                _paths,
                _logger,
                _peerTransport,
                ResolveCurrentInstanceDirectory,
                CreateBugReportContext,
                CaptureSupportEnvironmentAsync);
            _bugReports.ReportReceived += (_, arrival) => PostToUi(() =>
            {
                SetBugReportStatus(
                    $"Получен отчёт от {arrival.SenderName} ({DescribeBytes(arrival.ArchiveBytes)}). " +
                    $"Папка: {Path.GetFileName(arrival.Directory)}");
                RefreshDiagnosticsPanel();
            });
            _bugReports.PruneStoredReports();
            _peerRouter.Register(_bugReports);
            _peerRouter.Register(_waypointSync);
            _peerRouter.Register(_skinService);
            _greetings = new PeerGreetingService(_peerTransport, _logger);
            _greetings.Greeted += presence => _peerDirectory?.Introduce(presence);
            _peerRouter.Register(_greetings);
            _peerRouter.RegisterFallback(_transfer);
            _updateService = new UpdateService(_paths, _logger);
            _transfer.StatusChanged += message => PostToUi(() => SetState(message));
            _transfer.ProgressChanged += progress =>
                PostToUi(() => ApplyTransferProgress(progress));
            _transfer.BecameHost += () => PostToUi(() =>
            {
                SetState("World received");
                RefreshWorlds();
                RefreshUi();
            });
            LoadSettingsIntoUi();
            RefreshBuilds();
            RefreshControlsPresetStatus();
            RefreshPackMemory();
            RefreshMemoryText(saveIfChanged: true);
            RefreshWorlds();
            InitializeUpdateUi();
            InitializeRuntimeProgressUi();
            // Every control that a state decides - the preset button above all -
            // wears whatever the markup gave it until this runs. Without it the
            // window opens with the preset button live over a preset already in
            // place, and stays wrong until the two-second timer ticks or the
            // pack hash finishes, whichever loses.
            RefreshUi();
            _ = CheckForUpdatesAsync(_lifetimeCts.Token);
            if (BuildComboBox.SelectedItem is ClientBuildViewModel startupBuild)
            {
                _ = RefreshLauncherDataAsync(startupBuild.RelativePath);
            }
            _uiTimer.Start();
            SetState("Ready");
            _logger.Info("Minecraft portable launcher started.");
            await RefreshPackHashAsync(_lifetimeCts.Token);
            await StartNetworkingAsync();
            _startupComplete = true;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Minecraft", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        _windowPlacement.Save(this);

        // Always leave the original Closing event before issuing the final Close().
        // Several services can complete synchronously when networking was never started.
        await Dispatcher.Yield(DispatcherPriority.Background);

        _uiTimer.Stop();

        // Before anything is torn down, tell Steam this launcher is going.
        // Steam keeps the keys of a closed launcher and keeps serving them, so
        // without this the player stays on everybody's list and a report sent
        // their way dies on a connection nobody is listening to.
        if (_peerDirectory is not null && _lastPublishedPresence is not null)
        {
            try
            {
                _peerDirectory.PublishDeparture(_lastPublishedPresence);
            }
            catch (Exception ex)
            {
                _logger?.Warn($"The leaving notice could not be published ({ex.Message}).");
            }
        }

        _lifetimeCts.Cancel();
        try
        {
            if (_peerRouter is not null) await _peerRouter.DisposeAsync();
            if (_transfer is not null) await _transfer.DisposeAsync();
            if (_waypointSync is not null) await _waypointSync.DisposeAsync();
            if (_skinService is not null) await _skinService.DisposeAsync();
            if (_peerTransport is not null) await _peerTransport.DisposeAsync();
            if (_steamClient is not null) await _steamClient.DisposeAsync();
            _packInstances?.Dispose();
            _packRuntimes?.Dispose();
            _identityAdapter?.Dispose();
            _packHash?.Dispose();
            _lifetimeCts.Dispose();
        }
        finally
        {
            if (_paths is not null)
            {
                LogCleanupService.ScheduleCurrentExtractionCleanup(_paths, Environment.ProcessId);
            }
            try
            {
                if (_updateService is not null)
                {
                    var prepared = await Task.Run(_updateService.TryGetPreparedUpdate);
                    if (prepared is not null)
                    {
                        var mode = _restartAfterUpdateOnExit
                            ? UpdateInstallMode.InstallAndRestart
                            : UpdateInstallMode.InstallOnExit;
                        _updateService.StartInstall(prepared, mode);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Update could not be scheduled during shutdown: {ex.Message}");
            }
            _shutdownComplete = true;
            Close();
        }
    }

    private void LoadSettingsIntoUi()
    {
        var settings = RequireSettings();
        _suppressTextPersistence = true;
        try
        {
            PlayerNameTextBox.Text = RequireSettings().PlayerName;
        }
        finally
        {
            _suppressTextPersistence = false;
        }
    }

    /// <summary>
    /// Re-reads the friends list. Steam serves it from its own cache, so this
    /// is cheap enough for the two-second UI timer that used to poll adapters.
    /// </summary>
    /// <summary>
    /// Presence is what friends see; it has to follow the game starting and
    /// stopping, and it has to be re-set after e4steam clears rich presence
    /// when a hosted world closes.
    /// </summary>
    private void RefreshLocalPresence()
    {
        if (_shutdownStarted || !_startupComplete || !IsIdentityBound) return;
        try
        {
            PublishLocalPresence();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IdentityUnavailableException or IOException)
        {
            _logger?.Warn($"Steam presence refresh failed: {ex.Message}");
        }
    }

    private void RefreshSteamPeers()
    {
        if (_shutdownStarted || _peerDirectory is null) return;
        try
        {
            _peerDirectory.Refresh();
            // Take the whole list every tick, not only when something about it
            // changed: the window ages a peer out after PeerTtl, so a friend
            // whose presence simply stayed the same used to vanish from the
            // list about half a minute after the launcher started.
            ApplyPeers(_peerDirectory.Peers);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _logger?.Warn($"Steam friend refresh failed: {ex.Message}");
        }
    }

    private async Task RefreshPackHashAsync(CancellationToken token = default)
    {
        if (!token.CanBeCanceled) token = _lifetimeCts.Token;
        var settings = RequireSettings();
        var paths = RequirePaths();
        var relativePath = settings.ClientRelativePath;
        var hash = await RequirePackHash().CalculateAsync(paths.CombineUnderPacks(relativePath), token);
        if (!string.Equals(RequireSettings().ClientRelativePath, relativePath, StringComparison.OrdinalIgnoreCase)) return;
        _localPackHash = hash;
        PackHashText.Text = _localPackHash.Length > 12 ? _localPackHash[..12] : _localPackHash;
    }

    private void RefreshBuilds()
    {
        if (_paths is null || _settings is null || _settingsService is null) return;

        Directory.CreateDirectory(_paths.Packs);
        var selectedRelativePath = (BuildComboBox.SelectedItem as ClientBuildViewModel)?.RelativePath;
        var builds = Directory.EnumerateDirectories(_paths.Packs)
            .Where(MinecraftProcessService.HasPackData)
            .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
            .Select(path => new ClientBuildViewModel
            {
                Name = Path.GetFileName(path),
                RelativePath = Path.GetRelativePath(_paths.Packs, path),
                FullPath = path
            })
            .ToList();

        // Every pack the launcher knows how to fetch is offered even before it
        // exists on disk - pressing Play downloads it. Without this a new build
        // could only be seen by whoever already had its folder, which is no way
        // to hand one to friends.
        foreach (var known in PortablePackSyncService.KnownPacks)
        {
            if (builds.Any(build => string.Equals(
                    build.RelativePath,
                    known.RelativePath,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            builds.Add(new ClientBuildViewModel
            {
                // A star, not a word: the list is narrow and the name is what a
                // player reads. Pressing Play on a starred build downloads it.
                Name = $"{known.RelativePath}*",
                RelativePath = known.RelativePath,
                FullPath = _paths.CombineUnderPacks(known.RelativePath),
                IsInstalled = false
            });
        }

        // One order for the whole list. The installed builds were sorted and
        // the ones only offered were appended after them, so the names read
        // out of order and which build a fresh install landed on depended on
        // the order they happen to be written down in.
        builds = ListOrder.Builds(builds).ToList();

        var buildPathsMatch = _builds.Count == builds.Count &&
            _builds.Zip(builds).All(pair =>
                string.Equals(pair.First.RelativePath, pair.Second.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                pair.First.IsInstalled == pair.Second.IsInstalled);
        if (!buildPathsMatch)
        {
            _suppressBuildPersistence = true;
            try
            {
                _builds.Clear();
                foreach (var build in builds)
                {
                    _builds.Add(build);
                }
            }
            finally
            {
                _suppressBuildPersistence = false;
            }
        }

        var preferredRelativePath = _settings.ClientRelativePath;
        var selectedBuild = _builds.FirstOrDefault(build =>
                string.Equals(build.RelativePath, preferredRelativePath, StringComparison.OrdinalIgnoreCase)) ??
            _builds.FirstOrDefault(build =>
                string.Equals(build.RelativePath, selectedRelativePath, StringComparison.OrdinalIgnoreCase)) ??
            // Nothing remembered: the first build there is, by name. One that
            // is already downloaded comes before one that is only offered,
            // because pressing Play on the second spends half an hour.
            _builds.FirstOrDefault(build => build.IsInstalled) ??
            _builds.FirstOrDefault();

        _suppressBuildPersistence = true;
        try
        {
            BuildComboBox.SelectedItem = selectedBuild;
        }
        finally
        {
            _suppressBuildPersistence = false;
        }
        if (selectedBuild is null)
        {
            BuildComboBox.SelectedItem = null;
        }

        // A build the launcher chose on its own is still the player's build: it
        // is written down like any other choice, and its preset is looked at
        // straight away, because the selection handler is suppressed for it.
        if (selectedBuild is not null &&
            !string.Equals(_settings.ClientRelativePath, selectedBuild.RelativePath, StringComparison.OrdinalIgnoreCase))
        {
            _settings.ClientRelativePath = selectedBuild.RelativePath;
            _settingsService.Save(_settings);
            RefreshPackMemory();
            RefreshControlsPresetStatus();
            _ = RefreshPackHashAndNetworkingAsync();
        }
    }

    private void RefreshWorlds()
    {
        if (_paths is null || _settings is null || _settingsService is null) return;

        if (!Directory.Exists(_paths.Worlds))
        {
            _worlds.Clear();
            WorldComboBox.SelectedItem = null;
            RefreshUi();
            return;
        }

        var selectedPath = (WorldComboBox.SelectedItem as WorldViewModel)?.Path;
        var metadataContext = CreateWorldMetadataContext();
        var worlds = WorldLocations.Enumerate(_paths.Worlds)
            .Where(WorldTransferService.IsMinecraftWorldDirectory)
            .Where(path => !Path.GetFileName(path).Contains(".backup-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
            .Select(path => CreateWorldViewModel(path, metadataContext))
            // Every world, whatever build it belongs to. This list is what a
            // world is handed over from, and a world can only be handed over by
            // whoever is holding it - which used to mean switching builds first,
            // to a build that then had to be prepared and launched for no other
            // reason. Each row says which build it belongs to.
            //
            // Opening a world under the wrong build is still how the blocks of
            // every mod that build does not have are lost, and that is still
            // prevented - in the one place it can be. The game is given a saves
            // folder holding only the worlds this build may open, so a world
            // named here that belongs elsewhere is not in the game's list at all.
            .ToList();

        var worldsMatch = _worlds.Count == worlds.Count &&
            _worlds.Zip(worlds).All(pair =>
                string.Equals(pair.First.Path, pair.Second.Path, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(pair.First.DisplayName, pair.Second.DisplayName, StringComparison.Ordinal));
        if (!worldsMatch)
        {
            _worlds.Clear();
            foreach (var world in worlds)
            {
                _worlds.Add(world);
            }
        }

        var savedPath = string.IsNullOrWhiteSpace(_settings.SelectedWorldRelativePath)
            ? null
            : Path.GetFullPath(Path.Combine(_paths.Worlds, _settings.SelectedWorldRelativePath));
        var selectedWorld = _worlds.FirstOrDefault(world =>
                savedPath is not null && string.Equals(world.Path, savedPath, StringComparison.OrdinalIgnoreCase)) ??
            _worlds.FirstOrDefault(world =>
                string.Equals(world.Path, selectedPath, StringComparison.OrdinalIgnoreCase)) ??
            _worlds.FirstOrDefault();

        WorldComboBox.SelectedItem = selectedWorld;
        if (selectedWorld is not null)
        {
            var relativePath = Path.GetRelativePath(_paths.Worlds, selectedWorld.Path);
            if (!string.Equals(_settings.SelectedWorldRelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            {
                _settings.SelectedWorldRelativePath = relativePath;
                _settingsService.Save(_settings);
            }
        }
    }

    /// <summary>
    /// Pushes the bug-report state into the panel in the main window: who can
    /// receive a report right now, and what happened to the last one.
    /// </summary>
    internal void RefreshDiagnosticsPanel()
    {
        var targets = BuildDiagnosticLogTargets();
        {
            var selected = DiagnosticLogTargetComboBox.SelectedItem as DiagnosticLogTargetOption;
            // Rebuilding the list on every tick would close the drop-down the
            // moment a player opened it.
            if (!targets.SequenceEqual(
                    (DiagnosticLogTargetComboBox.ItemsSource as IEnumerable<DiagnosticLogTargetOption>) ?? []))
            {
                DiagnosticLogTargetComboBox.ItemsSource = targets;
                // The same answer the player list gives: keep whoever was
                // chosen, otherwise take the first - a friend appearing online
                // is already the choice, and a list of one is not a question.
                DiagnosticLogTargetComboBox.SelectedItem =
                    targets.FirstOrDefault(option => option == selected) ?? targets.FirstOrDefault();
            }
        }

        // Nothing here is ever switched off. This is the panel a player reaches
        // for when something has gone wrong, and what goes wrong first is Steam
        // and the friends list - the very things the panel used to wait for
        // before it would let itself be touched. It stays live and says why
        // instead: the reason belongs in the line under the button, where it can
        // be read, not in a grey button that explains nothing.
        DiagnosticLogTargetPlaceholderText.Visibility =
            targets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowBugReportStatus();
    }

    /// <summary>
    /// Packs the last of this player's logs, with whatever they typed, and
    /// sends it to the friend they picked. Nothing streams: the report is a
    /// snapshot of the moment they noticed something was wrong.
    /// </summary>
    private async void SendBugReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_bugReports is null)
        {
            SetBugReportStatus("Лаунчер ещё готовится - отчёт можно будет отправить через несколько секунд.");
            return;
        }
        if (_bugReportSending)
        {
            SetBugReportStatus(_bugReportStatus);
            return;
        }
        if (!IsIdentityBound)
        {
            SetBugReportStatus("Steam ещё не подключился - без него отчёт некому передать.");
            return;
        }
        if (DiagnosticLogTargetComboBox.SelectedItem is not DiagnosticLogTargetOption recipient)
        {
            SetBugReportStatus("Некому отправить отчёт");
            return;
        }

        _bugReportSending = true;
        _bugReportRate.Reset();
        SetBugReportStatus($"Подготовка отчёта для {recipient.DisplayName}…");
        try
        {
            var message = BugReportMessageTextBox.Text;
            var progress = new Progress<BugReportProgress>(value => ApplyBugReportProgress(recipient, value));
            var manifest = await _bugReports
                .SendAsync(recipient.SteamId, message, progress, _lifetimeCts.Token)
                .ConfigureAwait(true);
            BugReportMessageTextBox.Clear();
            SetBugReportStatus(
                $"Отчёт отправлен игроку {recipient.DisplayName} " +
                $"({DescribeBytes(manifest.ArchiveBytes)}).");
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Bug report could not be sent: {ex.Message}");
            SetBugReportStatus($"Не удалось отправить отчёт: {ex.Message}");
        }
        finally
        {
            _bugReportSending = false;
            RefreshDiagnosticsPanel();
        }
    }

    /// <summary>
    /// The line under the button while a report is going out: how much of it
    /// has left, out of how much, and how fast. A report is megabytes over a
    /// Steam relay, so a still line reads as a hang.
    /// </summary>
    private void ApplyBugReportProgress(DiagnosticLogTargetOption recipient, BugReportProgress progress)
    {
        var speed = _bugReportRate.Update(progress.SentBytes, recipient.SteamId.ToString());
        var line = $"Отправка отчёта игроку {recipient.DisplayName}: " +
                   $"{DescribeBytes(progress.SentBytes)} из {DescribeBytes(progress.TotalBytes)}";
        if (speed > 0) line += $" ({DescribeBytes((long)speed)}/с)";
        SetBugReportStatus(line);
    }

    private static string DescribeBytes(long bytes) =>
        bytes >= 1024L * 1024
            ? $"{bytes / (1024d * 1024d):F1} МиБ"
            : $"{Math.Max(0, bytes) / 1024d:F0} КиБ";

    private void SetBugReportStatus(string message)
    {
        _bugReportStatus = message;
        ShowBugReportStatus();
    }

    /// <summary>
    /// The status line, or - while nothing has happened - what stands in for
    /// it. An empty box under a filled one reads as a box that broke, so it
    /// says the panel is fine; and when Steam is not signed in it says the one
    /// thing there is to do about it, because that is also why the list of
    /// people to send the report to is empty.
    /// </summary>
    private void ShowBugReportStatus() =>
        DiagnosticLogStatusText.Text = _bugReportStatus.Length > 0
            ? _bugReportStatus
            : _steamClient?.Status.IsReady == true && IsIdentityBound
                ? "Всё работает :)"
                : "Включите Steam и нажмите кнопку " +
                  "«Повторить» в нижнем правом углу экрана";

    internal void OpenSupportLogsDirectory()
    {
        if (_paths is null || _bugReports is null) return;
        try
        {
            Directory.CreateDirectory(_bugReports.ReportsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _bugReports.ReportsDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is IOException or
                                   InvalidOperationException or
                                   System.ComponentModel.Win32Exception)
        {
            _logger?.Warn($"Received bug report directory could not be opened: {ex.Message}");
        }
    }

    private List<DiagnosticLogTargetOption> BuildDiagnosticLogTargets()
    {
        // Steam serves a friend's presence on its own schedule, and a gap in it
        // empties the player list for a tick. That is fine for a transfer, which
        // needs the peer right now, but not for a report: a player types what
        // happened for a while, and the button must not die under their hands.
        // A friend who was there a minute ago is still worth offering - and if
        // they are really gone, the send says so.
        var cutoff = DateTimeOffset.Now - DiagnosticTargetTtl;
        var result = new List<DiagnosticLogTargetOption>();
        // Anyone online can be sent a report. Whether their build can take it
        // is the send's problem, and it says so out loud - an empty list here
        // would only leave a player who needs help with nowhere to click.
        foreach (var peer in ListOrder.Players(_peers.Where(peer => peer.LastSeen >= cutoff)))
        {
            // The name alone. The transfer list says where a player is because
            // that decides whether a world can reach them; a report has no such
            // question - anyone can take one - so the status here would be a
            // line of text that changes nothing about the choice.
            result.Add(new DiagnosticLogTargetOption(peer.SteamId, peer.PeerName));
        }
        return result;
    }

    private static SteamId64 GetSelectablePeerId(PeerViewModel? peer) =>
        peer?.SteamId ?? SteamId64.None;

    private static PeerViewModel? FindMatchingPeer(
        ObservableCollection<PeerViewModel> peers,
        SteamId64 selectedPeerId) =>
        peers.FirstOrDefault(peer => peer.SteamId == selectedPeerId);

    private WorldMetadataContext? CreateWorldMetadataContext()
    {
        // Steam decides who the player is; until it has, no world is stamped
        // with an owner and the list simply shows what is on disk.
        if (!IsIdentityBound || BuildComboBox.SelectedItem is not ClientBuildViewModel build)
        {
            return null;
        }
        var owner = GetActiveLocalOwner();
        var ownerSteamId = RequireIdentityService().ResolveContext(RequireSettings()).SteamId64.ToString();

        return new WorldMetadataContext
        {
            BuildName = build.Name,
            BuildRelativePath = build.RelativePath,
            PackHash = _localPackHash,
            OwnerIdentityId = owner.id,
            OwnerIdentityName = owner.name,
            OwnerSteamId64 = ownerSteamId
        };
    }

    private WorldViewModel CreateWorldViewModel(string path, WorldMetadataContext? metadataContext)
    {
        // Listing a world never decides which build it belongs to. Writing the
        // selected build into every unlabelled world made the filter below
        // compare that label against the build that had just written it, so a
        // world was claimed by whichever build opened its list first and shown
        // there ever after. Only playing a world says where it belongs.
        var metadata = RequireWorldMetadata().EnsureMetadata(path, metadataContext, claimBuild: false);
        if (metadataContext is not null)
        {
            _ = RequireWorldMetadata().TryWriteOwnerMetadata(
                path,
                metadataContext.OwnerIdentityId,
                metadataContext.OwnerIdentityName,
                overwriteExistingOwner: false,
                ownerSteamId64: metadataContext.OwnerSteamId64);
            _ = RequireWorldMetadata().TryWriteCurrentHolderMetadata(
                path,
                metadataContext.OwnerIdentityId,
                metadataContext.OwnerIdentityName,
                transferred: false,
                holderSteamId64: metadataContext.OwnerSteamId64);
        }

        var buildName = string.IsNullOrWhiteSpace(metadata?.BuildName)
            ? RequireWorldMetadata().GetBuildName(path)
            : metadata.BuildName;

        return new WorldViewModel
        {
            Name = Path.GetFileName(path),
            Path = path,
            BuildName = buildName,
            BuildRelativePath = metadata?.BuildRelativePath ?? ""
        };
    }

    /// <summary>
    /// Brings up everything that needs other players: the connection router and
    /// the presence keys friends discover this launcher by. Without Steam there
    /// is nothing to start, and the window says so instead of failing.
    /// </summary>
    private async Task StartNetworkingAsync()
    {
        var settings = RequireSettings();
        if (!IsIdentityBound || _steamClient?.Status.IsReady != true)
        {
            RequireTransfer().StopAcceptingIncomingTransfers();
            RequireLogger().Warn("Steam is unavailable; network play stays off.");
            return;
        }

        RequireTransfer().UseSettingsForIncomingTransfers(settings);
        if (_peerRouter is not null)
        {
            await _peerRouter.StartAsync(_lifetimeCts.Token).ConfigureAwait(true);
        }
        PublishLocalPresence();
    }

    private async Task RefreshPackHashAndNetworkingAsync()
    {
        try
        {
            await RefreshPackHashAsync();
            if (_startupComplete) await StartNetworkingAsync();
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Pack refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Publishes what friends need to know about this launcher. This is the
    /// successor of the UDP announcement: the same facts, carried by Steam.
    /// </summary>
    private void PublishLocalPresence()
    {
        if (_peerDirectory is null || _steamClient?.Status.IsReady != true) return;
        if (!SteamId64.TryFrom(_steamClient.Status.SteamId64, out var localId)) return;

        var settings = RequireSettings();
        var identity = ResolveActiveLocalIdentity();
        var identityContext = RequireIdentityService().ResolveContext(settings);
        RequireWaypointSync().UpdateHostingState(
            false,
            settings.ClientRelativePath,
            _localPackHash,
            identityContext);
        var waypointHost = RequireWaypointSync().GetHostAdvertisement();
        var skin = RequireSkinService().GetAnnouncement(settings, identityContext.MinecraftUuid);
        var presence = new SteamPeerPresence
        {
            SteamId = localId,
            PersonaName = _steamClient.Status.PersonaName,
            ProtocolVersion = SteamPresenceCodec.ProtocolVersion,
            PlayerName = identity.name,
            MinecraftUuid = identityContext.MinecraftUuid,
            PackHash = _localPackHash,
            // The folder is the name: a build the launcher offers is named by
            // its folder, and one somebody put there themselves has no other
            // name to be known by.
            PackName = settings.ClientRelativePath,
            Release = UpdateService.CurrentReleaseNumber,
            State = _minecraftRunning
                ? SteamPresenceCodec.StateInGame
                : _minecraftPreparing
                    ? SteamPresenceCodec.StatePreparing
                    : SteamPresenceCodec.StateIdle,
            IsSkinAvailable = skin.IsAvailable,
            SkinSha256 = skin.Sha256,
            SkinModel = skin.Model,
            HostedWorldId = waypointHost?.WorldId ?? string.Empty,
            WaypointProtocolVersion = WaypointSyncService.ProtocolVersion,
            WaypointProviders = waypointHost?.Providers.ToList() ?? [],
            // Anyone running this build can receive a bug report.
            DiagnosticProtocolVersion = BugReportManifest.ProtocolVersion
        };
        _peerDirectory.PublishLocalPresence(presence);
        // Kept so the goodbye on the way out can be written without rebuilding
        // any of this: at that point the skin and waypoint services are already
        // going away.
        _lastPublishedPresence = presence;
        // Steam decides per client whose presence it serves; a friend we can
        // see may not be able to see us. Telling them ourselves is the only
        // way to be sure the two lists agree.
        _greetings?.SetLocalPresence(presence);
        _greetings?.GreetNew(_peerDirectory.Peers, _lifetimeCts.Token);
    }

    /// <summary>What a report says about the machine it came from.</summary>
    private BugReportContext CreateBugReportContext()
    {
        var settings = RequireSettings();
        var identity = RequireIdentityService().ResolveContext(settings);
        return new BugReportContext(
            identity.SteamId64,
            _steamClient?.Status.PersonaName ?? string.Empty,
            identity.IdentityName,
            identity.MinecraftUuid,
            BuildVersionText(),
            settings.ClientRelativePath,
            _localPackHash,
            _minecraftRunning);
    }

    private string? ResolveCurrentInstanceDirectory()
    {
        if (_paths is null || _settings is null ||
            string.IsNullOrWhiteSpace(_settings.ClientRelativePath))
        {
            return null;
        }

        try
        {
            return _paths.CombineUnderInstances(_settings.ClientRelativePath);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private Task<SupportEnvironmentSnapshot> CaptureSupportEnvironmentAsync(
        CancellationToken token)
    {
        var settings = RequireSettings();
        return SupportDiagnosticSnapshotBuilder.CaptureAsync(
            new SupportDiagnosticSnapshotRequest(
                RequirePaths(),
                settings.ClientRelativePath,
                _localPackHash,
                CaptureSteamDiagnosticContext(),
                BuildSupportRuntimeState(),
                _minecraft?.DiagnosticJavaPath),
            token);
    }

    private SteamDiagnosticContext CaptureSteamDiagnosticContext()
    {
        var status = _steamClient?.Status;
        return new SteamDiagnosticContext(
            status?.SteamId64.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            status?.PersonaName ?? string.Empty,
            status?.Availability.ToString() ?? SteamAvailability.NotStarted.ToString(),
            _steamClient?.Friends.Count ?? 0,
            _peerDirectory?.Peers.Count ?? 0);
    }

    private Dictionary<string, string> BuildSupportRuntimeState()
    {
        var identity = _settings is null
            ? (id: string.Empty, name: string.Empty)
            : ResolveActiveLocalIdentity();
        var steam = CaptureSteamDiagnosticContext();
        var state = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["launcher.state"] = _state,
            ["launcher.identityId"] = identity.id,
            ["launcher.playerName"] = identity.name,
            ["game.running"] = _minecraftRunning.ToString(CultureInfo.InvariantCulture),
            ["game.preparing"] = _minecraftPreparing.ToString(CultureInfo.InvariantCulture),
            ["game.version"] = _minecraft?.DiagnosticGameVersion ?? string.Empty,
            ["game.profile"] = _minecraft?.DiagnosticProfileId ?? string.Empty,
            ["pack.hash"] = _localPackHash,
            ["steam.id64"] = steam.SteamId64,
            ["steam.persona"] = steam.PersonaName,
            ["steam.availability"] = steam.Availability,
            ["steam.friends"] = steam.FriendCount.ToString(CultureInfo.InvariantCulture),
            ["steam.identityBound"] = IsIdentityBound.ToString(CultureInfo.InvariantCulture),
            ["steam.presenceProtocolVersion"] =
                SteamPresenceCodec.ProtocolVersion.ToString(CultureInfo.InvariantCulture),
            ["transfer.active"] =
                (_transfer?.IsOperationActive == true).ToString(CultureInfo.InvariantCulture),
            ["transfer.bytesCurrent"] =
                Interlocked.Read(ref _transferBytesCurrent).ToString(CultureInfo.InvariantCulture),
            ["transfer.bytesTotal"] =
                Interlocked.Read(ref _transferBytesTotal).ToString(CultureInfo.InvariantCulture)
        };

        foreach (var pair in BuildPeerSupportRuntimeState())
        {
            state[pair.Key] = pair.Value;
        }
        return state;
    }

    private IReadOnlyDictionary<string, string> BuildPeerSupportRuntimeState()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (_shutdownStarted ||
                Dispatcher.HasShutdownStarted ||
                Dispatcher.HasShutdownFinished)
            {
                return new Dictionary<string, string>();
            }

            try
            {
                return Dispatcher.Invoke(BuildPeerSupportRuntimeState);
            }
            catch (InvalidOperationException) when (
                Dispatcher.HasShutdownStarted ||
                Dispatcher.HasShutdownFinished)
            {
                return new Dictionary<string, string>();
            }
            catch (TaskCanceledException)
            {
                return new Dictionary<string, string>();
            }
        }

        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        var cutoff = DateTimeOffset.Now - PeerTtl;
        var peerIndex = 0;
        foreach (var peer in _peers
                     .Where(peer => peer.LastSeen >= cutoff)
                     .OrderBy(peer => peer.SteamId.Value)
                     .Take(32))
        {
            var prefix = $"peer.{peerIndex++}";
            state[$"{prefix}.steamId64"] = peer.SteamId.ToString();
            state[$"{prefix}.personaName"] = Clamp(peer.PersonaName);
            state[$"{prefix}.playerName"] = Clamp(peer.PlayerName);
            state[$"{prefix}.state"] = peer.IsMinecraftRunning
                ? SteamPresenceCodec.StateInGame
                : peer.IsMinecraftPreparing
                    ? SteamPresenceCodec.StatePreparing
                    : SteamPresenceCodec.StateIdle;
            state[$"{prefix}.lastSeenUtc"] =
                peer.LastSeen.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            state[$"{prefix}.diagnosticCompatible"] =
                peer.SupportsDiagnosticLogs.ToString(CultureInfo.InvariantCulture);
        }

        state["peer.count"] = peerIndex.ToString(CultureInfo.InvariantCulture);
        return state;

        static string Clamp(string value) => value.Length <= 128 ? value : value[..128];
    }

    private void PostToUi(Action action)
    {
        if (_shutdownStarted ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
            return;
        }
        try
        {
            _ = Dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException) when (
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
        }
    }

    /// <summary>
    /// Reconciles the window with the friends Steam currently reports. The VPN
    /// era merged one announcement at a time; presence arrives as a whole list,
    /// so anyone missing from it has simply stopped publishing.
    /// </summary>
    private void ApplyPeers(IReadOnlyList<SteamPeerPresence> peers)
    {
        if (_shutdownStarted) return;
        var localId = _steamClient?.Status.SteamId64 ?? 0;
        var selectedPeerId = GetSelectablePeerId(OnlinePlayerComboBox.SelectedItem as PeerViewModel);

        foreach (var presence in peers)
        {
            if (presence.SteamId.Value == localId) continue;
            RequireWaypointSync().ObservePeer(presence);

            var peer = _peers.FirstOrDefault(candidate => candidate.SteamId == presence.SteamId);
            if (peer is null)
            {
                peer = new PeerViewModel { SteamId = presence.SteamId };
                _peers.Add(peer);
            }
            peer.Apply(presence, _localPackHash);
            RequireSkinService().ObservePeer(peer);
            _identityRegistry?.ObservePeer(peer);
        }

        var current = peers.Select(presence => presence.SteamId).ToHashSet();
        for (var index = _peers.Count - 1; index >= 0; index--)
        {
            if (!current.Contains(_peers[index].SteamId)) _peers.RemoveAt(index);
        }

        SortPeersByName();

        OnlinePlayerComboBox.SelectedItem =
            FindMatchingPeer(_peers, selectedPeerId) ?? _peers.FirstOrDefault();
        RefreshDiagnosticsPanel();
        RefreshUi();
    }

    /// <summary>
    /// Puts the players in the order the other list of them uses.
    ///
    /// The list a world is handed over from is bound straight to the
    /// collection, so it read in whatever order Steam answered in - which
    /// changes between refreshes - while the list a report is sent to, of the
    /// same friends, was sorted by name. Two orders for one set of people is a
    /// way to hand a world to the wrong one.
    ///
    /// Moved rather than rebuilt: clearing the collection would drop whoever
    /// is selected.
    /// </summary>
    private void SortPeersByName()
    {
        var sorted = ListOrder.Players(_peers).ToList();
        for (var index = 0; index < sorted.Count; index++)
        {
            var current = _peers.IndexOf(sorted[index]);
            if (current != index) _peers.Move(current, index);
        }
    }

    /// <summary>
    /// Drops peers whose presence stopped being refreshed and forgets a
    /// diagnostics target that went with them.
    /// </summary>
    private void PruneStalePeers()
    {
        var cutoff = DateTimeOffset.Now - PeerTtl;
        var selectedPeerId = GetSelectablePeerId(OnlinePlayerComboBox.SelectedItem as PeerViewModel);
        for (var index = _peers.Count - 1; index >= 0; index--)
        {
            if (_peers[index].LastSeen < cutoff) _peers.RemoveAt(index);
        }

        OnlinePlayerComboBox.SelectedItem =
            FindMatchingPeer(_peers, selectedPeerId) ?? _peers.FirstOrDefault();

        RefreshDiagnosticsPanel();
    }

    private readonly record struct LanPortDetection(int? Port, long Generation)
    {
        public static LanPortDetection None { get; } = new(null, 0);
    }

    private void OnMinecraftClientRunningChanged(bool isRunning)
    {
        PostToUi(() =>
        {
            _minecraftRunning = isRunning;
            if (isRunning)
            {
                _sessionStartedUtc = DateTimeOffset.UtcNow;
            }

            if (!isRunning)
            {
                StampWorldsPlayedThisSession();
                RefreshWorlds();
                RefreshControlsPresetStatus();
                // The session may have left a measurement behind, and the split
                // of the budget for the next launch is already a different one:
                // the field has to describe that launch, not the one before it.
                RefreshPackMemory();
            }
            RefreshUi();
        });
    }

    /// <summary>
    /// Gives a build to any world that was played just now and had none.
    ///
    /// A world nobody stamped is offered in every build, on purpose: hiding a
    /// world the launcher cannot place is worse than showing it in the wrong
    /// list. But it leaves worlds older than this file, and worlds dropped into
    /// the folder by hand, ambiguous forever - and opening one under the wrong
    /// build is how the blocks of every missing mod are lost. Which world the
    /// game will open is not the launcher's to know; which one it did open is
    /// written in that world's session.lock, so the answer is taken from what
    /// happened rather than from a guess.
    /// </summary>
    private void StampWorldsPlayedThisSession()
    {
        if (_sessionStartedUtc is not { } startedUtc) return;
        _sessionStartedUtc = null;

        var context = CreateWorldMetadataContext();
        if (context is null || _paths is null) return;

        try
        {
            // A world the player just made is a real folder inside the
            // instance, because the game made it there; it has to be moved
            // beside the others before it can be stamped, or it is not among
            // the worlds this looks at.
            var instance = ResolveCurrentInstanceDirectory();
            if (instance is not null)
            {
                new SavesFolderService(_logger).Adopt(
                    WorldLocations.ForBuild(_paths.Worlds, _settings.ClientRelativePath),
                    instance);
            }
            var stamped = RequireWorldMetadata().StampPlayedWorlds(_paths.Worlds, context, startedUtc);
            foreach (var world in stamped)
            {
                _logger?.Info(
                    $"World {world} had no build recorded; it belongs to {context.BuildName} " +
                    "from now on, because that is what it was played on.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warn($"Worlds played this session could not be attributed ({ex.Message}).");
        }
    }

    /// <summary>
    /// The game closed because it ran out of memory. It is said in the line
    /// under the report panel: that is where the launcher already speaks, and
    /// the button that sends the logs is right beside it.
    /// </summary>
    /// <summary>
    /// Said as the game starts, while it can still be acted on: the number in
    /// the settings is smaller than this pack can run in, and the game is about
    /// to prove it.
    /// </summary>
    /// <remarks>
    /// Two numbers and the name of the box they go in, and nothing else. Why
    /// the pack needs it is a paragraph, and a player reading a line above a
    /// button they are already pressing does not want a paragraph - they want
    /// to know where to type and what. "RAM" is the label beside that box in
    /// this very window, so it is the word used here rather than a description
    /// of it.
    ///
    /// Unless the number cannot be typed. A machine keeps a quarter of itself
    /// back, so a laptop of eight gigabytes will not accept more than four or
    /// five - and a pack of three hundred mods wants more than that before its
    /// heap is off the floor. Telling that player to set ten is telling them to
    /// do something the box refuses; the honest version of the sentence is that
    /// the pack does not fit the machine, and this launcher says that the way
    /// its owner wants it said.
    /// </remarks>
    private void OnMinecraftMemoryIsTooSmall(int chosenGb, int neededGb)
    {
        PostToUi(() => SetBugReportStatus(
            neededGb > GetAllowedHeapGb()
                ? "Обнаружен компьютер со слабой аурой"
                : $"Этой сборке мало {chosenGb} ГБ, поставьте в RAM от {neededGb} ГБ."));
    }

    private void OnMinecraftRanOutOfMemory(int maxMemoryGb)
    {
        PostToUi(() => SetBugReportStatus(
            (maxMemoryGb > 0 ? $"Игре не хватило памяти: ей выделено {maxMemoryGb} ГБ. " : "Игре не хватило памяти. ") +
            "Мир не пострадал - игра закрылась, не успев предложить открыть его без модов."));
    }

    private void OnMinecraftClientPreparingChanged(bool isPreparing)
    {
        PostToUi(() =>
        {
            _minecraftPreparing = isPreparing;
            RefreshUi();
        });
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_minecraftPreparing || _minecraftRunning) return;
        _minecraftPreparing = true;
        _playProgressText = "Проверка сборки";
        RefreshUi();
        try
        {
            if (RequireTransfer().IsOperationActive)
            {
                throw new InvalidOperationException("Wait for the world transfer to finish before starting Minecraft.");
            }
            ApplyPlayerName();
            ApplyMemoryText(chosenByPlayer: false);
            var settings = RequireSettings();
            if (BuildComboBox.SelectedItem is not ClientBuildViewModel build)
            {
                throw new InvalidOperationException($"No Minecraft pack with {PackManifestService.ManifestFileName} was found in ./Minecraft/Packs.");
            }

            if (!string.Equals(settings.ClientRelativePath, build.RelativePath, StringComparison.OrdinalIgnoreCase))
            {
                settings.ClientRelativePath = build.RelativePath;
                RequireSettingsService().Save(settings);
            }

            var runtimeProgress = new Progress<RuntimePreparationProgress>(ApplyRuntimeProgress);
            PackSyncResult syncResult;
            try
            {
                syncResult = await RequirePackSync().SyncAsync(build.RelativePath, runtimeProgress, _lifetimeCts.Token);
            }
            catch
            {
                ApplyRuntimeProgress(new RuntimePreparationProgress(
                    RuntimePreparationStage.Failed,
                    "Не удалось подготовить сборку"));
                throw;
            }
            if (syncResult.Warning is not null)
            {
                RequireLogger().Warn(syncResult.Warning);
                SetState(syncResult.Warning);
                // And where somebody can read it. SetState only fills a field
                // the support snapshot carries, so everything the sync has ever
                // had to say - that it could not reach the internet and is
                // playing the copy on disk, that an update has taken mods away -
                // went into a diagnostic nobody opens.
                SetBugReportStatus(syncResult.Warning);
            }

            // A folder somebody filled with mods becomes a pack here: what
            // loader and which Minecraft comes out of the jars, which build of
            // that loader comes from whoever publishes it, and the answer is
            // written into the folder as the manifest every service downstream
            // already reads. A pack that came with one is untouched.
            if (_autoManifest is not null)
            {
                await _autoManifest.EnsureAsync(build.RelativePath, _lifetimeCts.Token);
            }

            await RefreshPackHashAsync();
            // The pack that just arrived may weigh something else than the one
            // that was here: the split, and a suggestion nobody has overruled,
            // are worked out again before the game is told its heap size.
            RefreshPackMemory();
            // Before the game reads its options: a mapping written twice stops
            // NeoForge before its loading window, and the player cannot reach
            // the preset button to fix it because the preset counts as applied.
            RepairInstanceOptions();
            RefreshControlsPresetStatus();
            if (_localPackHash == "missing")
            {
                throw new InvalidOperationException($"Pack validation failed: ./Minecraft/Packs/{settings.ClientRelativePath} is missing.");
            }

            RequireSettingsService().Save(settings);

            SetState("Starting client");
            try
            {
                await RequireMinecraft().StartClientAsync(settings, runtimeProgress, _lifetimeCts.Token);
            }
            catch
            {
                ApplyRuntimeProgress(new RuntimePreparationProgress(
                    RuntimePreparationStage.Failed,
                    "Не удалось подготовить сборку"));
                throw;
            }
            SetState("Minecraft");
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RequireLogger().Warn(ex.Message);
            MessageBox.Show(ex.Message, "Minecraft", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _minecraftPreparing = _minecraft?.IsClientPreparing == true;
            RefreshUi();
        }
    }

    /// <summary>
    /// The pack directory and the instance directory of the selected build, or
    /// null while nothing is selected. Both may not exist yet.
    /// </summary>
    private (string Pack, string Instance)? ResolveSelectedBuildDirectories()
    {
        if (_paths is null || BuildComboBox.SelectedItem is not ClientBuildViewModel build) return null;
        try
        {
            return (_paths.CombineUnderPacks(build.RelativePath), _paths.CombineUnderInstances(build.RelativePath));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Re-reads where the selected build stands against its pack's controls
    /// preset. Cheap - two small files - and called at the moments either can
    /// change: a build is picked, the pack syncs, the game exits, the button
    /// is pressed.
    /// </summary>
    private void RepairInstanceOptions()
    {
        var directories = ResolveSelectedBuildDirectories();
        if (directories is null) return;
        var removed = _controlsPreset?.RemoveDuplicateMappings(directories.Value.Instance) ?? 0;
        if (removed > 0) SetState($"Repaired the controls file: {removed} repeated line(s)");
        // The pack's own resource packs are switched on once, for an instance
        // that has played before and would otherwise get the files and never
        // see them selected. Afterwards the choice belongs to the player.
        _resourcePackDefaults?.Apply(directories.Value.Pack, directories.Value.Instance);
        // And the settings a pack built for a small machine wants to be met
        // with - render distance and the rest - which only reach an instance
        // that does not already have them, so they are a starting point and
        // never a correction.
        _optionsDefaults?.Apply(directories.Value.Pack, directories.Value.Instance);
        // And when the pack has reset the world's chunks, the map a minimap has
        // already drawn is a picture of ground that no longer exists.
        _minimapReset?.Apply(directories.Value.Pack, directories.Value.Instance);
    }

    /// <summary>
    /// Fetches the pack's own launcher folder - the controls preset, the
    /// resource pack list, the reset tokens - without waiting for a launch, and
    /// looks at the preset again once it lands. Tens of kilobytes: the button
    /// can offer a layout published minutes ago instead of claiming the old one
    /// is applied until somebody presses Play.
    /// </summary>
    private async Task RefreshLauncherDataAsync(string packRelativePath)
    {
        if (_packSync is null) return;
        try
        {
            var refreshed = await _packSync
                .RefreshLauncherDataAsync(packRelativePath, _lifetimeCts.Token)
                .ConfigureAwait(true);
            if (!refreshed) return;
            RefreshControlsPresetStatus();
            RefreshUi();
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Works out whether the pack's controls preset is already in the instance's
    /// options. Called on every tick, so it reads the two files only when one of
    /// them has changed since the last answer - the preset is six hundred lines
    /// and the options as many, and neither moves between launches.
    /// </summary>
    private void RefreshControlsPresetStatus()
    {
        var directories = ResolveSelectedBuildDirectories();
        if (_controlsPreset is null || directories is null)
        {
            _controlsPresetStatus = default;
            _controlsPresetStamp = null;
            return;
        }

        var stamp = ControlsPresetStamp(directories.Value.Pack, directories.Value.Instance);
        if (stamp is not null && stamp == _controlsPresetStamp) return;
        _controlsPresetStamp = stamp;
        _controlsPresetStatus = _controlsPreset.Evaluate(directories.Value.Pack, directories.Value.Instance);
    }

    /// <summary>
    /// What the two files looked like: their paths, sizes and moments of
    /// writing. Null when either cannot be looked at, which forces a real read.
    /// </summary>
    private static string? ControlsPresetStamp(string packDirectory, string instanceDirectory)
    {
        try
        {
            var preset = new FileInfo(Path.Combine(
                packDirectory, ControlsPresetService.PresetRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            var options = new FileInfo(Path.Combine(instanceDirectory, ControlsPresetService.OptionsFileName));
            return string.Join(
                '|',
                packDirectory,
                preset.Exists ? preset.Length + "@" + preset.LastWriteTimeUtc.Ticks : "none",
                options.Exists ? options.Length + "@" + options.LastWriteTimeUtc.Ticks : "none");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void ControlsPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_minecraftRunning || _controlsPreset is null) return;
        var directories = ResolveSelectedBuildDirectories();
        if (directories is null) return;

        if (!ControlsPresetConfirmationDialog.Ask(this, _controlsPresetStatus.FirstDifference)) return;

        try
        {
            var changed = _controlsPreset.Apply(directories.Value.Pack, directories.Value.Instance);
            SetState(changed == 0
                ? "Controls preset already in place"
                : $"Controls preset applied: {changed} key(s) changed");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            RequireLogger().Warn($"Controls preset failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Пресет управления", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshControlsPresetStatus();
        RefreshUi();
    }

    /// <summary>
    /// Takes the selected build off the machine, once the player has said so in
    /// as many words.
    /// </summary>
    /// <remarks>
    /// The plan is worked out twice on purpose. The first is for the question -
    /// how many worlds there are, and whether a Java goes with it - and is made
    /// with the worlds kept, so nothing about asking can delete anything. The
    /// second is made after the answer, because "вместе с мирами" adds a folder
    /// the first plan deliberately did not name.
    /// </remarks>
    private void DeleteBuildButton_Click(object sender, RoutedEventArgs e)
    {
        if (_minecraftRunning || _paths is null || _settings is null || _settingsService is null) return;
        if (BuildComboBox.SelectedItem is not ClientBuildViewModel build || !build.IsInstalled) return;

        var removal = new BuildRemovalService(_paths, _packHash, _logger);
        BuildRemovalService.RemovalPlan plan;
        try
        {
            plan = removal.Plan(build.RelativePath, worldsToo: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidOperationException or ArgumentException)
        {
            RequireLogger().Warn($"Build removal could not be planned for {build.RelativePath}: {ex.Message}");
            SetState("Не удалось прочитать, из чего состоит сборка");
            return;
        }

        var answer = BuildRemovalConfirmationDialog.Ask(this, build.Name, plan.Worlds, plan.Java);
        if (answer == BuildRemovalAnswer.Keep) return;

        var worldsToo = answer == BuildRemovalAnswer.WithWorlds;
        var outcome = removal.Remove(worldsToo ? removal.Plan(build.RelativePath, worldsToo: true) : plan);
        if (BuildRemovalService.Forget(_settings, build.RelativePath)) _settingsService.Save(_settings);

        RefreshBuilds();
        RefreshControlsPresetStatus();
        RefreshUi();
        SetState(outcome.Complete
            ? $"Сборка удалена: {build.Name}" +
              (outcome.Worlds > 0 ? $", {BuildRemovalConfirmationDialog.Worlds(outcome.Worlds)}" : "") +
              (outcome.Java.Count > 0 ? $", Java {string.Join(", ", outcome.Java)}" : "")
            // A part that would not go is almost always the game or an
            // explorer window holding a file open, and saying which folder
            // stayed is the difference between closing it and reinstalling.
            : $"Сборка удалена не полностью - осталось: {string.Join(", ", outcome.Kept)}");
    }

    private void SkinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_minecraftRunning || _settings is null || _skinService is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите скин Minecraft",
            Filter = "PNG (*.png)|*.png",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var identity = RequireIdentityService().ResolveContext(_settings);
            var skin = _skinService.SelectLocalSkin(_settings, identity.MinecraftUuid, dialog.FileName);
            RequireSettingsService().Save(_settings);
            RefreshSkinHint();
            SetState($"Skin selected ({skin.Model})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidDataException or NotSupportedException or
                                   IdentityUnavailableException)
        {
            RequireLogger().Warn($"Skin selection failed: {ex.Message}");
            MessageBox.Show(ex.Message, "Minecraft", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void TransferButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transferActive || _minecraftRunning || _minecraftPreparing)
        {
            return;
        }

        try
        {
            if (WorldComboBox.SelectedItem is not WorldViewModel world)
            {
                throw new InvalidOperationException("Choose a world to transfer.");
            }

            if (OnlinePlayerComboBox.SelectedItem is not PeerViewModel peer)
            {
                throw new InvalidOperationException("Choose an online player.");
            }
            if (!peer.IsCompatible)
            {
                throw new InvalidOperationException(
                    $"{peer.PersonaName} использует другую версию лаунчера. " +
                    "Мир можно передать только после того, как оба обновятся.");
            }
            if (peer.IsMinecraftRunning || peer.IsMinecraftPreparing)
            {
                throw new InvalidOperationException("The selected player is currently in Minecraft or preparing it.");
            }

            var settings = RequireSettings();
            SetState("Transferring world");
            await RequireTransfer().SendWorldAsync(peer, settings, world.Path, _lifetimeCts.Token);
            RequireSettingsService().Save(settings);
            RefreshWorlds();
            SetState("World sent");
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The log and the bug report keep the raw text - it is what makes a
            // failure diagnosable. The player gets a sentence instead: the last
            // one said "Steam refused a message: k_EResultNoConnection", which
            // is English, names an internal enum, blames Steam for something
            // Steam did not do, and leaves out the only thing they want to know
            // after a failed multi-gigabyte transfer - whether their world
            // survived. It did: nothing leaves this machine until the far side
            // has the whole world and says so.
            RequireLogger().Warn(ex.Message);
            MessageBox.Show(
                "Передача мира прервалась." + Environment.NewLine +
                "Ваш мир на месте, ничего не потеряно - можно просто попробовать ещё раз." +
                Environment.NewLine + Environment.NewLine +
                "Если обрывается снова, попросите игрока не закрывать Steam и лаунчер " +
                "и не запускать Minecraft во время передачи." + Environment.NewLine +
                Environment.NewLine + "Подробности: " + ex.Message,
                "Minecraft",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            RefreshUi();
        }
    }

    private void PlayerNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextPersistence || !_isEditingPlayerName) return;
        RefreshUi();
    }

    private void PlayerNameTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            e.Handled = true;
            return;
        }

        var draft = textBox.Text.Remove(textBox.SelectionStart, textBox.SelectionLength)
            .Insert(textBox.SelectionStart, e.Text);
        e.Handled = !LocalIdentityService.IsNicknameDraftValid(draft);
    }

    private void PlayerNameTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox textBox ||
            !e.DataObject.GetDataPresent(DataFormats.Text) ||
            e.DataObject.GetData(DataFormats.Text) is not string text)
        {
            e.CancelCommand();
            return;
        }

        var draft = textBox.Text.Remove(textBox.SelectionStart, textBox.SelectionLength)
            .Insert(textBox.SelectionStart, text);
        if (!LocalIdentityService.IsNicknameDraftValid(draft))
        {
            e.CancelCommand();
        }
    }

    private void PlayerNameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
    }

    private async void PlayerNameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_isEditingPlayerName) return;
        if (e.Key == Key.Escape)
        {
            CancelPlayerNameEdit();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter) return;
        await SavePlayerNameAsync();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private async void ChangePlayerNameButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!_isEditingPlayerName)
        {
            _isEditingPlayerName = true;
            PlayerNameTextBox.IsReadOnly = false;
            ChangePlayerNameButton.Content = "Сохранить";
            PlayerNameTextBox.Focus();
            PlayerNameTextBox.SelectAll();
            RefreshUi();
            return;
        }

        await SavePlayerNameAsync();
    }

    private async Task SavePlayerNameAsync()
    {
        var candidate = PlayerNameTextBox.Text;
        await RunUiActionAsync(() =>
        {
            var settings = RequireSettings();
            if (!LocalIdentityService.TryNormalizeNewNickname(candidate, out var normalized, out var error))
            {
                throw new InvalidOperationException(error);
            }
            var previousName = LocalIdentityService.NormalizeNickname(settings.PlayerName, Environment.UserName);
            if (string.Equals(previousName, normalized, StringComparison.Ordinal))
            {
                FinishPlayerNameEdit(normalized);
                return Task.CompletedTask;
            }
            if (_minecraft?.IsClientRunning == true)
            {
                throw new InvalidOperationException("Close Minecraft before changing the nickname.");
            }
            settings.PreviousPlayerName = string.Equals(previousName, normalized, StringComparison.Ordinal)
                ? settings.PreviousPlayerName
                : previousName;
            settings.PlayerName = normalized;
            PersistActivePlayerIdentity();
            // The file the running game reads its names from is rewritten here
            // rather than at the next launch. A name that only travels when the
            // game starts is a name nobody else's server knows yet, and the
            // adapter on the other end re-reads this file whenever its timestamp
            // moves - so a rename can reach a world somebody is already in.
            RefreshIdentityRegistry();
            FinishPlayerNameEdit(normalized);
            return Task.CompletedTask;
        });
    }

    private void CancelPlayerNameEdit()
    {
        FinishPlayerNameEdit(RequireSettings().PlayerName);
    }

    private void FinishPlayerNameEdit(string name)
    {
        _suppressTextPersistence = true;
        try
        {
            PlayerNameTextBox.Text = name;
            PlayerNameTextBox.CaretIndex = name.Length;
        }
        finally
        {
            _suppressTextPersistence = false;
        }

        _isEditingPlayerName = false;
        PlayerNameTextBox.IsReadOnly = true;
        ChangePlayerNameButton.Content = "Изменить";
        RefreshUi();
    }

    /// <summary>
    /// Connects to Steam and binds this machine's account to the profile its
    /// progress lives under. A closed or signed-out Steam is a status with a
    /// "Повторить" button, never a startup failure - but nothing that needs an
    /// identity (playing, transferring, diagnostics) runs until it succeeds.
    /// </summary>
    private async Task ConnectSteamAndBindIdentityAsync()
    {
        if (_steamClient is null || _identityService is null) return;

        var status = await _steamClient.StartAsync(_lifetimeCts.Token).ConfigureAwait(true);
        ApplySteamStatus(status);
        if (!status.IsReady) return;

        try
        {
            var binding = _identityService.Bind();
            if (!binding.Bound)
            {
                SetSteamMessage(string.IsNullOrEmpty(binding.Message)
                    ? SteamIdentityService.SteamUnavailableMessage
                    : binding.Message);
                return;
            }

            ResolveAndPersistLocalIdentity();
            // A retry after Steam was fixed has to bring the network up too;
            // during startup this runs once more, and starting is idempotent.
            if (_startupComplete) await StartNetworkingAsync().ConfigureAwait(true);
        }
        catch (IdentityUnavailableException ex)
        {
            _logger?.Warn($"Identity binding failed: {ex.Message}");
            SetSteamMessage(ex.Message);
        }
        finally
        {
            RefreshUi();
        }
    }

    /// <summary>Re-runs the whole Steam handshake after the player fixes Steam.</summary>
    private async void RetrySteamButton_Click(object sender, RoutedEventArgs e)
    {
        RetrySteamButton.IsEnabled = false;
        try
        {
            await ConnectSteamAndBindIdentityAsync();
        }
        finally
        {
            RetrySteamButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Steam takes one spot in the footer: the name it signed in as, or - when
    /// it could not - the button that tries again. The account number behind
    /// the name is nothing a player has any use for.
    /// </summary>
    private void ApplySteamStatus(SteamClientStatus status)
    {
        var settled = status.IsReady && IsIdentityBound;
        if (settled)
        {
            SteamPersonaText.Text = status.PersonaName;
            SteamPersonaText.Visibility = Visibility.Visible;
            RetrySteamButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            SetSteamMessage(status.Message);
        }
        RefreshUi();
    }

    /// <summary>
    /// Puts the retry button in the footer with the reason as its tooltip. The
    /// reason is also in the log; the window only shows what can be acted on.
    /// </summary>
    private void SetSteamMessage(string message)
    {
        RetrySteamButton.ToolTip = string.IsNullOrWhiteSpace(message) ? null : message;
        RetrySteamButton.Visibility = Visibility.Visible;
        SteamPersonaText.Visibility = Visibility.Collapsed;
    }

    private bool IsIdentityBound => _identityService?.IsBound == true;

    private void ResolveAndPersistLocalIdentity()
    {
        if (_identityService is null || _settings is null || _settingsService is null) return;

        _settings.PlayerName = LocalIdentityService.NormalizeNickname(_settings.PlayerName, Environment.UserName);
        var resolvedContext = _identityService.ResolveContext(_settings);
        var updated = false;

        var identityId = resolvedContext.SteamId64.ToString();
        if (!string.Equals(_settings.LocalIdentityId, identityId, StringComparison.Ordinal))
        {
            _settings.LocalIdentityId = identityId;
            updated = true;
        }

        var identityName = resolvedContext.IdentityName ?? "";
        if (!string.Equals(_settings.LocalIdentityName, identityName, StringComparison.Ordinal))
        {
            _settings.LocalIdentityName = identityName;
            updated = true;
        }

        if (updated || !string.IsNullOrWhiteSpace(_settings.PlayerName))
        {
            _settingsService.Save(_settings);
        }
    }

    private void RefreshPlayerIdentityDisplay()
    {
        if (_isEditingPlayerName || PlayerNameTextBox.IsKeyboardFocusWithin)
        {
            return;
        }

        var displayName = RequireSettings().PlayerName;
        if (string.Equals(PlayerNameTextBox.Text, displayName, StringComparison.Ordinal))
        {
            return;
        }

        _suppressTextPersistence = true;
        try
        {
            PlayerNameTextBox.Text = displayName;
        }
        finally
        {
            _suppressTextPersistence = false;
        }
    }

    private string GetPlayerDisplayName()
    {
        var playerName = RequireSettings().PlayerName;
        return string.IsNullOrWhiteSpace(playerName)
            ? LocalIdentityService.NormalizeNickname(null, Environment.UserName)
            : playerName.Trim();
    }

    private void ApplyPlayerName()
    {
        if (_settings is null || _identityService is null || _settingsService is null)
        {
            return;
        }

        var normalized = LocalIdentityService.NormalizeNickname(PlayerNameTextBox.Text, Environment.UserName);

        _suppressTextPersistence = true;
        try
        {
            PlayerNameTextBox.Text = normalized;
            PlayerNameTextBox.CaretIndex = normalized.Length;
        }
        finally
        {
            _suppressTextPersistence = false;
        }

        _settings.PlayerName = normalized;
        PersistActivePlayerIdentity();
    }

    /// <summary>
    /// Writes this machine's player into the registry the game reads names
    /// from, if Steam has answered and there is an identity to write.
    /// </summary>
    private void RefreshIdentityRegistry()
    {
        if (_identityRegistry is null || _identityService is not { IsBound: true } || _settings is null) return;
        try
        {
            _identityRegistry.Prepare(_identityService.ResolveContext(_settings));
        }
        catch (IdentityUnavailableException)
        {
            // Steam went away between the check and the answer; the launch will
            // write the registry itself.
        }
    }

    private void PersistActivePlayerIdentity()
    {
        if (_settings is null || _identityService is null || _settingsService is null ||
            string.IsNullOrWhiteSpace(_settings.PlayerName))
        {
            return;
        }

        var identity = _identityService.ResolveContext(_settings);
        _settings.LocalIdentityId = identity.SteamId64.ToString();
        _settings.LocalIdentityName = identity.IdentityName;
        _settingsService.Save(_settings);
    }

    /// <summary>
    /// Who owns a world: the Minecraft UUID, not the Steam account. World
    /// metadata is read by every build of the launcher, old ones included.
    /// </summary>
    private (string id, string name) GetActiveLocalOwner()
    {
        var settings = RequireSettings();
        if (_identityService is not { IsBound: true }) return ("", Environment.UserName);
        var resolved = _identityService.ResolveContext(settings);
        return (resolved.MinecraftUuid, resolved.IdentityName);
    }

    private (string id, string name) ResolveActiveLocalIdentity()
    {
        var settings = RequireSettings();
        if (_identityService is not { IsBound: true })
        {
            return (
                string.IsNullOrWhiteSpace(settings.LocalIdentityId) ? string.Empty : settings.LocalIdentityId.Trim(),
                string.IsNullOrWhiteSpace(settings.LocalIdentityName)
                    ? Environment.UserName
                    : settings.LocalIdentityName.Trim()
            );
        }

        var resolved = _identityService.ResolveContext(settings);
        return (resolved.SteamId64.ToString(), resolved.IdentityName ?? "");
    }

    /// <summary>
    /// Tidies the field when it is left: the text is read back, held to what
    /// the machine allows, and written out again.
    /// </summary>
    /// <remarks>
    /// Leaving a field is not choosing a number. This used to say
    /// <c>chosenByPlayer: true</c>, which was survivable while there was one
    /// number for every pack and merely froze the launcher's suggestion; with a
    /// number per pack it meant that clicking near the box - or the window
    /// losing focus at all - stamped whatever was showing as this pack's answer
    /// for ever. All The Fabric 3 was pinned at four gigabytes that way, on a
    /// machine where the launcher would have offered five, and it ran out of
    /// them while generating a world. A number the player types is written down
    /// by <see cref="MemoryTextBox_TextChanged"/> on the keystroke that types
    /// it, and Enter says so again; neither needs this to say it a third time.
    /// </remarks>
    private void MemoryTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyMemoryText(chosenByPlayer: false);
    }

    private void MemoryTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(ch => !char.IsDigit(ch));
    }

    private void MemoryTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text))
        {
            e.CancelCommand();
            return;
        }

        var text = e.DataObject.GetData(DataFormats.Text) as string;
        if (string.IsNullOrEmpty(text) || text.Any(ch => !char.IsDigit(ch)))
        {
            e.CancelCommand();
        }
    }

    private void MemoryTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressMemoryTextChanged || _settings is null || _settingsService is null) return;

        var digitsOnly = new string(MemoryTextBox.Text.Where(char.IsDigit).ToArray());
        if (!string.Equals(MemoryTextBox.Text, digitsOnly, StringComparison.Ordinal))
        {
            SetMemoryText(digitsOnly);
            return;
        }

        if (string.IsNullOrWhiteSpace(digitsOnly))
        {
            return;
        }

        var allowedHeapGb = GetAllowedHeapGb();
        if (!int.TryParse(digitsOnly, out var memoryGb) || memoryGb > allowedHeapGb)
        {
            SetMemoryGb(allowedHeapGb, chosenByPlayer: true);
            return;
        }

        if (memoryGb >= MinHeapGb)
        {
            _settings.MaxHeapGb = memoryGb;
            // Typed by hand: from here this is what the pack in front of them
            // is worth, kept under that pack's name and put back in the field
            // every time they return to it. Written on the keystroke rather
            // than on the launch, because thinking better of playing is not
            // thinking better of the number.
            _settingsService.RememberMemoryForPack(_settings, memoryGb);
            _settingsService.Save(_settings);
        }
    }

    private void MemoryTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        ApplyMemoryText(chosenByPlayer: true);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void TransferSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshUi();
    }

    private async void BuildComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressBuildPersistence || _settings is null || _settingsService is null) return;
        if (BuildComboBox.SelectedItem is not ClientBuildViewModel build) return;

        _settings.ClientRelativePath = build.RelativePath;
        _settingsService.Save(_settings);
        RefreshPackMemory();
        InitializeRuntimeProgressUi();
        RefreshControlsPresetStatus();
        _ = RefreshLauncherDataAsync(build.RelativePath);
        await RefreshPackHashAsync();
        if (_startupComplete) await StartNetworkingAsync();
        SetState($"Build selected: {build.Name}");
        RefreshUi();
    }

    private void WorldComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_paths is not null && _settings is not null && _settingsService is not null &&
            WorldComboBox.SelectedItem is WorldViewModel world)
        {
            _settings.SelectedWorldRelativePath = Path.GetRelativePath(_paths.Worlds, world.Path);
            _settingsService.Save(_settings);
        }

        SetTransferProgressVisible(_transferBytesCurrent, _transferBytesTotal);
        RefreshUi();
    }

    private void ApplyTransferProgress(WorldTransferProgress progress)
    {
        var activeChanged = _transferActive != progress.IsActive;
        _transferActive = progress.IsActive;
        SetProgressActivity(TransferProgressBar, progress.IsActive);
        if (!progress.IsActive)
        {
            RememberHowLongThisTransferTook();
            _transferRate.Reset();
            _transferBytesCurrent = 0;
            _transferBytesTotal = 0;
            _lastTransferSpeedBytesPerSecond = 0;
            _transferStage = "";
            _transferRemaining = null;
            SetTransferProgressVisible(0, 0);
            if (activeChanged) RefreshUi();
            return;
        }

        var current = progress.Current;
        var total = progress.Total;
        _transferStage = progress.Stage;
        _lastTransferSpeedBytesPerSecond = _transferRate.Update(
            current,
            string.IsNullOrEmpty(progress.Stage) ? "world" : progress.Stage);
        _transferRun ??= new TransferRun(_transferPacing);
        _transferRemaining = _transferRun.Advance(progress.Stage, current, total);
        _transferBytesCurrent = Math.Max(0, current);
        _transferBytesTotal = total;
        SetTransferProgressVisible(_transferBytesCurrent, _transferBytesTotal);
        if (activeChanged) RefreshUi();
    }

    private void SetTransferProgressVisible(long current, long total)
    {
        if (total <= 0)
        {
            TransferProgressBar.Value = 0;
            // A phase whose byte total is not known yet still has to look alive.
            TransferProgressBar.IsIndeterminate = _transferActive;
            TransferProgressText.Text = !_transferActive
                ? "В ожидании мира"
                : TransferProgressLine.ComposeWaiting(_transferStage, _transferRemaining);
            if (!_transferActive)
            {
                _lastTransferSpeedBytesPerSecond = 0;
            }
            return;
        }

        var clampedTotal = Math.Max(1, total);
        var value = Math.Clamp(current, 0, clampedTotal);
        var percent = Math.Round(value * 100d / clampedTotal, 1);
        TransferProgressBar.IsIndeterminate = false;
        TransferProgressBar.Value = percent;
        TransferProgressText.Text = TransferProgressLine.Compose(
            _transferStage, value, clampedTotal, _lastTransferSpeedBytesPerSecond, _transferRemaining);
    }

    /// <summary>
    /// A handover that reached its last step is what the next estimate is built
    /// from. One that was cancelled or broke off says nothing about how long
    /// the whole thing takes, so it is dropped rather than averaged in.
    /// </summary>
    private void RememberHowLongThisTransferTook()
    {
        var run = _transferRun;
        _transferRun = null;
        if (run is null || !run.Completed || _transferPacingStore is null) return;

        _transferPacing = _transferPacing.Blend(run.Timings());
        _transferPacingStore.Save(_transferPacing);
    }

    private static string FormatBytes(long bytes) => TransferProgressLine.FormatBytes(bytes);

    // The number is the commit count, so it names the commit by itself - the
    // short hash it used to carry said the same thing twice.
    private static string BuildVersionText() => $"Версия {UpdateService.CurrentReleaseNumber}";

    /// <summary>
    /// What the update bar says when there is nothing to fetch. The number
    /// rides along: the corner that used to hold it is a button now, and a
    /// player asked what version they are on has to be able to read it off the
    /// one line that is already about versions.
    /// </summary>
    private static string LatestVersionText() =>
        $"Вы на последней версии ({UpdateService.CurrentReleaseNumber})";

    /// <summary>
    /// The right column shows one of its two faces: the assistant it opens on,
    /// or the version history. The footer button carries the name of the other
    /// one, because that is what pressing it brings.
    /// </summary>
    private void ShowSidePanel(bool news)
    {
        _sidePanelShowsNews = news;
        ChangelogPanel.Visibility = news ? Visibility.Visible : Visibility.Collapsed;
        ChatPanel.Visibility = news ? Visibility.Collapsed : Visibility.Visible;
        SidePanelTitle.Text = news ? "Что нового" : "AI-помощник";
        SidePanelToggleButton.Content = news ? "Чат" : "Новости";
    }

    private void SidePanelToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSidePanel(!_sidePanelShowsNews);
    }

    /// <summary>
    /// Fills "Что нового" from the history embedded in the executable and marks
    /// the version this build is. Newest first, so the current one is at the
    /// top; a missing or broken history shows one line instead of an empty box.
    /// </summary>
    private void LoadChangelog()
    {
        var entries = ChangelogService.Load(_logger);
        _changelogPager = new ChangelogPager(entries);
        _changelogShown.Clear();
        ChangelogList.ItemsSource = _changelogShown;
        ShowMoreChangelog();
        ChangelogUnavailableText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // The history only grows, so the panel takes it a page at a time and
        // asks for the next one when the reader reaches the end of this one.
        ChangelogScroll.ScrollChanged += ChangelogScroll_ScrollChanged;
    }

    private void ShowMoreChangelog()
    {
        if (_changelogPager is null) return;
        foreach (var entry in _changelogPager.Next())
        {
            _changelogShown.Add(new ChangelogEntryViewModel { Version = entry.Version, Text = entry.Text });
        }
    }

    private void ChangelogScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_changelogPager is null || !_changelogPager.HasMore) return;
        // "Near the end" rather than "at the end": the last entry should not
        // have to touch the bottom edge before the next page starts arriving.
        var remaining = e.ExtentHeight - e.VerticalOffset - e.ViewportHeight;
        if (remaining > e.ViewportHeight / 2) return;
        ShowMoreChangelog();
    }

    private void InitializeUpdateUi()
    {
        _updateRate.Reset();
        UpdateProgressBar.Value = 0;
        UpdateProgressBar.IsIndeterminate = false;
        SetProgressActivity(UpdateProgressBar, active: false);
        UpdateProgressText.Text = LatestVersionText();
        UpdateButton.IsEnabled = false;
    }

    private void InitializeRuntimeProgressUi()
    {
        _runtimeRate.Reset();
        _playProgressText = null;
        PlayProgressBar.Value = 0;
        PlayProgressBar.IsIndeterminate = false;
        PlayProgressBar.Visibility = Visibility.Collapsed;
        RefreshUi();
    }

    /// <summary>
    /// Preparation is shown inside the Play button: the fill is the progress and
    /// the caption is the stage. One control instead of a bar the player had to
    /// look away to find, and it is exactly where they just clicked.
    /// </summary>
    private void ApplyRuntimeProgress(RuntimePreparationProgress progress)
    {
        var busy = progress.Stage is RuntimePreparationStage.Checking or
            RuntimePreparationStage.SyncingPack or
            RuntimePreparationStage.Downloading or
            RuntimePreparationStage.InstallingJava or
            RuntimePreparationStage.InstallingLoader or
            RuntimePreparationStage.Verifying;
        PlayProgressBar.Visibility = busy || progress.Stage == RuntimePreparationStage.Ready
            ? Visibility.Visible
            : Visibility.Collapsed;
        var isByteStage = progress.Stage is RuntimePreparationStage.SyncingPack or
            RuntimePreparationStage.Downloading or
            RuntimePreparationStage.InstallingJava;
        // Keyed by what is being fetched, so the measured rate starts again
        // where the bytes do: the base game and the loader's files are two
        // downloads, and one's speed is not the other's.
        var runtimeSpeed = isByteStage && progress.TotalBytes > 0
            ? _runtimeRate.Update(progress.DownloadedBytes, $"runtime:{progress.Stage}:{progress.Message}")
            : 0;
        if (!isByteStage) _runtimeRate.Reset();
        _playProgressText = PlayButtonCaption.For(progress, runtimeSpeed);
        PlayProgressBar.IsIndeterminate = progress.Fraction is null && busy;
        if (progress.Fraction is not null)
        {
            PlayProgressBar.Value = Math.Clamp(progress.Fraction.Value * 100d, 0d, 100d);
        }
        else if (!PlayProgressBar.IsIndeterminate)
        {
            PlayProgressBar.Value = progress.Stage == RuntimePreparationStage.Ready ? 100d : 0d;
        }
        RefreshUi();
    }

    private async Task CheckForUpdatesAsync(CancellationToken token)
    {
        if (_updateService is null) return;

        PreparedUpdate? startupPrepared = null;
        try
        {
            startupPrepared = await Task.Run(_updateService.TryGetPreparedUpdate, token);
            token.ThrowIfCancellationRequested();
            if (startupPrepared is not null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _updateBusy = true;
                    _preparedUpdate = startupPrepared;
                    UpdateProgressBar.IsIndeterminate = false;
                    UpdateProgressBar.Value = 100;
                    SetProgressActivity(UpdateProgressBar, active: false);
                    UpdateProgressText.Text = "Обновление готово к установке";
                    RefreshUi();
                });
            }

            var result = startupPrepared is null
                ? await _updateService.CheckAsync(token)
                : await _updateService.CheckAsync(
                    token,
                    attempts: 1,
                    attemptTimeout: TimeSpan.FromSeconds(5));
            token.ThrowIfCancellationRequested();
            if (!result.IsUpdateAvailable)
            {
                if (startupPrepared is null)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _preparedUpdate = null;
                        UpdateProgressBar.IsIndeterminate = false;
                        UpdateProgressBar.Value = 0;
                        SetProgressActivity(UpdateProgressBar, active: false);
                        UpdateProgressText.Text = result.IsUnavailable
                            ? "Не удалось проверить обновления"
                            : LatestVersionText();
                    });
                }
            }
            else if (result.Manifest is not null &&
                     (startupPrepared is null || ShouldReplacePreparedUpdate(startupPrepared.Manifest, result.Manifest)))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _updateBusy = true;
                    _updateRate.Reset();
                    UpdateProgressBar.IsIndeterminate = false;
                    UpdateProgressBar.Value = 0;
                    SetProgressActivity(UpdateProgressBar, active: true);
                    UpdateProgressText.Text = "Скачивается обновление";
                    RefreshUi();
                });

                var progress = new Progress<UpdatePreparationProgress>(value =>
                {
                    UpdateProgressBar.IsIndeterminate = value.Fraction is null;
                    if (value.Fraction is not null)
                    {
                        UpdateProgressBar.Value = Math.Clamp(value.Fraction.Value * 100d, 0d, 100d);
                    }
                    if (value.Stage == UpdatePreparationStage.Downloading && value.TotalBytes > 0)
                    {
                        var speed = _updateRate.Update(value.DownloadedBytes, "update");
                        UpdateProgressText.Text =
                            $"Скачивается обновление: {FormatBytes(value.DownloadedBytes)} / {FormatBytes(value.TotalBytes)} ({FormatBytes((long)speed)}/с)";
                    }
                    else if (value.Stage == UpdatePreparationStage.ApplyingDelta)
                    {
                        _updateRate.Reset();
                        UpdateProgressText.Text = "Применение обновления";
                    }
                });
                var readyUpdate = await _updateService.DownloadUpdateAsync(result, progress, token);
                token.ThrowIfCancellationRequested();
                await Dispatcher.InvokeAsync(() =>
                {
                    _preparedUpdate = readyUpdate;
                    UpdateProgressBar.IsIndeterminate = false;
                    UpdateProgressBar.Value = 100;
                    SetProgressActivity(UpdateProgressBar, active: false);
                    UpdateProgressText.Text = "Обновление готово к установке";
                });
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Background update failed: {ex.Message}");
            if (startupPrepared is null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _preparedUpdate = null;
                    UpdateProgressBar.IsIndeterminate = false;
                    UpdateProgressBar.Value = 0;
                    SetProgressActivity(UpdateProgressBar, active: false);
                    UpdateProgressText.Text = LatestVersionText();
                });
            }
        }
        finally
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _updateBusy = false;
                    RefreshUi();
                });
            }
        }
    }

    private static bool ShouldReplacePreparedUpdate(UpdateManifest cached, UpdateManifest remote)
    {
        if (remote.ReleaseNumber < cached.ReleaseNumber) return false;
        return !string.Equals(remote.CommitSha, cached.CommitSha, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(remote.Sha256, cached.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || _preparedUpdate is null) return;

        _updateBusy = true;
        RefreshUi();
        try
        {
            var prepared = await Task.Run(RequireUpdateService().TryGetPreparedUpdate, _lifetimeCts.Token);
            if (prepared is null)
            {
                _preparedUpdate = null;
                UpdateProgressText.Text = LatestVersionText();
                UpdateProgressBar.Value = 0;
                SetProgressActivity(UpdateProgressBar, active: false);
                return;
            }

            _preparedUpdate = prepared;
            UpdateProgressText.Text = "Обновление готово к установке";
            UpdateProgressBar.Value = 100;
            SetProgressActivity(UpdateProgressBar, active: false);
            _restartAfterUpdateOnExit = true;
            Close();
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RequireLogger().Warn($"Update failed: {ex.Message}");
            MessageBox.Show(ex.Message, "Minecraft", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updateBusy = false;
            RefreshUi();
        }
    }

    /// <summary>
    /// Reads the field back into the settings. <paramref name="chosenByPlayer"/>
    /// is false where the launcher is only re-applying what it put there
    /// itself - pressing Play, above all - because a number the player never
    /// touched goes on following the pack it is for.
    /// </summary>
    private void ApplyMemoryText(bool chosenByPlayer)
    {
        if (int.TryParse(MemoryTextBox.Text.Trim(), out var memoryGb))
        {
            SetMemoryGb(memoryGb, chosenByPlayer);
            return;
        }

        SetMemoryGb(MinHeapGb, chosenByPlayer);
    }

    private void SetMemoryGb(int memoryGb, bool chosenByPlayer)
    {
        var settings = RequireSettings();
        var clamped = ClampHeapGb(memoryGb);
        settings.MaxHeapGb = clamped;
        var service = RequireSettingsService();
        if (chosenByPlayer) service.RememberMemoryForPack(settings, clamped);
        service.Save(settings);
        SetMemoryText(clamped.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Weighs the pack that is selected - its mods, their bytes, the texture it
    /// ships - and puts the field back to the number that belongs to it: the one
    /// the player last set here, or what the pack asks for where they never set
    /// one. Vanilla on an old version and a pack heavier than Limitless 8 are
    /// both packs here, and they are why the number is not shared between them.
    /// </summary>
    private void RefreshPackMemory()
    {
        if (_settings is null || _settingsService is null) return;

        // Weighing a pack walks every jar in it - nine hundred of them on the
        // largest build here - and that is a second of disk on a cold cache. It
        // used to happen on the click that chose the build, which froze the
        // window mid-press: nothing moved, and the number it was working out is
        // the one thing on screen that could have said why. So the estimate says
        // it is thinking, and the walk happens off this thread.
        var wanted = _settings.ClientRelativePath;
        var generation = ++_packMemoryGeneration;
        StartMemoryEstimateWait();
        var service = _settingsService;
        var settings = _settings;
        var sync = _packSync;
        _ = Task.Run(async () =>
        {
            var measured = service.MeasurePack(wanted);
            // A build that is offered but not downloaded has no folder to walk,
            // and the rules would otherwise fall back to giving it two thirds of
            // the machine - which put a bigger number under a pack built for a
            // laptop than under the 880-mod one beside it. Its manifest names
            // every file with its size, so it can be weighed without fetching
            // any of them.
            if (!measured.IsKnown && sync is not null)
            {
                service.UsePackMemory(
                    await sync.WeighFromSourceAsync(wanted, CancellationToken.None).ConfigureAwait(false));
            }
        }).ContinueWith(
            _ =>
            {
                // A player who clicks through three builds gets three walks, and
                // only the last one is about the build in front of them.
                if (generation != _packMemoryGeneration) return;
                StopMemoryEstimateWait();
                service.ApplyPackMemory(settings);
                RefreshMemoryText();
                RefreshSkinHint();
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// While the pack is being weighed the estimate is a moving ellipsis rather
    /// than a stale number: the last pack's answer standing under a new build's
    /// name is worse than no answer at all.
    /// </summary>
    private void StartMemoryEstimateWait()
    {
        _memoryEstimateWaitStep = 0;
        MemoryEstimateText.Text = "от .";
        MemoryEstimateText.ToolTip = "Лаунчер взвешивает сборку, чтобы прикинуть память.";
        _memoryEstimateWait ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _memoryEstimateWaitStep = (_memoryEstimateWaitStep + 1) % 3;
                MemoryEstimateText.Text = "от " + new string('.', _memoryEstimateWaitStep + 1);
            },
            Dispatcher);
        _memoryEstimateWait.Start();
    }

    private void StopMemoryEstimateWait()
    {
        _memoryEstimateWait?.Stop();
    }

    /// <summary>
    /// What the Skin button says on hover, the way the memory field does: the
    /// rule for the file, and - where the pack is old enough for it to matter -
    /// what this pack's Minecraft will make of the skin that is chosen.
    /// </summary>
    private void RefreshSkinHint()
    {
        SkinButton.ToolTip = SkinCompatibility.Describe(
            _settingsService?.PackMemory.MinecraftVersion,
            _settings?.SkinPath);
    }

    private void RefreshMemoryText(bool saveIfChanged = false)
    {
        var settings = RequireSettings();
        var clamped = ClampHeapGb(settings.MaxHeapGb);
        if (settings.MaxHeapGb != clamped)
        {
            settings.MaxHeapGb = clamped;
            if (saveIfChanged)
            {
                RequireSettingsService().Save(settings);
            }
        }

        SetMemoryText(clamped.ToString(CultureInfo.InvariantCulture));
    }

    private static int ClampHeapGb(int value)
    {
        return MemorySizingService.ClampHeapGb(value);
    }

    /// <summary>
    /// The largest heap this machine offers, which is the machine less the
    /// operating system and nothing else. It does not depend on the build, on
    /// the card or on anything a session measured, so the number a player may
    /// type is the same tomorrow as it was today.
    /// </summary>
    private static int GetAllowedHeapGb()
    {
        return MemorySizingService.GetAllowedHeapGb();
    }

    private PackMemoryProfile PackForMemory() => _settingsService?.PackMemory ?? PackMemoryProfile.Unknown;

    private MeasuredMemoryProfile MeasuredForMemory() =>
        _settingsService?.MeasuredMemory ?? MeasuredMemoryProfile.Unknown;

    private void SetMemoryText(string text)
    {
        _suppressMemoryTextChanged = true;
        try
        {
            MemoryTextBox.Text = text;
            MemoryTextBox.CaretIndex = MemoryTextBox.Text.Length;
            DescribeMemorySplit(text);
            RefreshMemoryEstimate();
        }
        finally
        {
            _suppressMemoryTextChanged = false;
        }
    }

    /// <summary>
    /// The number the launcher would put in the field for the pack that is
    /// selected: the heap that pack asks for, held inside what this machine
    /// offers. It stands beside the field rather than in it, because a number
    /// somebody typed is theirs and this is only what the launcher makes of the
    /// pack's weight.
    /// </summary>
    private void RefreshMemoryEstimate()
    {
        var estimateGb = MemorySizingService.GetRecommendedMemoryGb(
            PackForMemory(), VideoMemoryProfile.Measure(), MeasuredForMemory());
        MemoryEstimateText.Text = $"от {estimateGb} ГБ";
        MemoryEstimateText.ToolTip =
            $"Оценка: столько памяти лаунчер советует этой сборке - {estimateGb} ГБ кучи. " +
            "Число в поле рядом ваше и остаётся вашим.";
    }

    /// <summary>
    /// The number is the Java heap and goes to <c>-Xmx</c> untouched, so the
    /// field says two things: that the game will report this very number, and
    /// how much the game takes on top of it - the class data of the mods, the
    /// compiled code and the buffers Sodium hands the graphics driver, which is
    /// what the ceiling of this field is short by. That room is the selected
    /// pack's, so the same machine offers vanilla a larger heap than it offers
    /// nine hundred mods.
    /// </summary>
    private void DescribeMemorySplit(string text)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var heapGb))
        {
            MemoryTextBox.ToolTip = null;
            return;
        }
        var pack = PackForMemory();
        var video = VideoMemoryProfile.Measure();
        var measured = MeasuredForMemory();
        var reserveGb = MemorySizingService.GetNativeReserveGb(pack, video, measured);
        var allowedHeapGb = MemorySizingService.GetAllowedHeapGb();
        var tooltip =
            $"Столько памяти получит куча Java - ровно {heapGb} ГБ, это же число покажет игра по F3. " +
            $"Сверх кучи игра займёт ещё около {reserveGb} ГБ: классы модов, скомпилированный код " +
            $"и буферы Sodium, итого около {heapGb + reserveGb} ГБ. " +
            $"Больше {allowedHeapGb} ГБ здесь не поставить - остальное оставлено системе.";
        // Where there is a measurement it is the whole answer, card included,
        // so the card is not named twice: the driver's copy is already inside
        // the number the game was seen holding.
        var videoSpillGb = MemorySizingService.GetVideoSpillGb(pack, video);
        if (measured.IsKnown)
        {
            tooltip += " Запас сверх кучи здесь не оценка, а замер: игра занимала около " +
                $"{measured.AtMostMb} МБ сверх кучи за последние сессии на этой машине.";
        }
        else if (videoSpillGb > 0)
        {
            tooltip += $" У видеокарты {video.DedicatedGb} ГБ, сборке этого мало, и около " +
                $"{videoSpillGb} ГБ текстур драйвер держит в оперативной - потолок здесь ниже на столько же.";
        }
        MemoryTextBox.ToolTip = tooltip;
    }

    private async Task RunUiActionAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        RefreshUi();
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RequireLogger().Warn(ex.Message);
            MessageBox.Show(ex.Message, "Minecraft", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            RefreshUi();
        }
    }

    private void SetState(string state)
    {
        _state = state;
    }

    /// <summary>
    /// A bar at rest shows its track and nothing else; a bar at work fills. Both
    /// brushes are tokens of the design system.
    /// </summary>
    private void SetProgressActivity(ProgressBar progressBar, bool active)
    {
        progressBar.Foreground = (Brush)FindResource(active ? "Brush.ProgressFill" : "Brush.ProgressTrack");
    }

    private void RefreshUi()
    {
        if (_settings is null) return;

        RefreshPlayerIdentityDisplay();
        // Everything that touches a world needs to know who the player is, and
        // that answer comes from Steam; without it only the settings stay live.
        var interactiveEnabled = !_busy && IsIdentityBound;
        var configurationEnabled = interactiveEnabled &&
                                   !_transferActive &&
                                   !_minecraftRunning &&
                                   !_minecraftPreparing;
        var hasBuild = BuildComboBox.SelectedItem is ClientBuildViewModel;
        var selectedRecipient = OnlinePlayerComboBox.SelectedItem as PeerViewModel;
        // The name and the memory size are read when the game starts, so they
        // stay editable while the pack downloads - that wait is long, and there
        // is nothing in it these two could spoil. Once the game holds them, they
        // are locked.
        var settingsEnabled = !_busy && !_minecraftRunning;
        PlayerNameTextBox.IsEnabled = settingsEnabled;
        PlayerNameTextBox.IsReadOnly = !_isEditingPlayerName || !PlayerNameTextBox.IsEnabled;
        ChangePlayerNameButton.IsEnabled = settingsEnabled;
        ChangePlayerNameButton.Content = _isEditingPlayerName ? "Сохранить" : "Изменить";
        _playerNameMarquee?.SetAllowed(!_isEditingPlayerName);
        BuildComboBox.IsEnabled = configurationEnabled && _builds.Count > 1;
        BuildPlaceholderText.Visibility = _builds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Three states of one control: idle, preparing (the fill and the stage
        // are inside it), and a game that is already up. While preparing it stays
        // enabled so its text keeps its colour, and simply does not take clicks -
        // PlayButton_Click refuses them anyway.
        if (_minecraftRunning)
        {
            PlayButtonText.Text = "Игра запущена";
            PlayProgressBar.Visibility = Visibility.Collapsed;
            PlayButton.IsHitTestVisible = true;
            PlayButton.IsEnabled = false;
        }
        else if (_minecraftPreparing)
        {
            PlayButtonText.Text = _playProgressText ?? "Проверка сборки";
            PlayButton.IsHitTestVisible = false;
            PlayButton.IsEnabled = true;
        }
        else
        {
            PlayButtonText.Text = "Играть";
            PlayProgressBar.Visibility = Visibility.Collapsed;
            PlayButton.IsHitTestVisible = true;
            PlayButton.IsEnabled = configurationEnabled && hasBuild && !_isEditingPlayerName;
        }
        // Preparing a pack is a long wait and the skin is only read when the
        // client itself starts, so there is room to change it right up to then.
        SkinButton.IsEnabled = !_minecraftRunning;
        // The layout is read when the game starts, like the skin, so the button
        // stays live while the pack downloads. It goes quiet when there is
        // nothing to do: no preset for this build, the preset already in place,
        // or a game that has the options file open.
        var preset = _controlsPresetStatus;
        ControlsPresetButton.IsEnabled = preset.HasPreset && !preset.IsApplied && !_minecraftRunning;
        // A build with no layout of its own says nothing at all: the button is
        // simply dead, the way a control is for a feature a build does not
        // have. The other two quiet states are worth explaining - the game is
        // holding the file, or the layout is already in place - because there
        // the button would otherwise look broken.
        // The delete button belongs to a build that is actually on the disk:
        // a name the list only offers has nothing to remove, and a game that is
        // running is holding the very files this would delete.
        var installed = BuildComboBox.SelectedItem is ClientBuildViewModel selected && selected.IsInstalled;
        DeleteBuildButton.IsEnabled = installed && !_minecraftRunning;
        DeleteBuildButton.ToolTip = !installed
            ? "Эта сборка ещё не скачана"
            : _minecraftRunning
                ? "Игра запущена - файлы сборки сейчас у неё"
                : "Удалить сборку с компьютера";
        ControlsPresetButton.ToolTip = !preset.HasPreset
            ? null
            : _minecraftRunning
                ? "Игра запущена - настройки управления сейчас у неё"
                : preset.IsApplied
                    ? "Пресет применён - настройки управления совпадают со сборкой"
                    // A lit button says what it found: the first line of the
                    // preset the game does not have. Otherwise the only answer
                    // to "but I applied it already" is six hundred lines by hand.
                    : $"Заменить настройки управления раскладкой сборки без конфликтов. Отличается: {preset.FirstDifference}";
        // A list with nothing to choose between is not a control, it is a
        // label that opens. One world is the answer already; the drop-down
        // stays readable and stays selected, it just stops pretending there is
        // a decision here - the same way the player list does with nobody in it.
        //
        // Only a transfer already running closes them. Both lists define that
        // transfer, and leaving them live let a player change the answer to a
        // question already being acted on while it carried on with the old one.
        // A running game is a different thing entirely: it stops a world being
        // sent, which the button and the bar say for themselves, but it is no
        // reason to stop reading the lists. Looking up who is online, and in
        // what, is most of what they are for - and doing it while playing is
        // when a player most wants to.
        var listsEnabled = interactiveEnabled && !_transferActive;
        WorldComboBox.IsEnabled = listsEnabled && _worlds.Count > 1;
        OnlinePlayerComboBox.IsEnabled = listsEnabled && _peers.Count > 0;
        WorldPlaceholderText.Visibility = _worlds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OnlinePlayerPlaceholderText.Visibility = _peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var canTransfer = interactiveEnabled && !_transferActive && !_minecraftRunning && !_minecraftPreparing &&
                          WorldComboBox.SelectedItem is WorldViewModel &&
                          selectedRecipient is not null &&
                          !selectedRecipient.IsMinecraftRunning &&
                          !selectedRecipient.IsMinecraftPreparing;
        TransferButton.IsEnabled = canTransfer;
        // The bar reads "В ожидании мира" whether or not anything could ever
        // arrive. It waits for nothing while the game holds the world, while no
        // world is chosen, or while there is nobody to send it to, so it goes
        // quiet with the button and speaks only when a transfer is possible or
        // already running.
        TransferProgressArea.IsEnabled = _transferActive || canTransfer;
        MemoryTextBox.IsEnabled = settingsEnabled;
        // Updating is the one thing in this window that does not need to know who
        // the player is: it replaces this program and touches nothing a world or a
        // friend list depends on. Steam being down is often the very reason a
        // player reaches for a newer build, so the button follows the update alone.
        UpdateButton.IsEnabled = !_busy && !_updateBusy && _preparedUpdate is not null;

        RefreshDiagnosticsPanel();
        RefreshTransferStatus();
    }

    private void RefreshTransferStatus()
    {
        TransferStatusText.Text = string.Empty;
    }

    private void AppendLog(string line)
    {
        LogTextBox.AppendText(line + Environment.NewLine);
        LogTextBox.ScrollToEnd();
    }

    private AppPaths RequirePaths() => _paths ?? throw new InvalidOperationException("App paths are not initialized.");
    private AppSettings RequireSettings() => _settings ?? throw new InvalidOperationException("Settings are not initialized.");
    private SettingsService RequireSettingsService() => _settingsService ?? throw new InvalidOperationException("Settings service is not initialized.");
    private Logger RequireLogger() => _logger ?? throw new InvalidOperationException("Logger is not initialized.");
    private PackHashService RequirePackHash() => _packHash ?? throw new InvalidOperationException("Pack hash service is not initialized.");

    private PortablePackSyncService RequirePackSync() => _packSync ?? throw new InvalidOperationException("Pack sync service is not initialized.");
    private WorldMetadataService RequireWorldMetadata() => _worldMetadata ?? throw new InvalidOperationException("World metadata service is not initialized.");
    private SteamIdentityService RequireIdentityService() => _identityService ?? throw new InvalidOperationException("Identity service is not initialized.");
    private WorldPlayerProfileService RequireWorldPlayerProfiles() => _worldPlayerProfiles ?? throw new InvalidOperationException("World player profile service is not initialized.");
    private MinecraftProcessService RequireMinecraft() => _minecraft ?? throw new InvalidOperationException("Minecraft service is not initialized.");
    private WorldTransferService RequireTransfer() => _transfer ?? throw new InvalidOperationException("World transfer service is not initialized.");
    private WaypointSyncService RequireWaypointSync() => _waypointSync ?? throw new InvalidOperationException("Waypoint sync service is not initialized.");
    private SkinService RequireSkinService() => _skinService ?? throw new InvalidOperationException("Skin service is not initialized.");

    private UpdateService RequireUpdateService() => _updateService ?? throw new InvalidOperationException("Update service is not initialized.");
}
