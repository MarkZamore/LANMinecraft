using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft;

public sealed class WorldTransferService : IAsyncDisposable, IPortableProtocolHandler
{
    public const string ProtocolName = "MinecraftPortableWorld";
    /// <summary>The launcher's one protocol version; see <see cref="PortableFormat"/>.</summary>
    public const int ProtocolVersion = PortableFormat.ProtocolVersion;
    public const string TransferMessageType = "Transfer";
    public const string ProbeMessageType = "Probe";
    public const string PrepareMessageType = "Prepare";
    public const string ProgressMessageType = "Progress";
    public const string ControlMessageType = "Control";
    internal const string SnapshotStage = "Snapshot";
    internal const string ProfileStage = "Profiles";
    internal const string CompressStage = "Compress";
    internal const string ExtractStage = "Extract";
    internal const string VerifyStage = "Verify";
    internal const string EscrowStage = "Escrow";
    internal const string InstallStage = "Install";
    private const int TransferCopyBufferBytes = 1024 * 1024;
    private const int MaxIncomingClients = 32;
    // A transfer may legitimately run for hours, so nothing caps its duration.
    // What is capped is silence: an active peer emits a progress frame at least
    // every ProgressHeartbeatInterval, so a longer gap means the peer is wedged.
    internal static readonly TimeSpan ProgressHeartbeatInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DefaultPeerIdleTimeout = TimeSpan.FromSeconds(90);
    // A peer that keeps talking but reports no movement for this long is stuck.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(30);
    // A blocked socket write is not the same signal as a silent peer: the
    // receiver's disk can legitimately stall for minutes while flushing a huge
    // archive, and its TCP window stays closed the whole time.
    private static TimeSpan WriteStallTimeout(TimeSpan idleTimeout) => idleTimeout * 8;
    private static readonly TimeSpan RejectionWriteTimeout = TimeSpan.FromSeconds(5);
    // Steam relays make a first connection slower than a LAN socket ever was.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    // The first frame is shared by world, waypoint, skin, relay and diagnostics
    // protocols. Existing waypoint snapshots may legitimately use the portable
    // protocol's full JSON limit; diagnostics applies its 256 KiB limit only
    // after the short upgrade frame and TLS handshake.
    private const int MaxInitialFrameBytes = PortableProtocol.MaxJsonFrameBytes;
    private static readonly TimeSpan InitialFrameTimeout = TimeSpan.FromSeconds(10);

    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly MinecraftProcessService _minecraft;
    private readonly SettingsService _settingsService;
    private readonly WorldMetadataService _worldMetadata;
    private readonly IIdentityService _identityService;
    private readonly WorldPlayerProfileService _playerProfiles;
    private readonly WaypointSyncService _waypointSync;
    private readonly SkinService _skinService;
    private readonly IPeerTransport _transport;
    private readonly IWorldTransferConfirmation? _confirmation;
    private readonly WorldTransferRuntimeOptions _runtimeOptions;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly JsonSerializerOptions _indentedJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _transferGate = new(1, 1);
    private readonly SemaphoreSlim _incomingClientGate =
        new(MaxIncomingClients, MaxIncomingClients);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly ConcurrentDictionary<int, Task> _receiveTasks = new();
    private readonly ConcurrentDictionary<int, Task> _cleanupTasks = new();
    private int _nextCleanupTaskId;
    private readonly object _disposeGate = new();
    private AppSettings? _incomingSettings;
    private int _nextReceiveTaskId;
    private int _disposeState;
    private Task? _disposeTask;

    public WorldTransferService(
        AppPaths paths,
        Logger logger,
        MinecraftProcessService minecraft,
        SettingsService settingsService,
        WorldMetadataService worldMetadata,
        IIdentityService identityService,
        WorldPlayerProfileService playerProfiles,
        WaypointSyncService waypointSync,
        SkinService skinService,
        IPeerTransport transport,
        WorldTransferRuntimeOptions? runtimeOptions = null,
        IWorldTransferConfirmation? confirmation = null)
    {
        _paths = paths;
        _logger = logger;
        _minecraft = minecraft;
        _settingsService = settingsService;
        _worldMetadata = worldMetadata;
        _identityService = identityService;
        _playerProfiles = playerProfiles;
        _waypointSync = waypointSync;
        _skinService = skinService;
        _transport = transport;
        _confirmation = confirmation;
        _runtimeOptions = runtimeOptions ?? new WorldTransferRuntimeOptions();
        _runtimeOptions.Validate();
        WorldTransferRecoveryService.Recover(paths, logger);
    }

    public event Action<string>? StatusChanged;
    public event Action? BecameHost;
    public event Action<WorldTransferProgress>? ProgressChanged;
    public bool IsOperationActive => _transferGate.CurrentCount == 0;

    /// <summary>
    /// The settings an incoming transfer runs with. Listening itself belongs to
    /// <see cref="PeerConnectionRouter"/> now, so this only says "accept
    /// transfers, using these settings" instead of binding a socket.
    /// </summary>
    public void UseSettingsForIncomingTransfers(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        Volatile.Write(ref _incomingSettings, settings);
    }

    /// <summary>Stops accepting new incoming transfers; those in flight finish.</summary>
    public void StopAcceptingIncomingTransfers() => Volatile.Write(ref _incomingSettings, null);

    string IPortableProtocolHandler.ProtocolName => ProtocolName;

    /// <summary>
    /// Every world transfer arrives here, whatever its first frame says: the
    /// transfer handshake predates the protocol field, so the router hands us
    /// anything it cannot name.
    /// </summary>
    async Task IPortableProtocolHandler.HandleAsync(
        Stream stream,
        byte[] initialFrame,
        PeerConnectionContext context,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(initialFrame);
        ArgumentNullException.ThrowIfNull(context);
        var settings = Volatile.Read(ref _incomingSettings);
        if (settings is null)
        {
            _logger.Warn(
                $"A world transfer from {context.PeerId} arrived while incoming transfers are disabled.");
            return;
        }

        if (!await _incomingClientGate.WaitAsync(0, token).ConfigureAwait(false))
        {
            _logger.Warn($"A world transfer from {context.PeerId} was refused: too many incoming transfers.");
            return;
        }

        var id = Interlocked.Increment(ref _nextReceiveTaskId);
        // An in-flight transfer outlives its caller: receiving is tracked here
        // so shutdown can wait for hours of work instead of cutting it off.
        var receiveTask = ObserveIncomingTransferAsync(stream, settings, initialFrame, context);
        _receiveTasks[id] = receiveTask;
        _ = receiveTask.ContinueWith(
            completedTask =>
            {
                _ = completedTask.Exception;
                _receiveTasks.TryRemove(id, out _);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        await receiveTask.ConfigureAwait(false);
    }

    private async Task ObserveIncomingTransferAsync(
        Stream stream,
        AppSettings settings,
        byte[] initialFrame,
        PeerConnectionContext context)
    {
        var token = _shutdownCts.Token;
        try
        {
            await ReceiveWorldAsync(stream, settings, initialFrame, context, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or InvalidOperationException)
        {
            _logger.Warn($"Incoming world transfer from {context.PeerId} failed: {ex.Message}");
        }
        finally
        {
            _incomingClientGate.Release();
        }
    }

    private async Task WaitForIncomingTransfersAsync()
    {
        var receiveTasks = _receiveTasks.Values.ToArray();
        if (receiveTasks.Length == 0) return;
        try
        {
            await Task.WhenAll(receiveTasks).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Incoming world transfer shutdown failed: {ex.Message}");
        }
    }

    public async Task SendWorldAsync(PeerViewModel peer, AppSettings settings, string worldPath, CancellationToken token)
    {
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdownCts.Token);
        token = operationCts.Token;
        EnsureMinecraftAvailableForTransfer("sender");
        await _transferGate.WaitAsync(token).ConfigureAwait(false);
        var gateReleased = false;
        try
        {
            BeginProgress();
            try
            {
                var identity = ResolveIdentityContext(settings);
                if (peer.PackStatus != "OK")
                {
                    _logger.Warn($"Pack hash mismatch ({peer.PackStatus}); world transfer is allowed by local settings.");
                }

                RaiseProgress(0, 0, "Проверка получателя");
                await VerifyPeerTransferReadyAsync(peer, settings, token).ConfigureAwait(false);

                var worldDir = ResolveWorldToSend(worldPath);
                WorldAccessGuard.EnsureClosed(worldDir);
                StatusChanged?.Invoke("Saving personal waypoints...");
                RaiseProgress(0, 0, "Сохранение точек");
                await _waypointSync.FlushWorldAsync(worldDir, identity, token).ConfigureAwait(false);
                RaiseProgress(0, 0, "Подключение к получателю");
                var worldName = Path.GetFileName(worldDir);
                var worldMetadata = _worldMetadata.Read(worldDir);
                var ownerId = ResolveOwnerIdentity(
                    worldMetadata?.OwnerIdentityId,
                    worldMetadata?.OwnerIdentityName,
                    settings,
                    identity.MinecraftUuid,
                    identity.IdentityName,
                    metadataOwnerSteamId: worldMetadata?.OwnerSteamId64,
                    localOwnerSteamId: identity.SteamId64.ToString());
                var transferId = Guid.NewGuid().ToString("N");
                var transactionRoot = CreateTransactionDirectory(transferId);
                var stagingWorld = Path.Combine(transactionRoot, "staging-world");
                var archivePath = Path.Combine(transactionRoot, "world.zip");
                var escrowPath = Path.Combine(transactionRoot, "escrow", worldName);
                var journal = new WorldTransferJournal
                {
                    TransferId = transferId,
                    Role = "Sender",
                    State = "Preparing",
                    SourceWorldPath = worldDir,
                    EscrowPath = escrowPath
                };
                WriteJournal(transactionRoot, journal);
                var completed = false;

                try
                {
                    // Connect before preparation so the receiver can follow
                    // snapshot and compression progress in its own window.
                    await using var connection = await _transport
                        .ConnectAsync(peer.SteamId, ProtocolName, token)
                        .ConfigureAwait(false);
                    var stream = connection.Stream;
                    await WriteJsonAsync(stream, new WorldTransferHeader
                    {
                        Protocol = ProtocolName,
                        ProtocolVersion = ProtocolVersion,
                        MessageType = PrepareMessageType,
                        TransferId = transferId,
                        SenderName = identity.IdentityName,
                        SenderIdentityId = identity.MinecraftUuid,
                        SenderSteamId64 = identity.SteamId64.ToString(),
                        SenderIdentityName = identity.IdentityName,
                        Size = 0,
                        FileName = "world.zip",
                        WorldName = worldName
                    }, token);
                    var preparedFrame = await ReadFrameWithIdleTimeoutAsync(stream, token).ConfigureAwait(false);
                    var prepared = PortableProtocol.Deserialize<WorldTransferAck>(preparedFrame, _jsonOptions);
                    if (prepared is null || !HasExpectedProtocol(prepared.Protocol, prepared.ProtocolVersion) ||
                        !prepared.Ok || prepared.Stage != "Preparing" || prepared.TransferId != transferId)
                    {
                        throw new InvalidOperationException(
                            $"Receiver rejected world transfer: {prepared?.Message ?? "no prepare acknowledgement"}");
                    }

                    var progress = new TransferProgressChannel(this, stream, transferId, token);
                    StatusChanged?.Invoke("Creating safe world snapshot...");
                    await progress.PublishStageAsync(SnapshotStage, "Копирование мира");
                    await CopyWorldDirectoryAsync(
                        worldDir,
                        stagingWorld,
                        (current, total) => progress.PublishAsync(
                            SnapshotStage, "Копирование мира", current, total),
                        progress.HeartbeatAsync,
                        token).ConfigureAwait(false);
                    StatusChanged?.Invoke("Preparing player profiles...");
                    await progress.PublishStageAsync(ProfileStage, "Подготовка профилей");
                    string playerManifestSha = "";
                    string waypointManifestSha = "";
                    await RunWithHeartbeatAsync(progress, () =>
                    {
                        _playerProfiles.PrepareWorldForOutgoingTransfer(stagingWorld, identity);
                        playerManifestSha = _playerProfiles.GetPlayerManifestHash(stagingWorld);
                        _waypointSync.Store.EnsureManifest(stagingWorld);
                        waypointManifestSha = _waypointSync.Store.GetManifestHash(stagingWorld);
                    }, token).ConfigureAwait(false);

                    StatusChanged?.Invoke("Compressing world...");
                    await progress.PublishStageAsync(CompressStage, "Сжатие мира");
                    var worldSha = await CreateWorldArchiveWithHashAsync(
                        stagingWorld,
                        archivePath,
                        (current, total) => progress.PublishAsync(
                            CompressStage, "Сжатие мира", current, total),
                        progress.HeartbeatAsync,
                        token).ConfigureAwait(false);

                    var fileInfo = new FileInfo(archivePath);
                    RaiseProgress(0, fileInfo.Length, "Отправка мира");

                    var header = new WorldTransferHeader
                    {
                        Protocol = ProtocolName,
                        ProtocolVersion = ProtocolVersion,
                        MessageType = TransferMessageType,
                        TransferId = transferId,
                        SenderName = identity.IdentityName,
                        SenderIdentityId = identity.MinecraftUuid,
                        SenderSteamId64 = identity.SteamId64.ToString(),
                        SenderIdentityName = identity.IdentityName,
                        OwnerIdentityId = ownerId.id,
                        OwnerSteamId64 = ownerId.steamId,
                        OwnerIdentityName = ownerId.name,
                        Size = fileInfo.Length,
                        WorldSha256 = worldSha,
                        PlayerManifestSha256 = playerManifestSha,
                        WaypointManifestSha256 = waypointManifestSha,
                        FileName = Path.GetFileName(archivePath),
                        WorldName = worldName
                    };

                    StatusChanged?.Invoke("Sending world archive...");
                    await WriteJsonAsync(stream, header, token);
                    await using (var file = File.OpenRead(archivePath))
                    {
                        await CopyWithProgressAsync(file, stream, fileInfo.Length, current =>
                        {
                            progress.PublishLocal("Отправка мира", current, fileInfo.Length);
                        }, WriteStallTimeout(_runtimeOptions.PeerIdleTimeout), token);
                    }

                    var ready = await ReadAckWatchingProgressAsync(stream, transferId, token);
                    if (ready is null || !HasExpectedProtocol(ready.Protocol, ready.ProtocolVersion) ||
                        !ready.Ok || ready.Stage != "Ready" || ready.TransferId != transferId ||
                        !string.Equals(ready.WorldSha256, worldSha, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(ready.PlayerManifestSha256, playerManifestSha, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Receiver rejected world archive: {ready?.Message ?? "no ready acknowledgement"}");
                    }
                    if (!string.Equals(ready.WaypointManifestSha256, waypointManifestSha, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Receiver rejected world archive: {ready?.Message ?? "no ready acknowledgement"}");
                    }

                    await progress.PublishStageAsync(EscrowStage, "Перенос исходного мира");
                    await RunWithHeartbeatAsync(progress, () =>
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(escrowPath)!);
                        Directory.Move(worldDir, escrowPath);
                    }, token).ConfigureAwait(false);
                    journal.State = "Escrowed";
                    WriteJournal(transactionRoot, journal);
                    journal.State = "CommitSent";
                    WriteJournal(transactionRoot, journal);
                    await WriteJsonAsync(stream, new WorldTransferControl
                    {
                        Protocol = ProtocolName,
                        ProtocolVersion = ProtocolVersion,
                        TransferId = transferId,
                        MessageType = ControlMessageType,
                        Command = "Commit"
                    }, token);

                    var committed = await ReadAckWatchingProgressAsync(stream, transferId, token);
                    if (committed is null || !HasExpectedProtocol(committed.Protocol, committed.ProtocolVersion) ||
                        !committed.Ok || committed.Stage != "Committed" || committed.TransferId != transferId ||
                        !string.Equals(committed.WorldSha256, worldSha, StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(committed.PlayerManifestSha256) ||
                        !string.Equals(committed.WaypointManifestSha256, waypointManifestSha, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "Transfer commit was not confirmed. The source world remains safely quarantined in Personal\\Transfers.");
                    }

                    journal.State = "Committed";
                    WriteJournal(transactionRoot, journal);
                    completed = true;
                    var selectedRelativePath = Path.GetRelativePath(_paths.Worlds, worldDir);
                    if (string.Equals(settings.SelectedWorldRelativePath, selectedRelativePath, StringComparison.OrdinalIgnoreCase))
                    {
                        settings.SelectedWorldRelativePath = "";
                    }

                    _logger.Info("World archive transferred successfully; local source world removed.");
                }
                catch
                {
                    if (journal.State != "CommitSent" && Directory.Exists(escrowPath) && !Directory.Exists(worldDir))
                    {
                        Directory.Move(escrowPath, worldDir);
                    }
                    throw;
                }
                finally
                {
                    // Deleting the transaction root can mean multiple copies of
                    // a multi-GB world; do it in the background so the transfer
                    // reports done and the gate frees as soon as the peers have
                    // committed. Leftovers are swept on the next launch. If the
                    // escrow rollback itself failed, the root still holds the
                    // only copy of the world - keep it for startup recovery.
                    var escrowStillHoldsWorld = !completed &&
                        Directory.Exists(escrowPath) && !Directory.Exists(worldDir);
                    if ((completed || journal.State != "CommitSent") && !escrowStillHoldsWorld)
                    {
                        ScheduleTransactionCleanup(transactionRoot);
                    }
                }
            }
            finally
            {
                // Release before reporting idle: the UI must never look free
                // while the gate would still refuse the next transfer.
                _transferGate.Release();
                gateReleased = true;
                EndProgress();
            }
        }
        finally
        {
            if (!gateReleased) _transferGate.Release();
        }
    }

    private async Task ReceiveWorldAsync(
        Stream stream,
        AppSettings settings,
        byte[] initialFrame,
        PeerConnectionContext context,
        CancellationToken token)
    {
        var identity = ResolveIdentityContext(settings);
        WorldTransferHeader? header = null;
        string? transactionRoot = null;
        string? receivedPath = null;
        string? tempWorldPath = null;
        WorldTransferJournal? journal = null;
        var operationAcquired = false;
        var progressStarted = false;
        try
        {
            header = PortableProtocol.Deserialize<WorldTransferHeader>(initialFrame, _jsonOptions)
                ?? throw new InvalidOperationException("Invalid transfer header.");
            if (!HasExpectedProtocol(header.Protocol, header.ProtocolVersion))
            {
                throw new InvalidOperationException("The sender uses an incompatible world transfer protocol.");
            }
            if (!string.Equals(
                    header.SenderSteamId64,
                    context.PeerId.ToString(),
                    StringComparison.Ordinal))
            {
                await WriteJsonAsync(stream, new WorldTransferAck
                {
                    Protocol = ProtocolName,
                    ProtocolVersion = ProtocolVersion,
                    Ok = false,
                    Stage = "Rejected",
                    TransferId = header.TransferId,
                    Message = "Отправитель не совпадает с Steam-подключением."
                }, token).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "The world transfer header does not match the Steam account that opened the connection.");
            }
            if (header.MessageType == ProbeMessageType)
            {
                var available = _transferGate.CurrentCount > 0 &&
                                !_minecraft.IsClientRunning &&
                                !_minecraft.IsClientPreparing;
                await WriteJsonAsync(stream, new WorldTransferAck
                {
                    Protocol = ProtocolName,
                    ProtocolVersion = ProtocolVersion,
                    Ok = available,
                    Stage = "Probe",
                    Message = available
                        ? "ready"
                        : _minecraft.IsClientRunning
                            ? "Minecraft is running on the receiver"
                            : _minecraft.IsClientPreparing
                                ? "Minecraft is being prepared on the receiver"
                            : "another world transfer is active"
                }, token);
                return;
            }
            var isPrepare = header.MessageType == PrepareMessageType;
            if ((header.MessageType != TransferMessageType && !isPrepare) ||
                !Guid.TryParseExact(header.TransferId, "N", out var parsedTransferId))
            {
                throw new InvalidOperationException("The sender uses an incompatible world transfer protocol.");
            }
            operationAcquired = await _transferGate.WaitAsync(0, token).ConfigureAwait(false);
            if (!operationAcquired)
            {
                throw new InvalidOperationException("Another world transfer is already active.");
            }
            EnsureMinecraftAvailableForTransfer("receiver");

            if (!await ConfirmIncomingWorldAsync(stream, header, context, token).ConfigureAwait(false))
            {
                return;
            }

            transactionRoot = CreateTransactionDirectory(header.TransferId);
            journal = new WorldTransferJournal
            {
                TransferId = header.TransferId,
                Role = "Receiver",
                State = "Receiving"
            };
            WriteJournal(transactionRoot, journal);
            BeginProgress();
            progressStarted = true;

            if (isPrepare)
            {
                await WriteJsonAsync(stream, new WorldTransferAck
                {
                    Protocol = ProtocolName,
                    ProtocolVersion = ProtocolVersion,
                    Ok = true,
                    Stage = "Preparing",
                    TransferId = header.TransferId,
                    Message = "watching"
                }, token);
                StatusChanged?.Invoke("Waiting for the sender to prepare the world...");
                RaiseProgress(0, 0, "Подготовка у отправителя");
                header = await WaitForTransferHeaderAsync(stream, header.TransferId, token);
            }

            if (header.Size <= 0 ||
                string.IsNullOrWhiteSpace(header.WorldSha256) ||
                string.IsNullOrWhiteSpace(header.PlayerManifestSha256) ||
                string.IsNullOrWhiteSpace(header.WaypointManifestSha256))
            {
                throw new InvalidOperationException("Transfer header is incomplete.");
            }
            EnsureSufficientDiskSpace(transactionRoot, header.Size);

            receivedPath = Path.Combine(transactionRoot, "received.zip");
            var progressChannel = new TransferProgressChannel(this, stream, header.TransferId, token);
            RaiseProgress(0, header.Size, "Получение мира");
            await using (var file = new FileStream(
                receivedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, TransferCopyBufferBytes))
            {
                await CopyExactlyWithProgressAsync(stream, file, header.Size, current =>
                {
                    progressChannel.PublishLocal("Получение мира", current, header.Size);
                }, _runtimeOptions.PeerIdleTimeout, token);
            }

            tempWorldPath = Path.Combine(transactionRoot, "staging-world");
            Directory.CreateDirectory(tempWorldPath);
            StatusChanged?.Invoke("Extracting world archive...");
            await progressChannel.PublishStageAsync(ExtractStage, "Распаковка мира");
            await ExtractWorldArchiveAsync(
                receivedPath,
                tempWorldPath,
                (current, total) => progressChannel.PublishAsync(
                    ExtractStage, "Распаковка мира", current, total),
                // The archive already occupies its own space on disk, so only
                // the extracted tree still has to fit.
                declaredSize => EnsureSufficientDiskSpace(transactionRoot!, 0, declaredSize),
                token,
                progressChannel.HeartbeatAsync).ConfigureAwait(false);
            if (!IsMinecraftWorldDirectory(tempWorldPath))
            {
                throw new InvalidOperationException("Received archive does not contain a Minecraft world.");
            }

            StatusChanged?.Invoke("Verifying world integrity...");
            await progressChannel.PublishStageAsync(VerifyStage, "Проверка мира");
            var receivedWorldSha = await HashDirectoryAsync(
                tempWorldPath,
                (current, total) => progressChannel.PublishAsync(
                    VerifyStage, "Проверка мира", current, total),
                token,
                progressChannel.HeartbeatAsync).ConfigureAwait(false);
            if (!string.Equals(receivedWorldSha, header.WorldSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("World SHA256 mismatch after extraction.");
            }

            StatusChanged?.Invoke("Preparing player profile...");
            await progressChannel.PublishStageAsync(InstallStage, "Подготовка профилей");
            var stagedWorldPath = tempWorldPath;
            string sourceManifestSha = "";
            string waypointManifestSha = "";
            string installedManifestSha = "";
            var owner = ResolveOwnerIdentity(
                null,
                null,
                settings,
                identity.MinecraftUuid,
                identity.IdentityName,
                header.OwnerIdentityId,
                header.OwnerIdentityName,
                headerOwnerSteamId: header.OwnerSteamId64,
                localOwnerSteamId: identity.SteamId64.ToString());
            await RunWithHeartbeatAsync(progressChannel, () =>
            {
                _playerProfiles.ValidatePlayerManifest(stagedWorldPath);
                sourceManifestSha = _playerProfiles.GetPlayerManifestHash(stagedWorldPath);
                if (!string.Equals(sourceManifestSha, header.PlayerManifestSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Player manifest SHA256 mismatch after extraction.");
                }
                _waypointSync.Store.ValidateManifest(stagedWorldPath);
                waypointManifestSha = _waypointSync.Store.GetManifestHash(stagedWorldPath);
                if (!string.Equals(waypointManifestSha, header.WaypointManifestSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Waypoint manifest SHA256 mismatch after extraction.");
                }

                _playerProfiles.PrepareReceivedWorldForIdentity(stagedWorldPath, identity);
                _playerProfiles.ValidatePlayerManifest(stagedWorldPath);
                installedManifestSha = _playerProfiles.GetPlayerManifestHash(stagedWorldPath);
                if (!_worldMetadata.TryWriteOwnerMetadata(
                        stagedWorldPath,
                        owner.id,
                        owner.name,
                        overwriteExistingOwner: false,
                        ownerSteamId64: owner.steamId))
                {
                    throw new InvalidOperationException("Could not preserve world creator metadata.");
                }
                if (!_worldMetadata.TryWriteCurrentHolderMetadata(
                        stagedWorldPath,
                        identity.MinecraftUuid,
                        identity.IdentityName,
                        transferred: true,
                        holderSteamId64: identity.SteamId64.ToString()))
                {
                    throw new InvalidOperationException("Could not update current world holder metadata.");
                }
            }, token).ConfigureAwait(false);

            EnsureMinecraftAvailableForTransfer("receiver");
            journal.State = "Ready";
            WriteJournal(transactionRoot, journal);
            await WriteJsonAsync(stream, new WorldTransferAck
            {
                Protocol = ProtocolName,
                ProtocolVersion = ProtocolVersion,
                Ok = true,
                Stage = "Ready",
                TransferId = header.TransferId,
                Message = "ready",
                WorldSha256 = receivedWorldSha,
                PlayerManifestSha256 = sourceManifestSha,
                WaypointManifestSha256 = waypointManifestSha
            }, token);
            var control = await ReadControlWatchingProgressAsync(
                stream, header.TransferId, token).ConfigureAwait(false);
            if (control is null || !HasExpectedProtocol(control.Protocol, control.ProtocolVersion) ||
                control.TransferId != header.TransferId || control.Command != "Commit")
            {
                throw new InvalidOperationException("World transfer commit command is invalid.");
            }

            journal.State = "CommitReceived";
            WriteJournal(transactionRoot, journal);
            EnsureMinecraftAvailableForTransfer("receiver");
            await progressChannel.PublishStageAsync(InstallStage, "Установка мира");
            var stagedForInstall = tempWorldPath;
            var installedWorldPath = "";
            await RunWithHeartbeatAsync(
                progressChannel,
                () => installedWorldPath = InstallReceivedWorld(stagedForInstall, header.WorldName),
                token).ConfigureAwait(false);
            tempWorldPath = null;
            journal.State = "Installed";
            journal.InstalledWorldPath = installedWorldPath;
            WriteJournal(transactionRoot, journal);
            await WriteJsonAsync(stream, new WorldTransferAck
            {
                Protocol = ProtocolName,
                ProtocolVersion = ProtocolVersion,
                Ok = true,
                Stage = "Committed",
                TransferId = header.TransferId,
                Message = "accepted",
                WorldSha256 = receivedWorldSha,
                PlayerManifestSha256 = installedManifestSha,
                WaypointManifestSha256 = waypointManifestSha
            }, token);
            journal.State = "Committed";
            WriteJournal(transactionRoot, journal);
            settings.SelectedWorldRelativePath = Path.GetRelativePath(_paths.Worlds, installedWorldPath);
            _settingsService.Save(settings);
            BecameHost?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Warn($"World receive failed: {ex.Message}");
            try
            {
                // Best effort only: a peer that stopped reading must not be able
                // to wedge this task, which listener restart and shutdown await.
                using var rejection = new CancellationTokenSource(RejectionWriteTimeout);
                await WriteJsonAsync(stream, new WorldTransferAck
                {
                    Protocol = ProtocolName,
                    ProtocolVersion = ProtocolVersion,
                    Ok = false,
                    Stage = "Rejected",
                    TransferId = header?.TransferId ?? string.Empty,
                    Message = ex.Message,
                    WorldSha256 = header?.WorldSha256 ?? string.Empty,
                    WaypointManifestSha256 = header?.WaypointManifestSha256 ?? string.Empty
                }, rejection.Token);
            }
            catch
            {
            }
        }
        finally
        {
            // received.zip and the staging tree live inside the transaction
            // root, and a successful install has already moved the world out of
            // it, so one background delete covers every path. The gate frees
            // immediately; leftovers are swept on the next launch.
            ScheduleTransactionCleanup(transactionRoot);
            if (operationAcquired) _transferGate.Release();
            if (progressStarted) EndProgress();
        }
    }

    private void ScheduleTransactionCleanup(string? transactionRoot)
    {
        if (string.IsNullOrWhiteSpace(transactionRoot) || !Directory.Exists(transactionRoot)) return;
        var id = Interlocked.Increment(ref _nextCleanupTaskId);
        var cleanup = Task.Run(() =>
        {
            try
            {
                DeleteDirectoryIfExists(transactionRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warn(
                    $"World transfer cleanup could not delete {Path.GetFileName(transactionRoot)}: " +
                    $"{ex.Message}; it will be removed on the next launch.");
            }
        });
        _cleanupTasks[id] = cleanup;
        _ = cleanup.ContinueWith(
            completedTask =>
            {
                _ = completedTask.Exception;
                _cleanupTasks.TryRemove(id, out _);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private string InstallReceivedWorld(string extractedWorldPath, string? worldName)
    {
        var safeWorldName = GetSafeWorldName(worldName);
        var worldDir = GetAvailableWorldDirectory(safeWorldName);
        _paths.EnsureUnderRoot(worldDir);
        Directory.CreateDirectory(_paths.Worlds);
        Directory.Move(extractedWorldPath, worldDir);
        _logger.Info($"Received world installed: {Path.GetFileName(worldDir)}.");
        return worldDir;
    }

    /// <summary>
    /// Asks the receiver whether it can take a world at all before hours of
    /// snapshotting begin. One Steam connection, one question - the VPN era had
    /// to try every candidate address it had ever seen for this player.
    /// </summary>
    private async Task VerifyPeerTransferReadyAsync(
        PeerViewModel peer,
        AppSettings settings,
        CancellationToken token)
    {
        var identity = _identityService.ResolveContext(settings);
        if (!_transport.IsAvailable)
        {
            throw new InvalidOperationException(_transport.UnavailableReason);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(ProbeTimeout);
        try
        {
            await using var connection = await _transport
                .ConnectAsync(peer.SteamId, ProtocolName, timeoutCts.Token)
                .ConfigureAwait(false);
            await WriteJsonAsync(connection.Stream, new WorldTransferHeader
            {
                Protocol = ProtocolName,
                ProtocolVersion = ProtocolVersion,
                MessageType = ProbeMessageType,
                SenderName = identity.IdentityName,
                SenderIdentityId = identity.MinecraftUuid,
                SenderSteamId64 = identity.SteamId64.ToString(),
                SenderIdentityName = identity.IdentityName,
                Size = 0,
                FileName = "probe",
                WorldName = ""
            }, timeoutCts.Token).ConfigureAwait(false);

            var ack = await ReadJsonAsync<WorldTransferAck>(connection.Stream, timeoutCts.Token)
                .ConfigureAwait(false);
            if (ack is null || !HasExpectedProtocol(ack.Protocol, ack.ProtocolVersion) ||
                !ack.Ok || ack.Stage != "Probe")
            {
                throw new InvalidOperationException(ack?.Message ?? "receiver did not accept transfer probe");
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException
                                       or InvalidDataException or JsonException)
        {
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                BuildPeerConnectionMessage(peer.DisplayName) + Environment.NewLine + ex.Message,
                ex);
        }
    }

    private void RaiseProgress(long current, long total, string stage = "")
    {
        try
        {
            ProgressChanged?.Invoke(new WorldTransferProgress(true, current, total, stage));
        }
        catch
        {
        }
    }

    // The archive and the extracted world exist side by side before install,
    // so the receiver needs room for both plus the size the entries declare.
    internal static void EnsureSufficientDiskSpace(string transactionRoot, long archiveSize, long extractedSize = 0)
    {
        var companionSize = extractedSize > 0 ? extractedSize : archiveSize;
        if (archiveSize < 0 || extractedSize < 0 ||
            archiveSize > long.MaxValue - companionSize)
        {
            throw new InvalidOperationException("The declared world size is not plausible.");
        }

        var required = archiveSize + companionSize;
        DriveInfo drive;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(transactionRoot));
            if (string.IsNullOrEmpty(root)) return;
            drive = new DriveInfo(root);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return;
        }

        if (drive.AvailableFreeSpace < required)
        {
            throw new InvalidOperationException(
                $"Not enough free disk space to receive the world: {FormatGigabytes(required)} GB required, " +
                $"{FormatGigabytes(drive.AvailableFreeSpace)} GB available on {drive.Name}.");
        }
    }

    private static string FormatGigabytes(long bytes) =>
        (bytes / (1024d * 1024d * 1024d)).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static string ReadMessageType(byte[] frame)
    {
        try
        {
            using var document = JsonDocument.Parse(frame);
            return document.RootElement.TryGetProperty("messageType", out var messageType)
                ? messageType.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    // Reads one frame, failing if the peer stays silent past PeerIdleTimeout.
    // The peer heartbeats every ProgressHeartbeatInterval while it is working,
    // so only a wedged peer trips this - long transfers never do.
    private async Task<byte[]> ReadFrameWithIdleTimeoutAsync(
        Stream stream,
        CancellationToken token)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        idle.CancelAfter(_runtimeOptions.PeerIdleTimeout);
        try
        {
            return await PortableProtocol.ReadFrameAsync(stream, idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("The other player stopped responding.");
        }
    }

    // Reads frames until a non-progress one arrives, publishing the peer's
    // progress meanwhile. Progress frames alone cannot keep this alive forever:
    // a peer that reports no forward movement for StallTimeout is treated as
    // wedged, which bounds the wait without capping a legitimate transfer.
    private async Task<byte[]> ReadNonProgressFrameAsync(
        Stream stream,
        string transferId,
        Func<string, string> describeStage,
        CancellationToken token)
    {
        var throttle = new ProgressReceiveThrottle();
        var stallClock = System.Diagnostics.Stopwatch.StartNew();
        var lastStage = "";
        long lastCurrent = -1;
        while (true)
        {
            var frame = await ReadFrameWithIdleTimeoutAsync(stream, token).ConfigureAwait(false);
            if (ReadMessageType(frame) != ProgressMessageType) return frame;

            // Every progress-typed frame counts against the stall bound, but
            // only a valid frame that actually advances can reset it — else a
            // stream of malformed frames would keep the gate hostage forever.
            var progress = PortableProtocol.Deserialize<WorldTransferProgressFrame>(frame, _jsonOptions);
            var valid = progress is not null &&
                HasExpectedProtocol(progress.Protocol, progress.ProtocolVersion) &&
                progress.TransferId == transferId;
            if (valid && (progress!.Current != lastCurrent ||
                !string.Equals(progress.Stage, lastStage, StringComparison.Ordinal)))
            {
                lastCurrent = progress.Current;
                lastStage = progress.Stage;
                stallClock.Restart();
            }
            else if (stallClock.Elapsed > StallTimeout)
            {
                throw new TimeoutException("The other player stopped making progress.");
            }
            if (valid && throttle.ShouldPublish(progress!))
            {
                RaiseProgress(progress!.Current, progress.Total, describeStage(progress.Stage));
            }
        }
    }

    private async Task<WorldTransferAck?> ReadAckWatchingProgressAsync(
        Stream stream,
        string transferId,
        CancellationToken token)
    {
        var frame = await ReadNonProgressFrameAsync(
            stream, transferId, DescribeRemoteStageForSender, token).ConfigureAwait(false);
        return PortableProtocol.Deserialize<WorldTransferAck>(frame, _jsonOptions);
    }

    private async Task<WorldTransferControl?> ReadControlWatchingProgressAsync(
        Stream stream,
        string transferId,
        CancellationToken token)
    {
        var frame = await ReadNonProgressFrameAsync(
            stream, transferId, DescribeRemoteStageForReceiver, token).ConfigureAwait(false);
        return PortableProtocol.Deserialize<WorldTransferControl>(frame, _jsonOptions);
    }

    /// <summary>
    /// Asks the player whether to take this world. Under Steam the sender can
    /// be any friend running the launcher, which is a wider circle than the
    /// neighbours on a VPN, so an incoming world is never installed silently.
    ///
    /// The sender is waiting on its own idle timeout while this dialog is open,
    /// so the wait is bounded and a refusal is answered explicitly.
    /// </summary>
    private async Task<bool> ConfirmIncomingWorldAsync(
        Stream stream,
        WorldTransferHeader header,
        PeerConnectionContext context,
        CancellationToken token)
    {
        if (_confirmation is null) return true;

        var offer = new WorldTransferOffer(
            context.PeerId,
            string.IsNullOrWhiteSpace(context.PersonaName)
                ? context.PeerId.ToString()
                : context.PersonaName,
            string.IsNullOrWhiteSpace(header.SenderName) ? header.SenderIdentityName : header.SenderName,
            string.IsNullOrWhiteSpace(header.WorldName) ? "мир" : header.WorldName,
            header.Size);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_runtimeOptions.PeerIdleTimeout);
        bool accepted;
        try
        {
            RaiseProgress(0, 0, "Ожидание подтверждения");
            accepted = await _confirmation.ConfirmAsync(offer, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            accepted = false;
        }

        if (accepted)
        {
            _logger.Info($"Incoming world from {context.PeerId} was accepted by the player.");
            return true;
        }

        _logger.Info($"Incoming world from {context.PeerId} was declined by the player.");
        await WriteJsonAsync(stream, new WorldTransferAck
        {
            Protocol = ProtocolName,
            ProtocolVersion = ProtocolVersion,
            Ok = false,
            Stage = "Rejected",
            TransferId = header.TransferId,
            Message = "Получатель отклонил приём мира."
        }, token).ConfigureAwait(false);
        StatusChanged?.Invoke("Incoming world declined");
        return false;
    }

    private async Task<WorldTransferHeader> WaitForTransferHeaderAsync(
        Stream stream,
        string transferId,
        CancellationToken token)
    {
        var frame = await ReadNonProgressFrameAsync(
            stream, transferId, DescribeRemoteStageForReceiver, token).ConfigureAwait(false);
        if (ReadMessageType(frame) != TransferMessageType)
        {
            throw new InvalidOperationException("The sender uses an incompatible world transfer protocol.");
        }
        var header = PortableProtocol.Deserialize<WorldTransferHeader>(frame, _jsonOptions)
            ?? throw new InvalidOperationException("Invalid transfer header.");
        if (!HasExpectedProtocol(header.Protocol, header.ProtocolVersion) ||
            header.TransferId != transferId)
        {
            throw new InvalidOperationException("The sender uses an incompatible world transfer protocol.");
        }
        return header;
    }

    // Caps how often remote frames reach the UI thread: the sending side already
    // throttles, but a nonconforming peer must not flood the dispatcher queue.
    private sealed class ProgressReceiveThrottle
    {
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private TimeSpan _nextAt;
        private string _stage = "";

        public bool ShouldPublish(WorldTransferProgressFrame frame)
        {
            var now = _clock.Elapsed;
            var stageChanged = !string.Equals(_stage, frame.Stage, StringComparison.Ordinal);
            if (!stageChanged && now < _nextAt) return false;
            _stage = frame.Stage;
            _nextAt = now + TransferProgressChannel.LocalInterval;
            return true;
        }
    }

    private static string DescribeRemoteStageForSender(string stage) => stage switch
    {
        ExtractStage => "Распаковка у получателя",
        VerifyStage => "Проверка у получателя",
        InstallStage => "Установка у получателя",
        _ => "Обработка у получателя"
    };

    private static string DescribeRemoteStageForReceiver(string stage) => stage switch
    {
        SnapshotStage => "Копирование у отправителя",
        ProfileStage => "Профили у отправителя",
        CompressStage => "Сжатие у отправителя",
        EscrowStage => "Завершение у отправителя",
        _ => "Подготовка у отправителя"
    };

    private sealed class TransferProgressChannel
    {
        internal static readonly TimeSpan LocalInterval = TimeSpan.FromMilliseconds(50);
        private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(250);
        private readonly WorldTransferService _service;
        private readonly Stream _peerStream;
        private readonly string _transferId;
        private readonly CancellationToken _token;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private TimeSpan _nextLocalAt;
        private TimeSpan _nextFrameAt;
        private string _stage = "";
        private string _lastFrameStage = "";
        private long _lastFrameCurrent;
        private long _lastFrameTotal;

        public TransferProgressChannel(
            WorldTransferService service,
            Stream peerStream,
            string transferId,
            CancellationToken token)
        {
            _service = service;
            _peerStream = peerStream;
            _transferId = transferId;
            _token = token;
        }

        // Publishes to the local UI and mirrors the same numbers to the peer.
        public async Task PublishAsync(string stage, string localLabel, long current, long total)
        {
            var stageChanged = !string.Equals(_stage, stage, StringComparison.Ordinal);
            _stage = stage;
            var final = current >= total;
            var now = _clock.Elapsed;
            if (stageChanged || final || now >= _nextLocalAt)
            {
                _nextLocalAt = now + LocalInterval;
                _service.RaiseProgress(current, total, localLabel);
            }
            if (stageChanged || final || now >= _nextFrameAt)
            {
                await WriteFrameAsync(stage, current, total).ConfigureAwait(false);
            }
        }

        // Local-only progress for the network copy: each side already sees
        // its own byte counters, so no frames are mirrored to the peer.
        public void PublishLocal(string localLabel, long current, long total)
        {
            var now = _clock.Elapsed;
            if (current < total && now < _nextLocalAt) return;
            _nextLocalAt = now + LocalInterval;
            _service.RaiseProgress(current, total, localLabel);
        }

        // Repeats the last frame so a peer blocked on a read can tell the
        // difference between "still working" and "wedged" (see PeerIdleTimeout).
        public async Task HeartbeatAsync()
        {
            if (_clock.Elapsed < _nextFrameAt) return;
            await WriteFrameAsync(_lastFrameStage, _lastFrameCurrent, _lastFrameTotal).ConfigureAwait(false);
        }

        // Enters a stage before its byte totals are known, so neither side
        // shows a stale label while a phase is being measured.
        public async Task PublishStageAsync(string stage, string localLabel)
        {
            _stage = stage;
            _nextLocalAt = _clock.Elapsed + LocalInterval;
            _service.RaiseProgress(0, 0, localLabel);
            await WriteFrameAsync(stage, 0, 0).ConfigureAwait(false);
        }

        private async Task WriteFrameAsync(string stage, long current, long total)
        {
            _nextFrameAt = _clock.Elapsed + FrameInterval;
            _lastFrameStage = stage;
            _lastFrameCurrent = current;
            _lastFrameTotal = total;
            await _service.WriteJsonAsync(_peerStream, new WorldTransferProgressFrame
            {
                Protocol = ProtocolName,
                ProtocolVersion = ProtocolVersion,
                MessageType = ProgressMessageType,
                TransferId = _transferId,
                Stage = stage,
                Current = current,
                Total = total
            }, _token).ConfigureAwait(false);
        }
    }

    // Beats the peer's idle timeout while a synchronous phase runs with no
    // natural progress callbacks (profile validation, manifest hashing).
    private static async Task RunWithHeartbeatAsync(
        TransferProgressChannel progress,
        Action work,
        CancellationToken token)
    {
        var task = Task.Run(work, CancellationToken.None);
        try
        {
            while (!task.IsCompleted)
            {
                using var beat = CancellationTokenSource.CreateLinkedTokenSource(token);
                var delay = Task.Delay(ProgressHeartbeatInterval, beat.Token);
                var finished = await Task.WhenAny(task, delay).ConfigureAwait(false);
                beat.Cancel();
                if (finished == task) break;
                token.ThrowIfCancellationRequested();
                await progress.HeartbeatAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // The work touches files the caller's cleanup deletes, so let it
            // finish before reporting why the heartbeat stopped.
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
            }
            throw;
        }

        await task.ConfigureAwait(false);
    }

    private void EnsureMinecraftAvailableForTransfer(string role)
    {
        if (!_minecraft.IsClientRunning && !_minecraft.IsClientPreparing) return;
        throw new InvalidOperationException(
            $"Minecraft is running or being prepared on the transfer {role}.");
    }

    private static bool HasExpectedProtocol(string? protocol, int version) =>
        string.Equals(protocol, ProtocolName, StringComparison.Ordinal) && version == ProtocolVersion;

    private void BeginProgress(long total = 0) => RaiseProgress(0, total);

    private void EndProgress()
    {
        try
        {
            ProgressChanged?.Invoke(new WorldTransferProgress(false, 0, 0));
        }
        catch
        {
        }
    }

    private string GetTransferTempDirectory()
    {
        var path = Path.Combine(_paths.Personal, "Transfers");
        _paths.EnsureUnderRoot(path);
        Directory.CreateDirectory(path);
        return path;
    }

    private LocalIdentityContext ResolveIdentityContext(AppSettings settings)
    {
        return _identityService.ResolveContext(settings);
    }

    private string CreateTransactionDirectory(string transferId)
    {
        if (!Guid.TryParseExact(transferId, "N", out _)) throw new InvalidDataException("Transfer ID is invalid.");
        var path = Path.Combine(GetTransferTempDirectory(), transferId);
        _paths.EnsureUnderRoot(path);
        if (Directory.Exists(path)) throw new IOException("Transfer transaction already exists.");
        Directory.CreateDirectory(path);
        return path;
    }

    private void WriteJournal(string transactionRoot, WorldTransferJournal journal)
    {
        journal.UpdatedAtUtc = DateTimeOffset.UtcNow;
        AtomicFile.WriteAllText(
            Path.Combine(transactionRoot, "transaction.json"),
            JsonSerializer.Serialize(journal, _indentedJsonOptions));
    }

    private static async Task CopyWorldDirectoryAsync(
        string sourceRoot,
        string destinationRoot,
        Func<long, long, Task> progress,
        Func<Task> heartbeat,
        CancellationToken token)
    {
        Directory.CreateDirectory(destinationRoot);
        var files = new List<(string Source, string Destination)>();
        long totalBytes = 0;
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((sourceRoot, destinationRoot));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (source, destination) = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
            {
                token.ThrowIfCancellationRequested();
                // Walking and stat-ing a huge world can take minutes before the
                // first byte moves; keep telling the peer we are alive.
                await heartbeat().ConfigureAwait(false);
                var name = Path.GetFileName(entry);
                if (string.Equals(name, "session.lock", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(destination, name);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"World contains an unsupported filesystem link: {entry}");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.CreateDirectory(target);
                    pending.Push((entry, target));
                }
                else
                {
                    totalBytes += new FileInfo(entry).Length;
                    files.Add((entry, target));
                }
            }
        }

        long processed = 0;
        var buffer = new byte[TransferCopyBufferBytes];
        foreach (var (source, target) in files)
        {
            token.ThrowIfCancellationRequested();
            await using (var input = new FileStream(
                source, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan))
            await using (var output = new FileStream(
                target, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    processed += read;
                    await progress(processed, totalBytes).ConfigureAwait(false);
                }
            }
            File.SetLastWriteTime(target, File.GetLastWriteTime(source));
        }
    }

    // Walking a large world can itself outlast the peer's idle timeout, so the
    // walk beats as it goes instead of after it finishes.
    private static async Task<List<string>> EnumerateFilesWithHeartbeatAsync(
        string fullRoot,
        Func<Task> heartbeat)
    {
        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
        {
            await heartbeat().ConfigureAwait(false);
            files.Add(file);
        }
        files.Sort((left, right) => string.CompareOrdinal(
            Path.GetRelativePath(fullRoot, left).Replace('\\', '/'),
            Path.GetRelativePath(fullRoot, right).Replace('\\', '/')));
        return files;
    }

    private static readonly HashSet<string> PrecompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mca", ".mcc", ".dat", ".dat_old", ".nbt", ".schem",
        ".png", ".jpg", ".jpeg", ".zip", ".jar", ".gz", ".ogg"
    };

    // Creates the archive and computes the directory hash in one disk pass.
    // The hash format must stay byte-identical to HashDirectoryAsync: the
    // receiver recomputes it over the extracted tree.
    internal static async Task<string> CreateWorldArchiveWithHashAsync(
        string sourceRoot,
        string archivePath,
        Func<long, long, Task> progress,
        Func<Task>? heartbeat,
        CancellationToken token)
    {
        heartbeat ??= () => Task.CompletedTask;
        var fullRoot = Path.GetFullPath(sourceRoot);
        var files = await EnumerateFilesWithHeartbeatAsync(fullRoot, heartbeat).ConfigureAwait(false);
        long totalBytes = 0;
        foreach (var file in files)
        {
            await heartbeat().ConfigureAwait(false);
            totalBytes += new FileInfo(file).Length;
        }

        long processed = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        // Directory entries carry no bytes and stay out of the hash, but an
        // empty folder in the world would otherwise be lost in transit.
        foreach (var directory in Directory.EnumerateDirectories(fullRoot, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            await heartbeat().ConfigureAwait(false);
            if (Directory.EnumerateFileSystemEntries(directory).Any()) continue;
            archive.CreateEntry(Path.GetRelativePath(fullRoot, directory).Replace('\\', '/') + "/");
        }

        var buffer = new byte[TransferCopyBufferBytes];
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(fullRoot, file).Replace('\\', '/');
            var relativeBytes = Encoding.UTF8.GetBytes(relativePath);
            AppendInt64(hash, relativeBytes.Length);
            hash.AppendData(relativeBytes);
            AppendInt64(hash, new FileInfo(file).Length);

            // Region and NBT files are already deflate/zlib-compressed;
            // recompressing them costs minutes of CPU for near-zero gain.
            var level = PrecompressedExtensions.Contains(Path.GetExtension(file))
                ? CompressionLevel.NoCompression
                : CompressionLevel.Fastest;
            var entry = archive.CreateEntry(relativePath, level);
            var lastWrite = File.GetLastWriteTime(file);
            if (lastWrite.Year is >= 1980 and <= 2107) entry.LastWriteTime = lastWrite;
            await using var input = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan);
            await using var output = entry.Open();
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
                processed += read;
                await progress(processed, totalBytes).ConfigureAwait(false);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static async Task ExtractWorldArchiveAsync(
        string archivePath,
        string destinationRoot,
        Func<long, long, Task> progress,
        Action<long>? checkDiskSpace,
        CancellationToken token,
        Func<Task>? heartbeat = null)
    {
        heartbeat ??= () => Task.CompletedTask;
        // Opening a huge archive parses its whole central directory, which can
        // outlast the peer's idle timeout on slow media.
        await Task.Yield();
        var openArchive = Task.Run(() => ZipFile.OpenRead(archivePath));
        try
        {
            while (!openArchive.IsCompleted)
            {
                await Task.WhenAny(openArchive, Task.Delay(ProgressHeartbeatInterval, token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!openArchive.IsCompleted) await heartbeat().ConfigureAwait(false);
            }
        }
        catch
        {
            // Abandoning the open would leak the handle on received.zip and
            // make the caller's cleanup delete fail; dispose it when it lands.
            _ = openArchive.ContinueWith(
                completed =>
                {
                    if (completed.Status == TaskStatus.RanToCompletion) completed.Result.Dispose();
                    else _ = completed.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }

        Directory.CreateDirectory(destinationRoot);
        var fullRoot = Path.GetFullPath(destinationRoot);
        var rootPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        using var archive = await openArchive.ConfigureAwait(false);
        // Entry streams are capped at the length each entry declares, so the
        // declared total bounds what extraction can write to disk. Confirm the
        // disk can hold that total before writing the first byte.
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            await heartbeat().ConfigureAwait(false);
            if (entry.Length < 0 || totalBytes > long.MaxValue - entry.Length)
            {
                throw new InvalidDataException("Received archive declares an implausible size.");
            }
            totalBytes += entry.Length;
        }
        checkDiskSpace?.Invoke(totalBytes);

        long processed = 0;
        var buffer = new byte[TransferCopyBufferBytes];
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var destination = Path.GetFullPath(Path.Combine(fullRoot, entry.FullName));
            if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Received archive contains an entry outside the world directory.");
            }
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var input = entry.Open())
            await using (var output = new FileStream(
                destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    processed += read;
                    await progress(processed, totalBytes).ConfigureAwait(false);
                }
            }
            File.SetLastWriteTime(destination, entry.LastWriteTime.LocalDateTime);
        }
    }

    private static string BuildPeerConnectionMessage(string peerName)
    {
        return $"""
        Не удалось связаться с игроком {peerName} через Steam.

        Проверьте на компьютере получателя:
        1. LANMinecraft.exe запущен, Steam запущен и вход выполнен.
        2. Вы у друг друга в друзьях Steam.
        3. Minecraft у получателя закрыт - во время игры мир принять нельзя.
        """;
    }

    private string ResolveWorldToSend(string worldPath)
    {
        Directory.CreateDirectory(_paths.Worlds);

        if (string.IsNullOrWhiteSpace(worldPath))
        {
            throw new DirectoryNotFoundException("Choose a world to transfer.");
        }

        var world = Path.GetFullPath(worldPath);
        _paths.EnsureUnderRoot(world);

        var worldsRoot = _paths.Worlds.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!world.StartsWith(worldsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Selected world must be inside ./Minecraft/Worlds.");
        }

        if (!IsMinecraftWorldDirectory(world))
        {
            throw new DirectoryNotFoundException("Selected folder is not a Minecraft world.");
        }

        _logger.Info($"Selected world for transfer: {Path.GetFileName(world)}.");
        return world;
    }

    private const string UnknownIdentityName = "\u043D\u0435\u0438\u0437\u0432\u0435\u0441\u0442\u043D\u043E";

    /// <summary>
    /// Who created a world, in order of authority: what the sender said, then
    /// what the local metadata remembers, then this machine. The Steam id rides
    /// along with the UUID it belongs to - never mixed between sources, because
    /// a Steam id attached to somebody else's UUID would mislabel ownership.
    /// </summary>
    private static (string id, string name, string steamId) ResolveOwnerIdentity(
        string? metadataOwnerId,
        string? metadataOwnerName,
        AppSettings settings,
        string? localOwnerId,
        string? localOwnerName,
        string? headerOwnerId = null,
        string? headerOwnerName = null,
        string? headerOwnerSteamId = null,
        string? metadataOwnerSteamId = null,
        string? localOwnerSteamId = null)
    {
        var resolvedHeaderId = !string.IsNullOrWhiteSpace(headerOwnerId) ? headerOwnerId.Trim() : null;
        var resolvedHeaderName = !string.IsNullOrWhiteSpace(headerOwnerName) ? headerOwnerName.Trim() : null;
        var resolvedLocalId = !string.IsNullOrWhiteSpace(localOwnerId) ? localOwnerId.Trim() : string.Empty;
        var resolvedLocalName = !string.IsNullOrWhiteSpace(localOwnerName) ? localOwnerName.Trim() : string.Empty;
        var resolvedLocalSteamId = !string.IsNullOrWhiteSpace(localOwnerSteamId) ? localOwnerSteamId.Trim() : string.Empty;

        if (!string.IsNullOrWhiteSpace(resolvedHeaderId) || !string.IsNullOrWhiteSpace(resolvedHeaderName))
        {
            var headerName = resolvedHeaderName;
            if (!string.IsNullOrWhiteSpace(resolvedLocalId) &&
                string.Equals(resolvedHeaderId, resolvedLocalId, StringComparison.OrdinalIgnoreCase))
            {
                headerName = string.IsNullOrWhiteSpace(resolvedLocalName) ? UnknownIdentityName : resolvedLocalName;
            }

            var headerSteamId = !string.IsNullOrWhiteSpace(headerOwnerSteamId)
                ? headerOwnerSteamId.Trim()
                : string.Empty;
            // The sender may not know their own Steam id yet; when the owner is
            // this machine we do.
            if (headerSteamId.Length == 0 &&
                resolvedLocalSteamId.Length != 0 &&
                string.Equals(resolvedHeaderId, resolvedLocalId, StringComparison.OrdinalIgnoreCase))
            {
                headerSteamId = resolvedLocalSteamId;
            }

            return (resolvedHeaderId ?? string.Empty, headerName ?? UnknownIdentityName, headerSteamId);
        }

        var resolvedMetadataId = !string.IsNullOrWhiteSpace(metadataOwnerId) ? metadataOwnerId.Trim() : null;
        var resolvedMetadataName = !string.IsNullOrWhiteSpace(metadataOwnerName) ? metadataOwnerName.Trim() : null;
        if (!string.IsNullOrWhiteSpace(resolvedMetadataId) || !string.IsNullOrWhiteSpace(resolvedMetadataName))
        {
            var metadataName = resolvedMetadataName;
            if (!string.IsNullOrWhiteSpace(resolvedLocalId) &&
                string.Equals(resolvedMetadataId, resolvedLocalId, StringComparison.OrdinalIgnoreCase))
            {
                metadataName = string.IsNullOrWhiteSpace(resolvedLocalName) ? UnknownIdentityName : resolvedLocalName;
            }

            var metadataSteamId = !string.IsNullOrWhiteSpace(metadataOwnerSteamId)
                ? metadataOwnerSteamId.Trim()
                : string.Empty;
            if (metadataSteamId.Length == 0 &&
                resolvedLocalSteamId.Length != 0 &&
                string.Equals(resolvedMetadataId, resolvedLocalId, StringComparison.OrdinalIgnoreCase))
            {
                metadataSteamId = resolvedLocalSteamId;
            }

            return (resolvedMetadataId ?? string.Empty, metadataName ?? UnknownIdentityName, metadataSteamId);
        }

        var settingsId = string.IsNullOrWhiteSpace(localOwnerId) ? string.Empty : localOwnerId.Trim();
        var settingsName = string.IsNullOrWhiteSpace(localOwnerName) ? UnknownIdentityName : localOwnerName.Trim();

        return (settingsId, settingsName, resolvedLocalSteamId);
    }

    public static bool IsMinecraftWorldDirectory(string path)
    {
        return Directory.Exists(path) && File.Exists(Path.Combine(path, "level.dat"));
    }

    private static DateTime GetWorldLastWriteTimeUtc(string path)
    {
        var levelDat = Path.Combine(path, "level.dat");
        return File.Exists(levelDat) ? File.GetLastWriteTimeUtc(levelDat) : Directory.GetLastWriteTimeUtc(path);
    }

    private string GetAvailableWorldDirectory(string safeWorldName)
    {
        var basePath = Path.Combine(_paths.Worlds, safeWorldName);
        if (!Directory.Exists(basePath) && !File.Exists(basePath))
        {
            return basePath;
        }

        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(_paths.Worlds, $"{safeWorldName} ({index})");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static string GetSafeWorldName(string? worldName)
    {
        if (string.IsNullOrWhiteSpace(worldName))
        {
            return "World";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var safe = new string(worldName.Trim()
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "World" : safe;
    }

    private void DeleteTransferredWorld(string worldDir)
    {
        _paths.EnsureUnderRoot(worldDir);
        var worldsRoot = _paths.Worlds.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullWorldDir = Path.GetFullPath(worldDir);
        if (!fullWorldDir.StartsWith(worldsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to delete a world outside ./Minecraft/Worlds.");
        }

        try
        {
            Directory.Delete(fullWorldDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("World was transferred and verified, but the local source world could not be deleted. Close Minecraft and retry the transfer so only one host remains.", ex);
        }
    }

    internal static async Task<string> HashDirectoryAsync(
        string root,
        Func<long, long, Task>? progress,
        CancellationToken token,
        Func<Task>? heartbeat = null)
    {
        heartbeat ??= () => Task.CompletedTask;
        var fullRoot = Path.GetFullPath(root);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = await EnumerateFilesWithHeartbeatAsync(fullRoot, heartbeat).ConfigureAwait(false);
        long totalBytes = 0;
        foreach (var file in files)
        {
            await heartbeat().ConfigureAwait(false);
            totalBytes += new FileInfo(file).Length;
        }

        long processed = 0;
        var buffer = new byte[TransferCopyBufferBytes];
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(fullRoot, file).Replace('\\', '/');
            var relativeBytes = Encoding.UTF8.GetBytes(relativePath);
            AppendInt64(hash, relativeBytes.Length);
            hash.AppendData(relativeBytes);

            var fileInfo = new FileInfo(file);
            AppendInt64(hash, fileInfo.Length);

            await using var stream = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
                processed += read;
                if (progress is not null) await progress(processed, totalBytes).ConfigureAwait(false);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static async Task CopyExactlyWithProgressAsync(
        Stream input,
        Stream output,
        long size,
        Action<long> progress,
        TimeSpan idleTimeout,
        CancellationToken token)
    {
        var buffer = new byte[TransferCopyBufferBytes];
        long total = 0;
        while (total < size)
        {
            int read;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                idle.CancelAfter(idleTimeout);
                try
                {
                    read = await input.ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - total)),
                        idle.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new TimeoutException("The other player stopped sending the world.");
                }
            }
            if (read == 0) throw new EndOfStreamException("Transfer ended early.");
            total += read;
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            progress(total);
        }
    }

    private static async Task CopyWithProgressAsync(
        Stream input,
        Stream output,
        long totalSize,
        Action<long> progress,
        TimeSpan idleTimeout,
        CancellationToken token)
    {
        var buffer = new byte[TransferCopyBufferBytes];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, totalSize - total)), token);
            if (read <= 0) break;
            total += read;
            if (total > totalSize) throw new InvalidOperationException("Transfer size exceeds expected archive size.");
            // A receiver that stopped draining keeps the socket alive through
            // zero-window probes, so bound the write the same way as the read.
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                idle.CancelAfter(idleTimeout);
                try
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), idle.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new TimeoutException("The other player stopped receiving the world.");
                }
            }
            progress(total);
        }

        if (total != totalSize)
        {
            throw new InvalidOperationException("Transfer data size mismatch.");
        }
    }

    private async Task WriteJsonAsync<T>(Stream stream, T value, CancellationToken token)
    {
        await PortableProtocol.WriteJsonAsync(stream, value, _jsonOptions, token).ConfigureAwait(false);
    }

    private async Task<T?> ReadJsonAsync<T>(Stream stream, CancellationToken token)
    {
        var bytes = await PortableProtocol.ReadFrameAsync(stream, token).ConfigureAwait(false);
        return PortableProtocol.Deserialize<T>(bytes, _jsonOptions);
    }

    private static void DeleteFileIfExists(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
    }

    private static void DeleteDirectoryIfExists(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            disposeTask = _disposeTask;
        }
        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposeState, 1);
        StopAcceptingIncomingTransfers();
        _shutdownCts.Cancel();
        await WaitForIncomingTransfersAsync().ConfigureAwait(false);
        await _transferGate.WaitAsync().ConfigureAwait(false);
        _transferGate.Release();
        // Give background transaction cleanup a moment to finish so temp files
        // usually disappear before the process exits; anything still running is
        // swept by WorldTransferRecoveryService on the next launch.
        var cleanupTasks = _cleanupTasks.Values.ToArray();
        if (cleanupTasks.Length > 0)
        {
            await Task.WhenAny(
                Task.WhenAll(cleanupTasks),
                Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }
        _shutdownCts.Dispose();
    }
}

public sealed record WorldTransferProgress(bool IsActive, long Current, long Total, string Stage = "");

/// <summary>What the receiving player is asked to accept or decline.</summary>
public sealed record WorldTransferOffer(
    SteamId64 SenderSteamId,
    string SenderPersonaName,
    string SenderPlayerName,
    string WorldName,
    long ArchiveBytes);

/// <summary>
/// Asks the receiving player about an incoming world. Implemented by the window
/// with a dialog; a launcher without one accepts, which is what the tests use.
/// </summary>
public interface IWorldTransferConfirmation
{
    Task<bool> ConfirmAsync(WorldTransferOffer offer, CancellationToken token);
}

public sealed class WorldTransferRuntimeOptions
{
    public TimeSpan PeerIdleTimeout { get; init; } = WorldTransferService.DefaultPeerIdleTimeout;

    internal void Validate()
    {
        if (PeerIdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PeerIdleTimeout), "Peer idle timeout must be positive.");
        }
    }
}
