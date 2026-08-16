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
    private const int MinMemoryGb = MemorySizingService.MinMemoryGb;
    private static readonly TimeSpan PeerTtl = TimeSpan.FromSeconds(35);
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
    private WorldMetadataService? _worldMetadata;
    private WorldPlayerProfileService? _worldPlayerProfiles;
    private SteamClientService? _steamClient;
    private SteamIdentityService? _identityService;
    private PortableIdentityAdapterService? _identityAdapter;
    private PackInstanceService? _packInstances;
    private PackRuntimeService? _packRuntimes;
    private WaypointSyncService? _waypointSync;
    private SkinService? _skinService;

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
    private string _localPackHash = "";
    private string _state = "Starting";
    private bool _busy;
    private bool _suppressTextPersistence;
    private bool _suppressBuildPersistence;
    private bool _suppressMemoryTextChanged;
    private bool _bugReportSending;
    private string _bugReportStatus = "Отчёт отправляется другу через Steam.";
    private long _transferBytesCurrent;
    private long _transferBytesTotal;
    private double _lastTransferSpeedBytesPerSecond;
    private string _transferStage = "";
    private bool _transferActive;
    private bool _updateBusy;
    private bool _isEditingPlayerName;
    private bool _startupComplete;
    private bool _minecraftRunning;
    private bool _minecraftPreparing;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private bool _restartAfterUpdateOnExit;
    private PreparedUpdate? _preparedUpdate;
    private readonly WindowPlacementService _windowPlacement;

    public MainWindow()
    {
        InitializeComponent();
        _windowPlacement = new WindowPlacementService(new AppPaths(AppPaths.ResolveApplicationRoot()));
        _windowPlacement.Apply(this);
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
            RefreshUi();
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            VersionTextBlock.Text = BuildVersionText();
            _paths = new AppPaths(AppPaths.ResolveApplicationRoot());
            _paths.Ensure();
            LogCleanupService.RunCleanup(_paths);
            _logger = new Logger(_paths.LogFile);
            DeprecatedFileCleanupService.Run(_paths, _logger);
            _settingsService = new SettingsService(_paths, _logger);
            _settings = _settingsService.Load();
            _logger.LineWritten += line => PostToUi(() => AppendLog(line));
            _packHash = new PackHashService(_paths);
            _packSync = new PortablePackSyncService(_paths, _logger);
            _worldMetadata = new WorldMetadataService();
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
            _peerDirectory = new SteamPeerDirectory(_steamClient, _logger);
            _peerDirectory.PeersChanged += (_, peers) => PostToUi(() => ApplyPeers(peers));
            _worldPlayerProfiles = new WorldPlayerProfileService(_paths, _logger);
            _packInstances = new PackInstanceService(_paths, _logger);
            _packRuntimes = new PackRuntimeService(_paths, _logger);
            _waypointSync = new WaypointSyncService(_paths, _logger, _worldMetadata, _peerTransport);
            _skinService = new SkinService(_paths, _logger, _peerTransport);
            await _skinService.StartAsync(_lifetimeCts.Token);
            _minecraft = new MinecraftProcessService(_paths, _logger, _identityService, _identityAdapter, _worldPlayerProfiles, _packInstances, _packRuntimes, _waypointSync, _skinService);
            _minecraft.ClientRunningChanged += OnMinecraftClientRunningChanged;
            _minecraft.ClientPreparingChanged += OnMinecraftClientPreparingChanged;
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
            _bugReports.ReportReceived += (_, directory) => PostToUi(() =>
            {
                SetBugReportStatus($"Получен отчёт: {Path.GetFileName(directory)}");
                RefreshDiagnosticsPanel();
            });
            _bugReports.PruneStoredReports();
            _peerRouter.Register(_bugReports);
            _peerRouter.Register(_waypointSync);
            _peerRouter.Register(_skinService);
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
            RefreshMemoryText(saveIfChanged: true);
            RefreshWorlds();
            InitializeUpdateUi();
            InitializeRuntimeProgressUi();
            _ = CheckForUpdatesAsync(_lifetimeCts.Token);
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

        // The Infinity pack has a built-in sync source, so it is offered even
        // before it is installed; pressing Play downloads it.
        if (!builds.Any(build => string.Equals(
                build.RelativePath,
                PortablePackSyncService.DefaultPackRelativePath,
                StringComparison.OrdinalIgnoreCase)))
        {
            builds.Add(new ClientBuildViewModel
            {
                Name = "Infinity (не установлена)",
                RelativePath = PortablePackSyncService.DefaultPackRelativePath,
                FullPath = _paths.CombineUnderPacks(PortablePackSyncService.DefaultPackRelativePath),
                IsInstalled = false
            });
        }

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

        if (selectedBuild is not null &&
            !string.Equals(_settings.ClientRelativePath, selectedBuild.RelativePath, StringComparison.OrdinalIgnoreCase))
        {
            _settings.ClientRelativePath = selectedBuild.RelativePath;
            _settingsService.Save(_settings);
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
        var worlds = Directory.EnumerateDirectories(_paths.Worlds)
            .Where(WorldTransferService.IsMinecraftWorldDirectory)
            .Where(path => !Path.GetFileName(path).Contains(".backup-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
            .Select(path => CreateWorldViewModel(path, metadataContext))
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
                DiagnosticLogTargetComboBox.SelectedItem =
                    targets.FirstOrDefault(option => option == selected) ??
                    targets.FirstOrDefault(option => !option.IsNobody) ??
                    targets.FirstOrDefault();
            }
        }

        var recipient = DiagnosticLogTargetComboBox.SelectedItem as DiagnosticLogTargetOption;
        SendBugReportButton.IsEnabled =
            !_bugReportSending && IsIdentityBound && recipient is { IsNobody: false };
        DiagnosticLogStatusText.Text = _bugReportStatus;
        OpenSupportLogsButton.IsEnabled = HasReceivedBugReports();
    }

    private bool HasReceivedBugReports()
    {
        try
        {
            var reports = _bugReports?.ReportsDirectory;
            return reports is not null && Directory.Exists(reports) &&
                   Directory.EnumerateDirectories(reports).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Packs the last of this player's logs, with whatever they typed, and
    /// sends it to the friend they picked. Nothing streams: the report is a
    /// snapshot of the moment they noticed something was wrong.
    /// </summary>
    private void OpenSupportLogsButton_Click(object sender, RoutedEventArgs e) =>
        OpenSupportLogsDirectory();

    private async void SendBugReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_bugReports is null || _bugReportSending) return;
        if (DiagnosticLogTargetComboBox.SelectedItem is not DiagnosticLogTargetOption recipient ||
            recipient.IsNobody)
        {
            SetBugReportStatus("Выберите, кому отправить отчёт.");
            return;
        }

        _bugReportSending = true;
        SetBugReportStatus($"Отправка отчёта игроку {recipient.DisplayName}…");
        try
        {
            var message = BugReportMessageTextBox.Text;
            var manifest = await _bugReports
                .SendAsync(recipient.SteamId, message, _lifetimeCts.Token)
                .ConfigureAwait(true);
            BugReportMessageTextBox.Clear();
            SetBugReportStatus(
                $"Отчёт отправлен игроку {recipient.DisplayName} " +
                $"({manifest.ArchiveBytes / 1024} КиБ).");
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

    private void SetBugReportStatus(string message)
    {
        _bugReportStatus = message;
        DiagnosticLogStatusText.Text = message;
    }

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
        var cutoff = DateTimeOffset.Now - PeerTtl;
        var result = new List<DiagnosticLogTargetOption> { DiagnosticLogTargetOption.Nobody };
        foreach (var peer in _peers
                     // A launcher that cannot read the report is not a place to
                     // send one; the list already says why it is missing.
                     .Where(peer => peer.LastSeen >= cutoff && peer.SupportsDiagnosticLogs)
                     .OrderBy(peer => peer.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            result.Add(new DiagnosticLogTargetOption(peer.SteamId, peer.DisplayName));
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
        var metadata = RequireWorldMetadata().EnsureMetadata(path, metadataContext);
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
            BuildName = buildName
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
        _peerDirectory.PublishLocalPresence(new SteamPeerPresence
        {
            SteamId = localId,
            PersonaName = _steamClient.Status.PersonaName,
            ProtocolVersion = SteamPresenceCodec.ProtocolVersion,
            PlayerName = identity.name,
            MinecraftUuid = identityContext.MinecraftUuid,
            PackHash = _localPackHash,
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
        });
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
        }

        var current = peers.Select(presence => presence.SteamId).ToHashSet();
        for (var index = _peers.Count - 1; index >= 0; index--)
        {
            if (!current.Contains(_peers[index].SteamId)) _peers.RemoveAt(index);
        }

        OnlinePlayerComboBox.SelectedItem =
            FindMatchingPeer(_peers, selectedPeerId) ?? _peers.FirstOrDefault();
        RefreshDiagnosticsPanel();
        RefreshUi();
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
            if (!isRunning)
            {
                RefreshWorlds();
            }
            RefreshUi();
        });
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
        RefreshUi();
        try
        {
            if (RequireTransfer().IsOperationActive)
            {
                throw new InvalidOperationException("Wait for the world transfer to finish before starting Minecraft.");
            }
            ApplyPlayerName();
            ApplyMemoryText();
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
            }

            await RefreshPackHashAsync();
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
            RequireLogger().Warn(ex.Message);
            MessageBox.Show(ex.Message, "Minecraft", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            if (!LocalIdentityService.TryNormalizeNickname(candidate, out var normalized, out var error))
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

    private void ApplySteamStatus(SteamClientStatus status)
    {
        SetSteamMessage(status.Message);
        RetrySteamButton.Visibility = status.IsReady && IsIdentityBound
            ? Visibility.Collapsed
            : Visibility.Visible;
        RefreshUi();
    }

    private void SetSteamMessage(string message)
    {
        SteamStatusText.Text = message;
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

    private void MemoryTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyMemoryText();
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

        var maxMemoryGb = GetAllowedMaxMemoryGb();
        if (!int.TryParse(digitsOnly, out var memoryGb) || memoryGb > maxMemoryGb)
        {
            SetMemoryGb(maxMemoryGb);
            return;
        }

        if (memoryGb >= MinMemoryGb)
        {
            _settings.MaxMemoryGb = memoryGb;
            _settingsService.Save(_settings);
        }
    }

    private void MemoryTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        ApplyMemoryText();
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
        InitializeRuntimeProgressUi();
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
            _transferRate.Reset();
            _transferBytesCurrent = 0;
            _transferBytesTotal = 0;
            _lastTransferSpeedBytesPerSecond = 0;
            _transferStage = "";
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
                : string.IsNullOrEmpty(_transferStage) ? "Передача..." : $"{_transferStage}...";
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
        var progressText = $"{FormatBytes(value)} / {FormatBytes(clampedTotal)} ({FormatBytes((long)_lastTransferSpeedBytesPerSecond)}/с)";
        TransferProgressText.Text = string.IsNullOrEmpty(_transferStage)
            ? progressText
            : $"{_transferStage}: {progressText}";
    }

    private static string FormatBytes(long bytes)
    {
        const long kb = 1024;
        const long mb = kb * 1024;
        const long gb = mb * 1024;

        if (bytes >= gb) return $"{bytes / (double)gb:0.##} ГБ";
        if (bytes >= mb) return $"{bytes / (double)mb:0.##} МБ";
        if (bytes >= kb) return $"{bytes / (double)kb:0.##} КБ";
        return $"{bytes} Б";
    }

    private static string BuildVersionText()
    {
        return $"Версия {UpdateService.CurrentReleaseNumber}";
    }

    private void InitializeUpdateUi()
    {
        _updateRate.Reset();
        UpdateProgressBar.Value = 0;
        UpdateProgressBar.IsIndeterminate = false;
        SetProgressActivity(UpdateProgressBar, active: false);
        UpdateProgressText.Text = "Вы на последней версии";
        UpdateButton.IsEnabled = false;
    }

    private void InitializeRuntimeProgressUi()
    {
        _runtimeRate.Reset();
        RuntimeProgressBar.Value = 0;
        RuntimeProgressBar.IsIndeterminate = false;
        SetProgressActivity(RuntimeProgressBar, active: false);
        RuntimeProgressText.Text = "В ожидании игры";
    }

    private void ApplyRuntimeProgress(RuntimePreparationProgress progress)
    {
        SetProgressActivity(
            RuntimeProgressBar,
            progress.Stage is RuntimePreparationStage.Checking or
                RuntimePreparationStage.SyncingPack or
                RuntimePreparationStage.Downloading or
                RuntimePreparationStage.InstallingJava or
                RuntimePreparationStage.InstallingLoader or
                RuntimePreparationStage.Verifying);
        var phase = progress.PhaseCount > 1 &&
                    progress.PhaseIndex > 0 &&
                    progress.PhaseIndex <= progress.PhaseCount
            ? $" {progress.PhaseIndex}/{progress.PhaseCount}"
            : string.Empty;
        var isByteStage = progress.Stage is RuntimePreparationStage.SyncingPack or
            RuntimePreparationStage.Downloading or
            RuntimePreparationStage.InstallingJava;
        var runtimeSpeed = isByteStage && progress.TotalBytes > 0
            ? _runtimeRate.Update(progress.DownloadedBytes, $"runtime:{progress.Stage}:{progress.PhaseIndex}/{progress.PhaseCount}")
            : 0;
        if (!isByteStage) _runtimeRate.Reset();
        RuntimeProgressText.Text = progress.Stage switch
        {
            RuntimePreparationStage.SyncingPack when progress.TotalBytes > 0 =>
                $"Обновление сборки: {FormatBytes(progress.DownloadedBytes)} / {FormatBytes(progress.TotalBytes)} ({FormatBytes((long)runtimeSpeed)}/с)",
            RuntimePreparationStage.SyncingPack => "Проверка сборки",
            RuntimePreparationStage.Downloading when progress.TotalBytes > 0 =>
                $"Скачивание файлов{phase}: {FormatBytes(progress.DownloadedBytes)} / {FormatBytes(progress.TotalBytes)} ({FormatBytes((long)runtimeSpeed)}/с)",
            RuntimePreparationStage.Downloading => $"Скачивание файлов{phase}",
            RuntimePreparationStage.InstallingJava when progress.TotalBytes > 0 =>
                $"{progress.Message}: {FormatBytes(progress.DownloadedBytes)} / {FormatBytes(progress.TotalBytes)} ({FormatBytes((long)runtimeSpeed)}/с)",
            RuntimePreparationStage.InstallingLoader => progress.Message + phase,
            _ => progress.Message
        };
        RuntimeProgressBar.IsIndeterminate = progress.Fraction is null &&
                                             progress.Stage is RuntimePreparationStage.Checking or
                                                 RuntimePreparationStage.SyncingPack or
                                                 RuntimePreparationStage.Downloading or
                                                 RuntimePreparationStage.InstallingJava or
                                                 RuntimePreparationStage.InstallingLoader or
                                                 RuntimePreparationStage.Verifying;
        if (progress.Fraction is not null)
        {
            RuntimeProgressBar.Value = Math.Clamp(progress.Fraction.Value * 100d, 0d, 100d);
        }
        else if (!RuntimeProgressBar.IsIndeterminate)
        {
            RuntimeProgressBar.Value = progress.Stage == RuntimePreparationStage.Ready ? 100d : 0d;
        }
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
                            : "Вы на последней версии";
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
                    UpdateProgressText.Text = "Вы на последней версии";
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
                UpdateProgressText.Text = "Вы на последней версии";
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

    private void ApplyMemoryText()
    {
        if (int.TryParse(MemoryTextBox.Text.Trim(), out var memoryGb))
        {
            SetMemoryGb(memoryGb);
            return;
        }

        SetMemoryGb(MinMemoryGb);
    }

    private void SetMemoryGb(int memoryGb)
    {
        var settings = RequireSettings();
        var clamped = ClampMemoryGb(memoryGb);
        settings.MaxMemoryGb = clamped;
        RequireSettingsService().Save(settings);
        SetMemoryText(clamped.ToString(CultureInfo.InvariantCulture));
    }

    private void RefreshMemoryText(bool saveIfChanged = false)
    {
        var settings = RequireSettings();
        var clamped = ClampMemoryGb(settings.MaxMemoryGb);
        if (settings.MaxMemoryGb != clamped)
        {
            settings.MaxMemoryGb = clamped;
            if (saveIfChanged)
            {
                RequireSettingsService().Save(settings);
            }
        }

        SetMemoryText(clamped.ToString(CultureInfo.InvariantCulture));
    }

    private static int ClampMemoryGb(int value)
    {
        return MemorySizingService.ClampMemoryGb(value);
    }

    private static int GetAllowedMaxMemoryGb()
    {
        return MemorySizingService.GetAllowedMaxMemoryGb();
    }

    private void SetMemoryText(string text)
    {
        _suppressMemoryTextChanged = true;
        try
        {
            MemoryTextBox.Text = text;
            MemoryTextBox.CaretIndex = MemoryTextBox.Text.Length;
        }
        finally
        {
            _suppressMemoryTextChanged = false;
        }
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

    private void SetProgressActivity(ProgressBar progressBar, bool active)
    {
        progressBar.Foreground = (Brush)FindResource(active ? "ProgressActiveBrush" : "ProgressIdleBrush");
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
        PlayerNameTextBox.IsEnabled = !_busy && !_minecraftRunning && !_minecraftPreparing;
        PlayerNameTextBox.IsReadOnly = !_isEditingPlayerName || !PlayerNameTextBox.IsEnabled;
        ChangePlayerNameButton.IsEnabled = !_busy && !_minecraftRunning && !_minecraftPreparing;
        ChangePlayerNameButton.Content = _isEditingPlayerName ? "Сохранить" : "Изменить";
        BuildComboBox.IsEnabled = configurationEnabled && _builds.Count > 0;
        BuildPlaceholderText.Visibility = _builds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlayButton.Content = "Играть";
        PlayButton.IsEnabled = configurationEnabled && hasBuild && !_isEditingPlayerName;
        SkinButton.IsEnabled = !_minecraftRunning;
        WorldComboBox.IsEnabled = interactiveEnabled && !_minecraftPreparing && _worlds.Count > 0;
        OnlinePlayerComboBox.IsEnabled = interactiveEnabled && !_minecraftPreparing && _peers.Count > 0;
        WorldPlaceholderText.Visibility = _worlds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OnlinePlayerPlaceholderText.Visibility = _peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TransferButton.IsEnabled = interactiveEnabled && !_transferActive && !_minecraftRunning && !_minecraftPreparing &&
                                   WorldComboBox.SelectedItem is WorldViewModel &&
                                   selectedRecipient is not null &&
                                   !selectedRecipient.IsMinecraftRunning &&
                                   !selectedRecipient.IsMinecraftPreparing;
        MemoryTextBox.IsEnabled = configurationEnabled;
        UpdateButton.IsEnabled = interactiveEnabled && !_updateBusy && _preparedUpdate is not null;

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
