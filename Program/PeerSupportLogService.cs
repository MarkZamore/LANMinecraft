using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Minecraft;

internal sealed class PeerSupportLogService : IAsyncDisposable, IPortableProtocolHandler
{
    private const int FirstDynamicStreamId = 100;
    private const int MaxSpoolPayloadBytes =
        SupportLogSpool.MaxRecordBytes - SpoolEnvelopeHeaderSize;
    private const int SpoolEnvelopeHeaderSize = 25;
    private const int MaxIncomingConnections = 16;
    private const int MaxIncomingSessions = 32;
    // The byte limiter is what actually bounds a peer; this only stops a flood
    // of tiny frames, so it has to sit well above the frame rate a sender
    // reaches while shaped to SupportLogRateLimiter.DefaultBytesPerSecond.
    private const int MaxFramesPerSecond = 1024;
    private const int MaxIncomingSessionsPerPeer = 2;
    private const int MaxManifestStreams = 512;
    private const int CollectorQueueCapacity = 4;
    private static readonly TimeSpan PeerTtl = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FrameIdleTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FrameWriteTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MetricsInterval = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly AppPaths _paths;
    private readonly IPeerTransport _transport;
    private readonly Func<(string id, string name)> _identityProvider;
    private readonly Func<string?> _instanceDirectoryProvider;
    private readonly Func<CancellationToken, Task<SupportEnvironmentSnapshot>>
        _environmentProvider;
    private readonly Func<SupportNetworkMetrics> _metricsProvider;
    private readonly SupportLogSanitizer _sanitizer;
    private readonly SupportLogStorage _storage;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, ObservedPeer> _peers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IncomingSessionState> _incoming =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _incomingCreationGate = new(1, 1);
    private readonly SemaphoreSlim _incomingConnectionGate =
        new(MaxIncomingConnections, MaxIncomingConnections);
    private readonly SemaphoreSlim _targetChangeGate = new(1, 1);
    private readonly SupportLogRateLimiter _receiveLimiter;
    private readonly Task _maintenanceTask;
    private readonly object _targetGate = new();

    private OutgoingSession? _target;
    private string _statusText = "Передача логов выключена.";
    private long _bytesSent;
    private long _bytesReceived;
    private long _reconnects;
    private long _decodeErrors;
    private long _targetRequestVersion;
    private int _statusRefreshPending;
    private int _disposed;

    public PeerSupportLogService(
        AppPaths paths,
        IPeerTransport transport,
        Func<(string id, string name)> identityProvider,
        Func<string?> instanceDirProvider,
        Func<CancellationToken, Task<SupportEnvironmentSnapshot>>
            environmentProvider,
        Func<SupportNetworkMetrics> metricsProvider,
        SupportLogStorage? storage = null,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _identityProvider =
            identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        _instanceDirectoryProvider = instanceDirProvider ??
            throw new ArgumentNullException(nameof(instanceDirProvider));
        _environmentProvider = environmentProvider ??
            throw new ArgumentNullException(nameof(environmentProvider));
        _metricsProvider =
            metricsProvider ?? throw new ArgumentNullException(nameof(metricsProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sanitizer = SupportLogSanitizer.CreateDefault(paths);
        _storage = storage ?? new SupportLogStorage(paths, _sanitizer, _timeProvider);
        _receiveLimiter = new SupportLogRateLimiter(
            SupportLogRateLimiter.DefaultBytesPerSecond,
            _timeProvider);
        PruneOrphanSpools();
        _maintenanceTask = RunMaintenanceAsync();
    }

    public event Action? StateChanged;

    /// <summary>The first frame of an incoming diagnostics connection names this.</summary>
    public string ProtocolName => PeerSupportProtocol.UpgradeProtocolName;

    Task IPortableProtocolHandler.HandleAsync(
        Stream stream,
        byte[] initialFrame,
        PeerConnectionContext context,
        CancellationToken token) =>
        HandleIncomingAsync(stream, initialFrame, context, token);

    public string CurrentTargetIdentityId
    {
        get
        {
            lock (_targetGate) return _target?.Peer.IdentityId ?? string.Empty;
        }
    }

    public string StatusText => Volatile.Read(ref _statusText);

    public bool HasReceivedLogs
    {
        get
        {
            try
            {
                return _storage.HasReceivedLogs;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Records something the launcher did that matters to whoever reads the
    /// bundle later. The VPN era also carried addresses and the chosen adapter;
    /// under Steam a peer is only an account.
    /// </summary>
    public void PublishRuntimeEvent(
        string type,
        string peerIdentityId,
        string reason,
        IReadOnlyDictionary<string, string>? details = null,
        DateTimeOffset? atUtc = null)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        OutgoingSession? target;
        lock (_targetGate) target = _target;
        target?.TryPublishEvent(new SupportTransportEvent(
            atUtc ?? _timeProvider.GetUtcNow(),
            SanitizeRuntimeField(
                _sanitizer,
                "type",
                string.IsNullOrWhiteSpace(type) ? "runtime" : type),
            SanitizeRuntimeField(
                _sanitizer,
                "peerIdentityId",
                NormalizeIdentity(peerIdentityId)),
            SanitizeRuntimeField(_sanitizer, "reason", reason),
            SanitizeRuntimeDetails(_sanitizer, details)));
    }

    internal static string SanitizeRuntimeField(
        SupportLogSanitizer sanitizer,
        string fieldName,
        string? value)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        var normalizedName = string.IsNullOrWhiteSpace(fieldName)
            ? "field"
            : fieldName.Trim();
        var normalizedValue = value?.Trim() ?? string.Empty;
        var prefix = normalizedName + ": ";
        var contextual = sanitizer.SanitizeLine(prefix + normalizedValue);
        if (contextual is null)
        {
            return "<REMOVED_USER_CONTENT>";
        }
        return contextual.StartsWith(prefix, StringComparison.Ordinal)
            ? contextual[prefix.Length..]
            : sanitizer.SanitizeMetadataValue(normalizedValue);
    }

    internal static IReadOnlyDictionary<string, string>? SanitizeRuntimeDetails(
        SupportLogSanitizer sanitizer,
        IReadOnlyDictionary<string, string>? details)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        if (details is null) return null;

        var sanitized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in details)
        {
            var key = SanitizeRuntimeField(sanitizer, "detailName", pair.Key);
            if (key.Length == 0)
            {
                key = "detail";
            }
            var value = SanitizeRuntimeField(
                sanitizer,
                key,
                pair.Value);
            if (string.Equals(
                    value,
                    "<REMOVED_USER_CONTENT>",
                    StringComparison.Ordinal))
            {
                continue;
            }
            sanitized[key] = value;
        }
        return sanitized;
    }

    /// <summary>
    /// Takes a friend's rich presence as the announcement it replaces: it says
    /// whether they run a launcher that speaks this diagnostics version, and
    /// under which name to show them.
    /// </summary>
    public void ObservePeer(SteamPeerPresence presence)
    {
        ArgumentNullException.ThrowIfNull(presence);
        if (Volatile.Read(ref _disposed) != 0) return;

        var identityId = presence.SteamId.ToString();
        if (identityId.Length == 0) return;

        if (presence.DiagnosticProtocolVersion != PeerSupportProtocol.ProtocolVersion)
        {
            _peers.TryRemove(identityId, out _);
            CancelTargetIfMatches(identityId, "peer_incompatible");
            return;
        }

        var observed = new ObservedPeer(
            presence.SteamId,
            NormalizePlayerName(
                presence.PlayerName.Length != 0 ? presence.PlayerName : presence.PersonaName),
            _timeProvider.GetUtcNow());
        var changed = !_peers.TryGetValue(identityId, out var previous) ||
                      !previous.DescribesSamePeer(observed);
        _peers[identityId] = observed;

        if (!changed) return;

        OutgoingSession? target;
        lock (_targetGate) target = _target;
        if (target is not null &&
            string.Equals(target.Peer.IdentityId, identityId, StringComparison.Ordinal))
        {
            target.Peer = observed;
            target.TryPublishEvent(new SupportTransportEvent(
                _timeProvider.GetUtcNow(),
                "peer_presence_observed",
                identityId,
                "discovery"));
        }
        RaiseStateChanged();
    }

    /// <summary>
    /// Called when Steam goes away (client closed, signed out, API lost). The
    /// peer list is only meaningful while Steam can authenticate accounts, so
    /// everything in flight stops instead of retrying into a dead transport.
    /// </summary>
    public async Task OnTransportUnavailableAsync(
        string reason,
        CancellationToken token = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "transport_unavailable" : reason;

        Interlocked.Increment(ref _targetRequestVersion);
        CancelCurrentTarget(normalizedReason);
        _peers.Clear();
        foreach (var incoming in _incoming.Values.ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (!_incoming.TryRemove(incoming.Key, out _))
            {
                continue;
            }
            try
            {
                await incoming.CompleteAsync(normalizedReason, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or
                                       UnauthorizedAccessException or
                                       InvalidOperationException)
            {
                Interlocked.Increment(ref _decodeErrors);
            }
        }
        SetStatus("Steam недоступен; передача логов остановлена.");
    }

    public async Task SetTargetAsync(
        DiagnosticLogTargetOption option,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(option);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        token.ThrowIfCancellationRequested();
        var normalized = option.SteamId.ToString();
        var requestVersion = Interlocked.Increment(ref _targetRequestVersion);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(
            token,
            _shutdown.Token);
        await _targetChangeGate.WaitAsync(wait.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (requestVersion != Volatile.Read(ref _targetRequestVersion))
            {
                return;
            }

            OutgoingSession? previous;
            lock (_targetGate)
            {
                previous = _target;
                _target = null;
            }
            if (previous is not null)
            {
                PublishStopEvent(previous, "target_changed");
                previous.RequestStop("target_changed", _timeProvider.GetUtcNow());
                await StopSessionWithinDeadlineAsync(previous, token)
                    .ConfigureAwait(false);
            }

            if (requestVersion != Volatile.Read(ref _targetRequestVersion))
            {
                return;
            }
            if (normalized.Length == 0)
            {
                SetStatusFromSessions();
                return;
            }

            if (!TryGetCurrentPeer(normalized, out var peer))
            {
                throw new InvalidOperationException(
                    "Выбранный игрок больше не доступен для передачи логов.");
            }
            var session = new OutgoingSession(
                peer,
                Guid.NewGuid(),
                _timeProvider.GetUtcNow(),
                CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
            lock (_targetGate)
            {
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref _disposed) != 0,
                    this);
                if (requestVersion != Volatile.Read(ref _targetRequestVersion))
                {
                    session.ForceCancel();
                    return;
                }
                _target = session;
                session.Task = RunOutgoingSessionAsync(session);
            }
            SetStatus($"Подготовка логов для {peer.PlayerName}…");
        }
        finally
        {
            _targetChangeGate.Release();
        }
    }

    public async Task HandleIncomingAsync(
        Stream stream,
        byte[] initialFrame,
        PeerConnectionContext connection,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(initialFrame);
        ArgumentNullException.ThrowIfNull(connection);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!await _incomingConnectionGate.WaitAsync(0, token).ConfigureAwait(false))
        {
            throw new InvalidDataException(
                "Too many diagnostics connections are already active.");
        }
        try
        {
            PeerSupportProtocol.ReadUpgrade(initialFrame);
            ValidateIncomingConnectionContext(connection);

            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshake.CancelAfter(HandshakeTimeout);

            var helloFrame = await ReadFrameWithTimeoutAsync(
                stream,
                HandshakeTimeout,
                handshake.Token).ConfigureAwait(false);
            if (helloFrame.Type != PeerSupportFrameType.Hello ||
                helloFrame.LogicalStreamId != 0 ||
                helloFrame.Sequence != 1 ||
                helloFrame.Ack != 0)
            {
                throw new InvalidDataException("The diagnostics hello frame is invalid.");
            }
            var hello = PeerSupportProtocol.DeserializeJson<PeerSupportHello>(
                helloFrame.Payload);
            var peer = ValidateIncomingHello(hello, connection);
            var incoming = await GetOrCreateIncomingSessionAsync(
                hello,
                peer,
                connection,
                handshake.Token).ConfigureAwait(false);

            if (!incoming.IsActive)
            {
                await TryWriteTerminalErrorAsync(
                    stream,
                    incoming.HighestAcceptedSequence,
                    "diagnostic_session_is_no_longer_active",
                    token).ConfigureAwait(false);
                _incoming.TryRemove(incoming.Key, out _);
                return;
            }
            if (!await incoming.ConnectionGate.WaitAsync(0, token).ConfigureAwait(false))
            {
                throw new InvalidDataException(
                    "Another connection is already writing this diagnostics session.");
            }

            var generation = incoming.BeginConnection();
            try
            {
                var accepted = new PeerSupportResumeState(
                    hello.SessionId,
                    incoming.HighestAcceptedSequence);
                await WriteFrameWithTimeoutAsync(
                    stream,
                    new PeerSupportFrame(
                        PeerSupportFrameType.HelloAccepted,
                        0,
                        1,
                        incoming.HighestAcceptedSequence,
                        PeerSupportProtocol.SerializeJson(accepted)),
                    handshake.Token).ConfigureAwait(false);

                try
                {
                    await ReceiveFramesAsync(
                        stream,
                        hello,
                        peer,
                        connection,
                        incoming,
                        token).ConfigureAwait(false);
                }
                catch (SupportLogStorageLimitException ex)
                {
                    var reason = "receiver_storage_limit: " +
                                 _sanitizer.SanitizeMetadataValue(ex.Reason);
                    await TryWriteTerminalErrorAsync(
                        stream,
                        incoming.HighestAcceptedSequence,
                        reason,
                        token).ConfigureAwait(false);
                    if (incoming.IsActive)
                    {
                        await incoming.CompleteAsync(
                            reason,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    _incoming.TryRemove(incoming.Key, out _);
                    SetStatus($"Получение логов остановлено: {reason}");
                }
            }
            finally
            {
                incoming.EndConnection(generation);
                incoming.ConnectionGate.Release();
                if (incoming.IsActive)
                {
                    _ = CompleteDisconnectedIncomingAsync(incoming, generation);
                }
                else
                {
                    _incoming.TryRemove(incoming.Key, out _);
                }
                SetStatusFromSessions();
            }
        }
        finally
        {
            _incomingConnectionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Increment(ref _targetRequestVersion);

        OutgoingSession? target;
        lock (_targetGate)
        {
            target = _target;
            _target = null;
        }
        if (target is not null)
        {
            PublishStopEvent(target, "launcher_shutdown");
            target.RequestStop("launcher_shutdown", _timeProvider.GetUtcNow());
            await StopSessionWithinDeadlineAsync(target, CancellationToken.None)
                .ConfigureAwait(false);
        }
        _shutdown.Cancel();
        await ObserveTaskAsync(_maintenanceTask).ConfigureAwait(false);

        foreach (var incoming in _incoming.Values)
        {
            try
            {
                await incoming.Storage.CompleteAsync(
                    "launcher_shutdown",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        _incoming.Clear();
        _incomingCreationGate.Dispose();
        _incomingConnectionGate.Dispose();
        _targetChangeGate.Dispose();
        _shutdown.Dispose();
    }

    private async Task RunMaintenanceAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await Task.Delay(
                    TimeSpan.FromHours(1),
                    _timeProvider,
                    _shutdown.Token).ConfigureAwait(false);
                try
                {
                    await _storage.PruneExpiredAsync(_shutdown.Token)
                        .ConfigureAwait(false);
                    PruneOrphanSpools();
                }
                catch (Exception ex) when (ex is IOException or
                                           UnauthorizedAccessException)
                {
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task RunOutgoingSessionAsync(OutgoingSession session)
    {
        string? terminalStatus = null;
        try
        {
            var token = session.Token;
            var environment = await _environmentProvider(token).ConfigureAwait(false);
            var spool = new SupportLogSpool(
                _paths,
                session.SessionId,
                _storage);
            session.Spool = spool;
            var manifest = CreateManifest(session, environment);
            session.Manifest = manifest;

            await EnqueueFrameAsync(
                session,
                PeerSupportFrameType.Manifest,
                0,
                PeerSupportProtocol.SerializeJson(manifest),
                token).ConfigureAwait(false);
            var environmentJson = JsonSerializer.Serialize(environment, JsonOptions);
            await EnqueueTextAsync(
                session,
                PeerSupportFrameType.Data,
                1,
                _sanitizer.SanitizeText(environmentJson) + Environment.NewLine,
                token).ConfigureAwait(false);

            await using var collector = new SupportLogCollector(
                _paths,
                _sanitizer,
                _instanceDirectoryProvider,
                _timeProvider,
                CollectorQueueCapacity);
            var producerTask = RunProducerGroupAsync(session, collector);
            var senderTask = SendSpoolAsync(session, token);
            await Task.WhenAll(producerTask, senderTask).ConfigureAwait(false);
            terminalStatus = session.FailureStatus;
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested)
        {
            terminalStatus = session.FailureStatus;
        }
        catch (SupportLogStorageLimitException ex)
        {
            terminalStatus = $"Передача логов остановлена: {ex.Reason}";
        }
        catch (Exception ex) when (ex is IOException or
                                   InvalidDataException or
                                   InvalidOperationException or
                                   UnauthorizedAccessException)
        {
            terminalStatus =
                "Передача диагностических логов остановлена: " +
                _sanitizer.SanitizeMetadataValue(ex.Message);
        }
        finally
        {
            session.ForceCancel();
            if (session.Spool is not null)
            {
                try
                {
                    await session.Spool.AckThroughAsync(
                        ulong.MaxValue,
                        CancellationToken.None).ConfigureAwait(false);
                    session.CollectorWindow.AcknowledgeThrough(ulong.MaxValue);
                    await session.Spool.DeleteIfEmptyAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }
            lock (_targetGate)
            {
                if (ReferenceEquals(_target, session))
                {
                    _target = null;
                }
            }
            session.DisposeCancellation();
            if (string.IsNullOrWhiteSpace(terminalStatus))
            {
                SetStatusFromSessions();
            }
            else
            {
                SetStatus(terminalStatus);
            }
        }
    }

    private async Task RunProducerGroupAsync(
        OutgoingSession session,
        SupportLogCollector collector)
    {
        Task[] tasks = [];
        try
        {
            var token = session.ProducerToken;
            tasks =
            [
                PumpCollectorAsync(session, collector, token),
                PumpMetricsAsync(session, token),
                PumpEventsAsync(session, token)
            ];
            var first = await Task.WhenAny(tasks).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            if (!session.StopRequested)
            {
                session.FailureStatus =
                    "Передача логов остановлена: источники диагностики завершились.";
                session.RequestStop(
                    "diagnostic_sources_completed",
                    _timeProvider.GetUtcNow());
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.ProducerToken.IsCancellationRequested)
        {
        }
        catch (SupportLogStorageLimitException ex)
        {
            session.FailureStatus = $"Передача логов остановлена: {ex.Reason}";
            session.RequestStop(
                "sender_storage_limit: " +
                _sanitizer.SanitizeMetadataValue(ex.Reason),
                _timeProvider.GetUtcNow());
        }
        catch (Exception ex) when (ex is IOException or
                                   InvalidDataException or
                                   InvalidOperationException or
                                   UnauthorizedAccessException)
        {
            session.FailureStatus =
                "Передача диагностических логов остановлена: " +
                _sanitizer.SanitizeMetadataValue(ex.Message);
            session.RequestStop(
                "sender_collector_error",
                _timeProvider.GetUtcNow());
        }
        finally
        {
            if (tasks.Length > 0)
            {
                session.CancelProducers();
                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch
                {
                }
            }
            session.MarkProducersCompleted();
            session.SignalData();
        }
    }

    /// <summary>
    /// Turns collected log lines into frames. The collector emits one item per
    /// line, and a game log writes hundreds of lines a second, so lines from
    /// the same source are batched into one frame: a frame per line put the
    /// sender over the receiver's cadence guard within seconds, which is what
    /// cut every game run short in the field.
    /// </summary>
    private async Task PumpCollectorAsync(
        OutgoingSession session,
        SupportLogCollector collector,
        CancellationToken token)
    {
        var batch = new CollectorBatch(this, session);
        await foreach (var item in collector.ReadAllAsync(token).ConfigureAwait(false))
        {
            if (item.Kind is not (SupportLogCollectorItemKind.Content or
                                  SupportLogCollectorItemKind.SourceReset))
            {
                await batch.FlushAsync(token).ConfigureAwait(false);
            }

            switch (item.Kind)
            {
                case SupportLogCollectorItemKind.SourceOpened:
                {
                    var streamId = session.GetOrAddStream(
                        item.SourceId,
                        () => checked((uint)Interlocked.Increment(ref session.LastStreamId)));
                    var protocolKind = ToProtocolKind(item.SourceKind);
                    session.AddManifestStream(new PeerSupportManifestStream(
                        streamId,
                        protocolKind,
                        item.LogicalName,
                        null,
                        item.CreatedAtUtc));
                    await EnqueueFrameAsync(
                        session,
                        PeerSupportFrameType.Manifest,
                        0,
                        PeerSupportProtocol.SerializeJson(session.Manifest!),
                        token,
                        collectorFrame: true).ConfigureAwait(false);
                    break;
                }
                case SupportLogCollectorItemKind.Content:
                case SupportLogCollectorItemKind.SourceReset:
                {
                    if (!session.TryGetStream(item.SourceId, out var streamId))
                    {
                        throw new InvalidDataException(
                            "A diagnostic source produced data before registration.");
                    }
                    await batch.AppendAsync(streamId, item.Text, token).ConfigureAwait(false);
                    // Nothing else is waiting: send what is buffered rather
                    // than holding a partial batch while the log is quiet.
                    if (!collector.Items.TryPeek(out _))
                    {
                        await batch.FlushAsync(token).ConfigureAwait(false);
                    }
                    break;
                }
                case SupportLogCollectorItemKind.SnapshotCompleted:
                case SupportLogCollectorItemKind.Warning:
                    await EnqueueEventAsync(
                        session,
                        new SupportCollectorEvent(
                            item.CreatedAtUtc,
                            item.Kind.ToString(),
                            item.SourceId,
                            item.LogicalName,
                            item.Text.Trim()),
                        token,
                        collectorFrame: true).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>
    /// Collects consecutive lines from one source into a single frame. It is
    /// flushed when the source changes, when the buffer reaches a frame's
    /// worth of text, or as soon as the collector has nothing more to hand
    /// over - so a busy log is batched and a quiet one is still immediate.
    /// </summary>
    private sealed class CollectorBatch(PeerSupportLogService owner, OutgoingSession session)
    {
        private const int FlushThresholdBytes = 32 * 1024;

        private readonly StringBuilder _text = new();
        private uint _streamId;

        public async Task AppendAsync(uint streamId, string text, CancellationToken token)
        {
            if (text.Length == 0) return;
            if (_text.Length > 0 && streamId != _streamId)
            {
                await FlushAsync(token).ConfigureAwait(false);
            }
            _streamId = streamId;
            _text.Append(text);
            if (_text.Length >= FlushThresholdBytes)
            {
                await FlushAsync(token).ConfigureAwait(false);
            }
        }

        public async Task FlushAsync(CancellationToken token)
        {
            if (_text.Length == 0) return;
            var payload = _text.ToString();
            _text.Clear();
            await owner.EnqueueTextAsync(
                session,
                PeerSupportFrameType.Data,
                _streamId,
                payload,
                token,
                collectorFrame: true).ConfigureAwait(false);
        }
    }

    private async Task PumpMetricsAsync(
        OutgoingSession session,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var metrics = _metricsProvider() with
            {
                DiagnosticBytesSent = Interlocked.Read(ref _bytesSent),
                DiagnosticBytesReceived = Interlocked.Read(ref _bytesReceived),
                DiagnosticReconnects = Interlocked.Read(ref _reconnects),
                DiagnosticDecodeErrors = Interlocked.Read(ref _decodeErrors)
            };
            var json = _sanitizer.SanitizeLine(
                JsonSerializer.Serialize(metrics, JsonOptions));
            if (json is not null)
            {
                await EnqueueTextAsync(
                    session,
                    PeerSupportFrameType.Data,
                    3,
                    json + Environment.NewLine,
                    token).ConfigureAwait(false);
            }
            await Task.Delay(MetricsInterval, _timeProvider, token).ConfigureAwait(false);
        }
    }

    private async Task PumpEventsAsync(
        OutgoingSession session,
        CancellationToken token)
    {
        try
        {
            await foreach (var value in session.Events.Reader.ReadAllAsync(token)
                               .ConfigureAwait(false))
            {
                await EnqueueEventAsync(session, value, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        while (session.Events.Reader.TryRead(out var pending) &&
               !session.Token.IsCancellationRequested)
        {
            await EnqueueEventAsync(session, pending, session.Token)
                .ConfigureAwait(false);
        }
    }

    private Task EnqueueEventAsync<T>(
        OutgoingSession session,
        T value,
        CancellationToken token,
        bool collectorFrame = false)
    {
        var json = _sanitizer.SanitizeLine(JsonSerializer.Serialize(value, JsonOptions));
        return json is null
            ? Task.CompletedTask
            : EnqueueTextAsync(
                session,
                PeerSupportFrameType.Data,
                2,
                json + Environment.NewLine,
                token,
                collectorFrame);
    }

    private async Task EnqueueTextAsync(
        OutgoingSession session,
        PeerSupportFrameType type,
        uint streamId,
        string text,
        CancellationToken token,
        bool collectorFrame = false)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length == 0) return;
        for (var offset = 0; offset < bytes.Length;)
        {
            var count = Math.Min(MaxSpoolPayloadBytes, bytes.Length - offset);
            while (offset + count < bytes.Length &&
                   count > 0 &&
                   (bytes[offset + count] & 0xC0) == 0x80)
            {
                count--;
            }
            if (count == 0)
            {
                throw new InvalidDataException(
                    "A diagnostics UTF-8 character exceeds the frame limit.");
            }
            await EnqueueFrameAsync(
                session,
                type,
                streamId,
                bytes.AsMemory(offset, count),
                token,
                collectorFrame).ConfigureAwait(false);
            offset += count;
        }
    }

    private static async Task EnqueueFrameAsync(
        OutgoingSession session,
        PeerSupportFrameType type,
        uint streamId,
        ReadOnlyMemory<byte> payload,
        CancellationToken token,
        bool collectorFrame = false)
    {
        var spool = session.Spool ??
            throw new InvalidOperationException("The diagnostics spool is unavailable.");
        if (payload.Length > MaxSpoolPayloadBytes)
        {
            throw new InvalidDataException("The diagnostics spool payload is too large.");
        }
        var collectorReservationBytes =
            checked(payload.Length + SpoolEnvelopeHeaderSize);
        var collectorReservationState = 0;
        await session.CollectorWindow.ReserveIfCollectorAsync(
                collectorFrame,
                collectorReservationBytes,
                token)
            .ConfigureAwait(false);
        if (collectorFrame) collectorReservationState = 1;
        try
        {
            await session.EnqueueGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var sequenceValue = Interlocked.Increment(ref session.LastSequence);
                var sequence = checked((ulong)sequenceValue);
                try
                {
                    if (collectorFrame)
                    {
                        session.CollectorWindow.Commit(
                            sequence,
                            collectorReservationBytes);
                        collectorReservationState = 2;
                    }
                    await spool.EnqueueAsync(
                        sequence,
                        EncodeSpoolEnvelope(
                            type,
                            streamId,
                            sequence,
                            0,
                            payload.Span),
                        token).ConfigureAwait(false);
                    session.SignalData();
                }
                catch
                {
                    if (collectorReservationState == 2)
                    {
                        session.CollectorWindow.Remove(sequence);
                        collectorReservationState = 3;
                    }
                    _ = Interlocked.CompareExchange(
                        ref session.LastSequence,
                        sequenceValue - 1,
                        sequenceValue);
                    throw;
                }
            }
            finally
            {
                session.EnqueueGate.Release();
            }
        }
        finally
        {
            if (collectorReservationState == 1)
            {
                session.CollectorWindow.CancelReservation(
                    collectorReservationBytes);
            }
        }
    }

    private async Task SendSpoolAsync(
        OutgoingSession session,
        CancellationToken token)
    {
        var limiter = new SupportLogRateLimiter(
            SupportLogRateLimiter.DefaultBytesPerSecond,
            _timeProvider);
        while (!token.IsCancellationRequested)
        {
            if (!TryGetCurrentPeer(session.Peer.IdentityId, out var peer))
            {
                if (!session.StopRequested)
                {
                    PublishStopEvent(session, "peer_stale");
                }
                session.RequestStop("peer_stale", _timeProvider.GetUtcNow());
                session.ForceCancel();
                break;
            }
            session.Peer = peer;

            try
            {
                await SendSpoolConnectionAsync(
                    session,
                    peer,
                    limiter,
                    token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or
                                       InvalidDataException or
                                       InvalidOperationException)
            {
                if (session.StopRequested &&
                    session.StopRequestedAtUtc is { } stoppedAt &&
                    _timeProvider.GetUtcNow() - stoppedAt >= GracefulStopTimeout)
                {
                    session.ForceCancel();
                    break;
                }
                Interlocked.Increment(ref _reconnects);
                if (!session.StopRequested)
                {
                    SetStatus($"Повторное подключение к {peer.PlayerName}…");
                }
                await Task.Delay(ReconnectDelay, _timeProvider, token)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task SendSpoolConnectionAsync(
        OutgoingSession session,
        ObservedPeer peer,
        SupportLogRateLimiter limiter,
        CancellationToken token)
    {
        if (!_transport.IsAvailable)
        {
            throw new InvalidOperationException(_transport.UnavailableReason);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ConnectTimeout);
        await using var connection = await _transport
            .ConnectAsync(peer.SteamId, PeerSupportProtocol.UpgradeProtocolName, timeout.Token)
            .ConfigureAwait(false);
        var stream = connection.Stream;
        await PeerSupportProtocol.WriteUpgradeAsync(stream, timeout.Token)
            .ConfigureAwait(false);

        var localIdentity = GetLocalIdentity();
        var hello = new PeerSupportHello
        {
            SessionId = session.SessionId,
            SenderIdentityId = localIdentity.id,
            RecipientIdentityId = peer.IdentityId,
            StartedAtUtc = session.StartedAtUtc,
            ResumeAfterSequence = session.LastAcknowledged
        };
        await WriteFrameWithTimeoutAsync(
            stream,
                new PeerSupportFrame(
                    PeerSupportFrameType.Hello,
                    0,
                    1,
                    0,
                    PeerSupportProtocol.SerializeJson(hello)),
            timeout.Token).ConfigureAwait(false);
        var acceptedFrame = await ReadFrameWithTimeoutAsync(
            stream,
            HandshakeTimeout,
            timeout.Token).ConfigureAwait(false);
        if (acceptedFrame.Type == PeerSupportFrameType.Error)
        {
            StopFromRemoteError(session, acceptedFrame);
            return;
        }
        if (acceptedFrame.Type != PeerSupportFrameType.HelloAccepted ||
            acceptedFrame.LogicalStreamId != 0 ||
            acceptedFrame.Sequence != 1)
        {
            FailOutgoingSession(
                session,
                "Получатель отклонил начало диагностической сессии.",
                "receiver_rejected_hello");
            return;
        }
        var accepted = PeerSupportProtocol.DeserializeJson<PeerSupportResumeState>(
            acceptedFrame.Payload);
        if (accepted.SessionId != session.SessionId ||
            accepted.ResumeAfterSequence != acceptedFrame.Ack ||
            accepted.ResumeAfterSequence < session.LastAcknowledged ||
            accepted.ResumeAfterSequence > (ulong)Interlocked.Read(
                ref session.LastSequence))
        {
            FailOutgoingSession(
                session,
                "Получатель вернул недопустимую позицию возобновления.",
                "invalid_receiver_resume");
            return;
        }
        session.LastAcknowledged = accepted.ResumeAfterSequence;
        await session.Spool!.AckThroughAsync(
            session.LastAcknowledged,
            token).ConfigureAwait(false);
        session.CollectorWindow.AcknowledgeThrough(session.LastAcknowledged);
        SetStatus($"Логи передаются игроку {peer.PlayerName}.");

        while (!token.IsCancellationRequested)
        {
            if (!TryGetCurrentPeer(peer.IdentityId, out var current))
            {
                if (!session.StopRequested)
                {
                    PublishStopEvent(session, "peer_stale");
                }
                session.RequestStop("peer_stale", _timeProvider.GetUtcNow());
            }
            if (session.StopRequested)
            {
                await QueueTerminalFrameAsync(session, token).ConfigureAwait(false);
            }

            var sentAny = false;
            await foreach (var record in session.Spool.ReplayFromAsync(
                               session.LastAcknowledged,
                               token).ConfigureAwait(false))
            {
                if (session.StopRequested)
                {
                    break;
                }
                sentAny = true;
                var frame = DecodeSpoolEnvelope(record.Payload.Span);
                if (frame.Sequence != record.Sequence)
                {
                    throw new InvalidDataException(
                        "The diagnostics spool sequence is inconsistent.");
                }
                await limiter.WaitAsync(
                    frame.Payload.Length + PeerSupportProtocol.HeaderSize,
                    token).ConfigureAwait(false);
                await WriteFrameWithTimeoutAsync(
                    stream,
                    frame,
                    token).ConfigureAwait(false);
                var ack = await ReadFrameWithTimeoutAsync(
                    stream,
                    FrameIdleTimeout,
                    token).ConfigureAwait(false);
                if (ack.Type == PeerSupportFrameType.Error)
                {
                    StopFromRemoteError(session, ack);
                    return;
                }
                if (ack.Type != PeerSupportFrameType.Ack ||
                    ack.LogicalStreamId != 0 ||
                    ack.Ack != frame.Sequence)
                {
                    FailOutgoingSession(
                        session,
                        "Получатель вернул недопустимое подтверждение данных.",
                        "invalid_receiver_ack");
                    return;
                }
                session.LastAcknowledged = ack.Ack;
                await session.Spool.AckThroughAsync(ack.Ack, token)
                    .ConfigureAwait(false);
                session.CollectorWindow.AcknowledgeThrough(ack.Ack);
                Interlocked.Add(ref _bytesSent, frame.Payload.Length);
                if (frame.Type is PeerSupportFrameType.Cancel or
                    PeerSupportFrameType.CompleteSession)
                {
                    session.MarkTerminalAcknowledged();
                    session.ForceCancel();
                    return;
                }
            }

            if (!sentAny)
            {
                await session.WaitForDataAsync(_timeProvider, token)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task QueueTerminalFrameAsync(
        OutgoingSession session,
        CancellationToken token)
    {
        if (!session.TryMarkTerminalQueued())
        {
            return;
        }
        await session.WaitForProducersAsync(token).ConfigureAwait(false);
        var spool = session.Spool ??
                    throw new InvalidOperationException(
                        "The diagnostics spool is unavailable.");
        await spool.DiscardAfterAsync(
            session.LastAcknowledged,
            token).ConfigureAwait(false);
        session.CollectorWindow.DiscardAfter(session.LastAcknowledged);
        Interlocked.Exchange(
            ref session.LastSequence,
            checked((long)session.LastAcknowledged));
        var reason = _sanitizer.SanitizeMetadataValue(session.StopReason);
        if (reason.Length > 1024)
        {
            reason = reason[..1024];
        }
        await EnqueueFrameAsync(
            session,
            PeerSupportFrameType.Cancel,
            0,
            PeerSupportProtocol.SerializeJson(new PeerSupportCancelMessage(reason)),
            token).ConfigureAwait(false);
    }

    private void StopFromRemoteError(
        OutgoingSession session,
        PeerSupportFrame error)
    {
        var reason = "receiver_rejected_diagnostics";
        if (!error.Payload.IsEmpty)
        {
            try
            {
                var message = PeerSupportProtocol.DeserializeJson<
                    PeerSupportCancelMessage>(error.Payload);
                if (!string.IsNullOrWhiteSpace(message.Reason))
                {
                    reason = message.Reason;
                }
            }
            catch (InvalidDataException)
            {
            }
        }
        reason = _sanitizer.SanitizeMetadataValue(reason);
        session.FailureStatus = $"Передача логов остановлена получателем: {reason}";
        session.RequestStop("receiver_error: " + reason, _timeProvider.GetUtcNow());
        session.ForceCancel();
    }

    private void FailOutgoingSession(
        OutgoingSession session,
        string status,
        string reason)
    {
        session.FailureStatus = status;
        session.RequestStop(reason, _timeProvider.GetUtcNow());
        session.ForceCancel();
    }

    private static async Task<PeerSupportFrame> ReadFrameWithTimeoutAsync(
        Stream stream,
        TimeSpan timeout,
        CancellationToken token)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await PeerSupportProtocol.ReadFrameAsync(
                stream,
                timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException(
                "The diagnostics peer did not send a frame before the idle timeout.");
        }
    }

    private static async Task TryWriteTerminalErrorAsync(
        Stream stream,
        ulong acknowledged,
        string reason,
        CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await WriteFrameWithTimeoutAsync(
                stream,
                new PeerSupportFrame(
                    PeerSupportFrameType.Error,
                    0,
                    2,
                    acknowledged,
                    PeerSupportProtocol.SerializeJson(
                        new PeerSupportCancelMessage(reason))),
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or
                                   OperationCanceledException or
                                   InvalidOperationException)
        {
        }
    }

    private async Task ReceiveFramesAsync(
        Stream stream,
        PeerSupportHello hello,
        ObservedPeer authenticatedPeer,
        PeerConnectionContext connection,
        IncomingSessionState incoming,
        CancellationToken token)
    {
        ulong ackSequence = 1;
        while (!token.IsCancellationRequested)
        {
            if (!IsIncomingPeerCurrent(hello.SenderIdentityId, connection))
            {
                throw new IOException(
                    "The diagnostics connection no longer belongs to its sender.");
            }

            PeerSupportFrame frame;
            try
            {
                frame = await ReadFrameWithTimeoutAsync(
                    stream,
                    FrameIdleTimeout,
                    token).ConfigureAwait(false);
                incoming.ValidateFrameCadence(
                    frame.Type,
                    _timeProvider.GetUtcNow());
                await _receiveLimiter.WaitAsync(
                    frame.Payload.Length + PeerSupportProtocol.HeaderSize,
                    token).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                Interlocked.Increment(ref _decodeErrors);
                throw;
            }

            if (frame.Sequence <= incoming.HighestAcceptedSequence)
            {
                if (frame.Sequence != incoming.HighestAcceptedSequence ||
                    !string.Equals(
                        frame.Sha256,
                        incoming.HighestAcceptedHash,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "A diagnostics sequence was replayed with different content.");
                }
                await WriteAckAsync(
                    stream,
                    ++ackSequence,
                    incoming.HighestAcceptedSequence,
                    token).ConfigureAwait(false);
                continue;
            }
            if (frame.Sequence != incoming.HighestAcceptedSequence + 1)
            {
                throw new InvalidDataException(
                    "The diagnostics frame sequence contains a gap.");
            }

            var terminal = frame.Type is
                PeerSupportFrameType.CompleteSession or
                PeerSupportFrameType.Cancel;
            if (!terminal)
            {
                await incoming.Storage.CommitAcceptedFrameAsync(
                    frame.Sequence,
                    frame.Sha256,
                    async commitToken =>
                    {
                        switch (frame.Type)
                        {
                            case PeerSupportFrameType.Manifest:
                                await ApplyManifestAsync(
                                    incoming,
                                    hello,
                                    frame,
                                    commitToken).ConfigureAwait(false);
                                break;
                            case PeerSupportFrameType.Data:
                                await AppendIncomingDataAsync(
                                    incoming,
                                    frame,
                                    commitToken).ConfigureAwait(false);
                                Interlocked.Add(
                                    ref _bytesReceived,
                                    frame.Payload.Length);
                                break;
                            case PeerSupportFrameType.CompleteStream:
                            case PeerSupportFrameType.Heartbeat:
                                break;
                            default:
                                throw new InvalidDataException(
                                    "The diagnostics sender used an invalid frame type.");
                        }
                    },
                    token).ConfigureAwait(false);
                incoming.HighestAcceptedSequence =
                    incoming.Storage.HighestAcceptedSequence;
                incoming.HighestAcceptedHash =
                    incoming.Storage.HighestAcceptedHash;
            }
            else if (frame.Type == PeerSupportFrameType.CompleteSession)
            {
                await incoming.CompleteAsync("completed", token).ConfigureAwait(false);
                incoming.HighestAcceptedSequence = frame.Sequence;
                incoming.HighestAcceptedHash = frame.Sha256;
            }
            else
            {
                var reason = "cancelled_by_sender";
                if (!frame.Payload.IsEmpty)
                {
                    var cancel = PeerSupportProtocol.DeserializeJson<
                        PeerSupportCancelMessage>(frame.Payload);
                    reason = string.IsNullOrWhiteSpace(cancel.Reason)
                        ? reason
                        : cancel.Reason;
                }
                await incoming.CompleteAsync(reason, token).ConfigureAwait(false);
                incoming.HighestAcceptedSequence = frame.Sequence;
                incoming.HighestAcceptedHash = frame.Sha256;
            }

            await WriteAckAsync(
                stream,
                ++ackSequence,
                incoming.HighestAcceptedSequence,
                token).ConfigureAwait(false);
            incoming.Touch();
            QueueStatusRefresh();
            if (terminal)
            {
                _incoming.TryRemove(incoming.Key, out _);
                return;
            }
        }
    }

    /// <summary>
    /// The Steam account behind a connection is fixed for its whole life, so
    /// staying "current" now only means the frames still claim that account.
    /// </summary>
    private static bool IsIncomingPeerCurrent(
        string identityId,
        PeerConnectionContext connection)
    {
        var normalized = NormalizeIdentity(identityId);
        return normalized.Length != 0 &&
               string.Equals(normalized, connection.PeerId.ToString(), StringComparison.Ordinal);
    }

    private async Task ApplyManifestAsync(
        IncomingSessionState incoming,
        PeerSupportHello hello,
        PeerSupportFrame frame,
        CancellationToken token)
    {
        if (frame.LogicalStreamId != 0)
        {
            throw new InvalidDataException(
                "The diagnostics manifest stream ID is invalid.");
        }
        var manifest = PeerSupportProtocol.DeserializeJson<PeerSupportManifest>(
            frame.Payload);
        if (manifest.SessionId != hello.SessionId ||
             !string.Equals(
                 NormalizeIdentity(manifest.SenderIdentityId),
                 NormalizeIdentity(hello.SenderIdentityId),
                 StringComparison.OrdinalIgnoreCase) ||
             manifest.Streams.Count > MaxManifestStreams)
        {
            throw new InvalidDataException(
                "The diagnostics manifest identity is invalid.");
        }

        await incoming.Storage.UpdateMetadataAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["senderIdentityId"] = manifest.SenderIdentityId,
                ["senderPlayerName"] = manifest.SenderPlayerName,
                ["launcherVersion"] = manifest.LauncherVersion,
                ["gameVersion"] = manifest.GameVersion,
                ["javaVersion"] = manifest.JavaVersion,
                ["packHash"] = manifest.PackHash
            },
            token).ConfigureAwait(false);

        foreach (var stream in manifest.Streams)
        {
            if (stream.LogicalStreamId == 0 ||
                stream.LogicalStreamId > PeerSupportProtocol.MaxLogicalStreamId)
            {
                throw new InvalidDataException(
                    "The diagnostics manifest stream ID is invalid.");
            }

            var kind = ToStorageKind(stream.Kind);
            if (incoming.Streams.TryGetValue(
                    stream.LogicalStreamId,
                    out var existing))
            {
                if (existing.Kind != kind)
                {
                    throw new InvalidDataException(
                        "A diagnostics stream changed its kind.");
                }
                continue;
            }

            var sourceId = $"stream_{stream.LogicalStreamId:D7}";
            if (kind is not (SupportLogSourceKind.Events or
                             SupportLogSourceKind.Network))
            {
                var displayName = _sanitizer.SanitizeMetadataValue(
                    stream.DisplayName);
                if (displayName.Length > 256)
                {
                    displayName = displayName[..256];
                }
                await incoming.Storage.RegisterSourceAsync(
                    new SupportLogStreamDescriptor(
                        sourceId,
                        kind,
                        displayName,
                        stream.SourceLength,
                        stream.LastWriteUtc),
                    token).ConfigureAwait(false);
            }
            incoming.Streams.Add(
                stream.LogicalStreamId,
                new IncomingStream(sourceId, kind));
        }
    }

    private static async Task AppendIncomingDataAsync(
        IncomingSessionState incoming,
        PeerSupportFrame frame,
        CancellationToken token)
    {
        if (!incoming.Streams.TryGetValue(frame.LogicalStreamId, out var stream))
        {
            throw new InvalidDataException(
                "The diagnostics data stream was not registered.");
        }

        switch (stream.Kind)
        {
            case SupportLogSourceKind.Events:
                await AppendStructuredPayloadAsync(
                    incoming.Storage.AppendEventAsync,
                    frame.Payload,
                    token).ConfigureAwait(false);
                break;
            case SupportLogSourceKind.Network:
                await AppendStructuredPayloadAsync(
                    incoming.Storage.AppendNetworkAsync,
                    frame.Payload,
                    token).ConfigureAwait(false);
                break;
            default:
                await incoming.Storage.AppendLogAsync(
                    stream.SourceId,
                    frame.Payload,
                    token).ConfigureAwait(false);
                break;
        }
    }

    private static async Task AppendStructuredPayloadAsync(
        Func<JsonElement, CancellationToken, Task> append,
        ReadOnlyMemory<byte> payload,
        CancellationToken token)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(payload.Span);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                "The diagnostics structured stream is not UTF-8.",
                ex);
        }

        foreach (var line in text.Split(
                     ["\r\n", "\n"],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            await append(document.RootElement.Clone(), token).ConfigureAwait(false);
        }
    }

    private static Task WriteAckAsync(
        Stream stream,
        ulong ackSequence,
        ulong acknowledged,
        CancellationToken token) =>
        WriteFrameWithTimeoutAsync(
            stream,
            new PeerSupportFrame(
                PeerSupportFrameType.Ack,
                0,
                ackSequence,
                acknowledged,
                ReadOnlyMemory<byte>.Empty),
            token);

    private static async Task WriteFrameWithTimeoutAsync(
        Stream stream,
        PeerSupportFrame frame,
        CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(FrameWriteTimeout);
        try
        {
            await PeerSupportProtocol.WriteFrameAsync(
                stream,
                frame,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException(
                "The diagnostics peer did not accept a frame before the write timeout.");
        }
    }

    private async Task<IncomingSessionState> GetOrCreateIncomingSessionAsync(
        PeerSupportHello hello,
        ObservedPeer peer,
        PeerConnectionContext connection,
        CancellationToken token)
    {
        var key = BuildIncomingKey(peer.IdentityId, hello.SessionId);
        if (_incoming.TryGetValue(key, out var existing))
        {
            return existing;
        }
        await _incomingCreationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_incoming.TryGetValue(key, out existing))
            {
                return existing;
            }
            if (_incoming.Count >= MaxIncomingSessions ||
                _incoming.Values.Count(session =>
                    string.Equals(
                        session.Storage.PeerIdentityId,
                        peer.IdentityId,
                        StringComparison.Ordinal)) >=
                MaxIncomingSessionsPerPeer)
            {
                throw new InvalidDataException(
                    "Too many diagnostics sessions are already active.");
            }

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["protocolVersion"] =
                    PeerSupportProtocol.ProtocolVersion.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                ["steamId64"] = peer.IdentityId,
                ["steamPersonaName"] = connection.PersonaName
            };
            var storage = await _storage.CreateSessionAsync(
                new SupportLogSessionDescriptor(
                    hello.SessionId,
                    peer.IdentityId,
                    peer.PlayerName,
                    hello.StartedAtUtc,
                    metadata),
                token).ConfigureAwait(false);
            var persistedStreams = await storage
                .GetPersistedProtocolStreamsAsync(token)
                .ConfigureAwait(false);
            var created = new IncomingSessionState(
                key,
                storage,
                _timeProvider,
                persistedStreams);
            storage.StatusChanged += _ => QueueStatusRefresh();
            _incoming[key] = created;
            SetStatusFromSessions();
            return created;
        }
        finally
        {
            _incomingCreationGate.Release();
        }
    }

    private async Task CompleteDisconnectedIncomingAsync(
        IncomingSessionState incoming,
        long generation)
    {
        try
        {
            var identityId = incoming.Storage.PeerIdentityId;
            var delay = _peers.TryGetValue(identityId, out var peer)
                ? peer.LastSeenUtc + PeerTtl - _timeProvider.GetUtcNow()
                : TimeSpan.Zero;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _timeProvider, _shutdown.Token)
                    .ConfigureAwait(false);
            }
            if (!incoming.ShouldExpire(generation)) return;
            await incoming.CompleteAsync(
                "connection_lost_for_35_seconds",
                CancellationToken.None).ConfigureAwait(false);
            _incoming.TryRemove(incoming.Key, out _);
            SetStatusFromSessions();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Interlocked.Increment(ref _decodeErrors);
        }
    }

    /// <summary>
    /// The sender is whoever Steam says opened the connection; the hello only
    /// has to agree with that and to be addressed to this account. A friend
    /// whose rich presence has not been read yet is still a legitimate sender,
    /// so they are taken from the connection rather than from the peer list.
    /// </summary>
    private ObservedPeer ValidateIncomingHello(
        PeerSupportHello hello,
        PeerConnectionContext connection)
    {
        PeerSupportProtocol.ValidateHello(hello, _timeProvider.GetUtcNow());
        var senderId = NormalizeIdentity(hello.SenderIdentityId);
        var recipientId = NormalizeIdentity(hello.RecipientIdentityId);
        var localIdentity = GetLocalIdentity();
        if (hello.SessionId == Guid.Empty ||
            senderId.Length == 0 ||
            recipientId.Length == 0 ||
            !string.Equals(recipientId, localIdentity.id, StringComparison.Ordinal) ||
            string.Equals(senderId, localIdentity.id, StringComparison.Ordinal) ||
            !string.Equals(senderId, connection.PeerId.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The diagnostics sender identity does not match its Steam connection.");
        }

        if (TryGetCurrentPeer(senderId, out var peer)) return peer;

        var observed = new ObservedPeer(
            connection.PeerId,
            NormalizePlayerName(connection.PersonaName),
            _timeProvider.GetUtcNow());
        _peers[senderId] = observed;
        return observed;
    }

    internal void ValidateIncomingPeerForTesting(
        PeerSupportHello hello,
        PeerConnectionContext connection)
    {
        ValidateIncomingConnectionContext(connection);
        _ = ValidateIncomingHello(hello, connection);
    }

    internal static async Task AppendResumedFrameForTestingAsync(
        SupportLogReceiveSession storage,
        PeerSupportFrame frame,
        CancellationToken token = default)
    {
        var persistedStreams = await storage
            .GetPersistedProtocolStreamsAsync(token)
            .ConfigureAwait(false);
        var incoming = new IncomingSessionState(
            "test",
            storage,
            TimeProvider.System,
            persistedStreams);
        await AppendIncomingDataAsync(incoming, frame, token)
            .ConfigureAwait(false);
    }

    private void ValidateIncomingConnectionContext(PeerConnectionContext connection)
    {
        var now = _timeProvider.GetUtcNow();
        if (!connection.PeerId.IsValid ||
            !connection.IsFriend ||
            connection.AcceptedAtUtc < now.AddMinutes(-1) ||
            connection.AcceptedAtUtc > now.AddMinutes(1))
        {
            throw new InvalidDataException(
                "The diagnostics connection is not an accepted Steam friend.");
        }
    }

    private PeerSupportManifest CreateManifest(
        OutgoingSession session,
        SupportEnvironmentSnapshot environment)
    {
        var identity = GetLocalIdentity();
        var streams = new[]
        {
            new PeerSupportManifestStream(
                1,
                PeerSupportLogKind.Environment,
                "environment",
                null,
                environment.CapturedAtUtc),
            new PeerSupportManifestStream(
                2,
                PeerSupportLogKind.Events,
                "events",
                null,
                environment.CapturedAtUtc),
            new PeerSupportManifestStream(
                3,
                PeerSupportLogKind.Network,
                "network",
                null,
                environment.CapturedAtUtc)
        };
        var metadata = environment.RuntimeState
            .Take(256)
            .ToDictionary(
                pair => pair.Key,
                pair => _sanitizer.SanitizeMetadataValue(pair.Value),
                StringComparer.Ordinal);
        return new PeerSupportManifest
        {
            SessionId = session.SessionId,
            CreatedAtUtc = session.StartedAtUtc,
            SenderIdentityId = identity.id,
            SenderPlayerName = identity.name,
            LauncherVersion = environment.LauncherVersion,
            GameVersion = metadata.GetValueOrDefault("game.version", string.Empty),
            JavaVersion = environment.JavaVersion,
            PackHash = environment.PackHash,
            Streams = streams,
            Environment = metadata
        };
    }

    private bool TryGetCurrentPeer(string identityId, out ObservedPeer peer)
    {
        if (!_peers.TryGetValue(identityId, out peer!))
        {
            return false;
        }
        if (_timeProvider.GetUtcNow() - peer.LastSeenUtc > PeerTtl)
        {
            _peers.TryRemove(identityId, out _);
            peer = null!;
            return false;
        }
        return true;
    }

    private (string id, string name) GetLocalIdentity()
    {
        var value = _identityProvider();
        var identityId = NormalizeIdentity(value.id);
        if (identityId.Length == 0)
        {
            throw new InvalidOperationException(
                "The local diagnostics identity is unavailable.");
        }
        return (identityId, NormalizePlayerName(value.name));
    }

    private void CancelTargetIfMatches(string identityId, string reason)
    {
        OutgoingSession? target;
        lock (_targetGate)
        {
            target = _target;
            if (target is null ||
                !string.Equals(
                    target.Peer.IdentityId,
                    identityId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _target = null;
        }
        PublishStopEvent(target, reason);
        target.RequestStop(reason, _timeProvider.GetUtcNow());
        _ = StopSessionWithinDeadlineAsync(target, CancellationToken.None);
        SetStatusFromSessions();
    }

    private void CancelCurrentTarget(string reason)
    {
        OutgoingSession? target;
        lock (_targetGate)
        {
            target = _target;
            _target = null;
        }
        if (target is not null)
        {
            PublishStopEvent(target, reason);
            target.RequestStop(reason, _timeProvider.GetUtcNow());
            _ = StopSessionWithinDeadlineAsync(target, CancellationToken.None);
        }
        RaiseStateChanged();
    }

    private void PublishStopEvent(OutgoingSession session, string reason)
    {
        session.TryPublishEvent(new SupportTransportEvent(
            _timeProvider.GetUtcNow(),
            "diagnostic_session_stopping",
            session.Peer.IdentityId,
            reason));
    }

    private void SetStatusFromSessions()
    {
        OutgoingSession? target;
        lock (_targetGate) target = _target;
        var incoming = _incoming.Values
            .Where(session => session.IsActive)
            .Select(session =>
                $"{session.Storage.PeerPlayerName} " +
                $"({session.Storage.BytesReceived / (1024d * 1024d):F1} МиБ)")
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (target is not null)
        {
            SetStatus(
                $"Логи передаются игроку {target.Peer.PlayerName}." +
                (incoming.Length == 0
                    ? string.Empty
                    : $" Получение: {string.Join(", ", incoming)}."));
        }
        else if (incoming.Length > 0)
        {
            SetStatus($"Получение логов: {string.Join(", ", incoming)}.");
        }
        else
        {
            SetStatus("Передача логов выключена.");
        }
    }

    private void SetStatus(string value)
    {
        var previous = Interlocked.Exchange(ref _statusText, value);
        if (string.Equals(previous, value, StringComparison.Ordinal))
        {
            return;
        }
        RaiseStateChanged();
    }

    private void QueueStatusRefresh()
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Interlocked.Exchange(ref _statusRefreshPending, 1) != 0)
        {
            return;
        }
        _ = RefreshStatusAfterDelayAsync();
    }

    private async Task RefreshStatusAfterDelayAsync()
    {
        try
        {
            await Task.Delay(
                MetricsInterval,
                _timeProvider,
                _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            Interlocked.Exchange(ref _statusRefreshPending, 0);
        }
        if (Volatile.Read(ref _disposed) == 0)
        {
            SetStatusFromSessions();
        }
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch
        {
        }
    }

    private void PruneOrphanSpools()
    {
        try
        {
            Directory.CreateDirectory(_paths.SupportSpool);
            string? activeSpoolDirectory;
            lock (_targetGate)
            {
                activeSpoolDirectory = _target?.Spool?.DirectoryPath;
            }
            var directories = Directory.EnumerateDirectories(
                    _paths.SupportSpool,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath)
                .ToArray();
            foreach (var directory in directories)
            {
                if (!string.IsNullOrWhiteSpace(activeSpoolDirectory) &&
                    string.Equals(
                        directory,
                        Path.GetFullPath(activeSpoolDirectory),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or
                                           UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static byte[] EncodeSpoolEnvelope(
        PeerSupportFrameType type,
        uint logicalStreamId,
        ulong sequence,
        ulong ack,
        ReadOnlySpan<byte> payload)
    {
        var result = new byte[SpoolEnvelopeHeaderSize + payload.Length];
        result[0] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(1, 4), logicalStreamId);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(5, 8), sequence);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(13, 8), ack);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(21, 4), payload.Length);
        payload.CopyTo(result.AsSpan(SpoolEnvelopeHeaderSize));
        return result;
    }

    private static PeerSupportFrame DecodeSpoolEnvelope(ReadOnlySpan<byte> value)
    {
        if (value.Length < SpoolEnvelopeHeaderSize)
        {
            throw new InvalidDataException("The diagnostics spool record is truncated.");
        }
        var type = (PeerSupportFrameType)value[0];
        var streamId = BinaryPrimitives.ReadUInt32BigEndian(value.Slice(1, 4));
        var sequence = BinaryPrimitives.ReadUInt64BigEndian(value.Slice(5, 8));
        var ack = BinaryPrimitives.ReadUInt64BigEndian(value.Slice(13, 8));
        var length = BinaryPrimitives.ReadInt32BigEndian(value.Slice(21, 4));
        if (length < 0 || length != value.Length - SpoolEnvelopeHeaderSize)
        {
            throw new InvalidDataException(
                "The diagnostics spool payload length is invalid.");
        }
        return new PeerSupportFrame(
            type,
            streamId,
            sequence,
            ack,
            value[SpoolEnvelopeHeaderSize..].ToArray());
    }

    private static PeerSupportLogKind ToProtocolKind(SupportLogSourceKind kind) =>
        kind switch
        {
            SupportLogSourceKind.Launcher => PeerSupportLogKind.LauncherLog,
            SupportLogSourceKind.Game => PeerSupportLogKind.GameLog,
            SupportLogSourceKind.CrashReport => PeerSupportLogKind.CrashReport,
            SupportLogSourceKind.Environment => PeerSupportLogKind.Environment,
            SupportLogSourceKind.Events => PeerSupportLogKind.Events,
            SupportLogSourceKind.Network => PeerSupportLogKind.Network,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static SupportLogSourceKind ToStorageKind(PeerSupportLogKind kind) =>
        kind switch
        {
            PeerSupportLogKind.LauncherLog => SupportLogSourceKind.Launcher,
            PeerSupportLogKind.GameLog => SupportLogSourceKind.Game,
            PeerSupportLogKind.CrashReport => SupportLogSourceKind.CrashReport,
            PeerSupportLogKind.Environment => SupportLogSourceKind.Environment,
            PeerSupportLogKind.Events => SupportLogSourceKind.Events,
            PeerSupportLogKind.Network => SupportLogSourceKind.Network,
            _ => throw new InvalidDataException(
                "The diagnostics stream kind is invalid.")
        };

    private static string NormalizeIdentity(string? value) =>
        SteamId64.TryNormalize(value, out var canonical) ? canonical : string.Empty;

    private static string NormalizePlayerName(string? value)
    {
        var normalized = new string((value ?? string.Empty)
            .Where(character => !char.IsControl(character))
            .Take(64)
            .ToArray());
        return normalized.Length == 0 ? "Игрок" : normalized;
    }

    private static string BuildIncomingKey(string identityId, Guid sessionId) =>
        $"{identityId}/{sessionId:D}";

    private static async Task ObserveTaskAsync(Task? task)
    {
        if (task is null) return;
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private async Task StopSessionWithinDeadlineAsync(
        OutgoingSession session,
        CancellationToken token)
    {
        var task = session.Task;
        if (task is null)
        {
            session.ForceCancel();
            return;
        }
        try
        {
            await task.WaitAsync(
                GracefulStopTimeout,
                _timeProvider,
                token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            session.ForceCancel();
            await ObserveTaskAsync(task).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            session.ForceCancel();
            throw;
        }
        catch
        {
            session.ForceCancel();
            await ObserveTaskAsync(task).ConfigureAwait(false);
        }
    }

    private sealed record ObservedPeer(
        SteamId64 SteamId,
        string PlayerName,
        DateTimeOffset LastSeenUtc)
    {
        public string IdentityId { get; } = SteamId.ToString();

        public bool DescribesSamePeer(ObservedPeer other) =>
            SteamId == other.SteamId &&
            string.Equals(PlayerName, other.PlayerName, StringComparison.Ordinal);
    }

    [SuppressMessage(
        "Design",
        "CA1001",
        Justification = "The session-scoped SemaphoreSlim is used without exposing its wait handle.")]
    private sealed class OutgoingSession
    {
        private readonly object _streamGate = new();
        private readonly Dictionary<string, uint> _streams =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<PeerSupportManifestStream> _manifestStreams = [];
        private readonly SemaphoreSlim _dataAvailable = new(0, 1);
        private readonly object _stopGate = new();
        private readonly TaskCompletionSource _producersCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string _stopReason = "completed";
        private DateTimeOffset? _stopRequestedAtUtc;
        private int _stopRequested;
        private int _terminalQueued;
        private int _terminalAcknowledged;
        private int _cancellationDisposed;

        public OutgoingSession(
            ObservedPeer peer,
            Guid sessionId,
            DateTimeOffset startedAtUtc,
            CancellationTokenSource cancellation)
        {
            Peer = peer;
            SessionId = sessionId;
            CancelSource = cancellation;
            ProducerCancelSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            StartedAtUtc = startedAtUtc;
            Events = Channel.CreateBounded<SupportTransportEvent>(
                new BoundedChannelOptions(256)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest
                });
        }

        public ObservedPeer Peer { get; set; }
        public Guid SessionId { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public CancellationTokenSource CancelSource { get; }
        public CancellationTokenSource ProducerCancelSource { get; }
        public CancellationToken Token => CancelSource.Token;
        public CancellationToken ProducerToken => ProducerCancelSource.Token;
        public Task? Task { get; set; }
        public SupportLogSpool? Spool { get; set; }
        public PeerSupportManifest? Manifest { get; set; }
        public Channel<SupportTransportEvent> Events { get; }
        public SemaphoreSlim EnqueueGate { get; } = new(1, 1);
        public SupportCollectorSpoolWindow CollectorWindow { get; } = new();
        public string? FailureStatus { get; set; }
        public bool StopRequested => Volatile.Read(ref _stopRequested) != 0;
        public string StopReason
        {
            get
            {
                lock (_stopGate) return _stopReason;
            }
        }
        public DateTimeOffset? StopRequestedAtUtc
        {
            get
            {
                lock (_stopGate) return _stopRequestedAtUtc;
            }
        }
        public long LastSequence;
        public int LastStreamId = FirstDynamicStreamId - 1;
        public ulong LastAcknowledged;

        public uint GetOrAddStream(string sourceId, Func<uint> factory)
        {
            lock (_streamGate)
            {
                if (_streams.TryGetValue(sourceId, out var value)) return value;
                value = factory();
                _streams.Add(sourceId, value);
                return value;
            }
        }

        public bool TryGetStream(string sourceId, out uint streamId)
        {
            lock (_streamGate) return _streams.TryGetValue(sourceId, out streamId);
        }

        public void AddManifestStream(PeerSupportManifestStream stream)
        {
            lock (_streamGate)
            {
                if (_manifestStreams.Any(item =>
                        item.LogicalStreamId == stream.LogicalStreamId))
                {
                    return;
                }
                _manifestStreams.Add(stream);
                Manifest = Manifest! with
                {
                    Streams = Manifest.Streams
                        .Concat([stream])
                        .OrderBy(item => item.LogicalStreamId)
                        .ToArray()
                };
            }
        }

        public void TryPublishEvent(SupportTransportEvent value) =>
            Events.Writer.TryWrite(value);

        public void SignalData()
        {
            if (_dataAvailable.CurrentCount != 0) return;
            try
            {
                _dataAvailable.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }

        public async Task WaitForDataAsync(
            TimeProvider timeProvider,
            CancellationToken token)
        {
            _ = timeProvider;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                await _dataAvailable.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
            }
        }

        public void RequestStop(string reason, DateTimeOffset requestedAtUtc)
        {
            lock (_stopGate)
            {
                if (_stopRequested != 0)
                {
                    return;
                }
                _stopReason = string.IsNullOrWhiteSpace(reason)
                    ? "completed"
                    : reason;
                _stopRequestedAtUtc = requestedAtUtc;
                Volatile.Write(ref _stopRequested, 1);
            }
            Events.Writer.TryComplete();
            CancelProducers();
            SignalData();
        }

        public void CancelProducers()
        {
            try
            {
                ProducerCancelSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void MarkProducersCompleted() =>
            _producersCompleted.TrySetResult();

        public Task WaitForProducersAsync(CancellationToken token) =>
            _producersCompleted.Task.WaitAsync(token);

        public bool TryMarkTerminalQueued() =>
            StopRequested &&
            Interlocked.Exchange(ref _terminalQueued, 1) == 0;

        public void MarkTerminalAcknowledged() =>
            Interlocked.Exchange(ref _terminalAcknowledged, 1);

        public void ForceCancel()
        {
            Events.Writer.TryComplete();
            CancelProducers();
            try
            {
                CancelSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void DisposeCancellation()
        {
            if (Interlocked.Exchange(ref _cancellationDisposed, 1) != 0)
            {
                return;
            }
            _dataAvailable.Dispose();
            EnqueueGate.Dispose();
            ProducerCancelSource.Dispose();
            CancelSource.Dispose();
        }
    }

    private sealed class IncomingSessionState
    {
        private readonly object _gate = new();
        private readonly TimeProvider _timeProvider;
        private long _generation;
        private long _connectedGeneration;
        private DateTimeOffset _frameWindowStartedUtc;
        private DateTimeOffset _lastHeartbeatUtc;
        private int _framesInWindow;

        public IncomingSessionState(
            string key,
            SupportLogReceiveSession storage,
            TimeProvider timeProvider,
            IReadOnlyDictionary<uint, SupportLogSourceKind>? persistedStreams = null)
        {
            Key = key;
            Storage = storage;
            _timeProvider = timeProvider;
            HighestAcceptedSequence = storage.HighestAcceptedSequence;
            HighestAcceptedHash = storage.HighestAcceptedHash;
            foreach (var pair in persistedStreams ??
                                 new Dictionary<uint, SupportLogSourceKind>())
            {
                Streams.Add(
                    pair.Key,
                    new IncomingStream($"stream_{pair.Key:D7}", pair.Value));
            }
        }

        public string Key { get; }
        public SupportLogReceiveSession Storage { get; }
        public SemaphoreSlim ConnectionGate { get; } = new(1, 1);
        public Dictionary<uint, IncomingStream> Streams { get; } = [];
        public ulong HighestAcceptedSequence { get; set; }
        public string HighestAcceptedHash { get; set; } = string.Empty;
        public bool IsActive => Storage.IsActive;

        public long BeginConnection()
        {
            lock (_gate)
            {
                _generation++;
                _connectedGeneration = _generation;
                return _generation;
            }
        }

        public void EndConnection(long generation)
        {
            lock (_gate)
            {
                if (_connectedGeneration == generation)
                {
                    _connectedGeneration = 0;
                }
            }
        }

        public bool ShouldExpire(long generation)
        {
            lock (_gate)
            {
                return Storage.IsActive &&
                       _generation == generation &&
                       _connectedGeneration == 0;
            }
        }

        public void Touch()
        {
            _ = _timeProvider.GetUtcNow();
        }

        public void ValidateFrameCadence(
            PeerSupportFrameType type,
            DateTimeOffset now)
        {
            lock (_gate)
            {
                if (_frameWindowStartedUtc == default ||
                    now - _frameWindowStartedUtc >= TimeSpan.FromSeconds(1))
                {
                    _frameWindowStartedUtc = now;
                    _framesInWindow = 0;
                }
                if (++_framesInWindow > MaxFramesPerSecond)
                {
                    throw new InvalidDataException(
                        "The diagnostics sender exceeded the frame-rate limit.");
                }
                if (type != PeerSupportFrameType.Heartbeat)
                {
                    return;
                }
                if (_lastHeartbeatUtc != default &&
                    now - _lastHeartbeatUtc < TimeSpan.FromSeconds(5))
                {
                    throw new InvalidDataException(
                        "The diagnostics sender exceeded the heartbeat rate.");
                }
                _lastHeartbeatUtc = now;
            }
        }

        public Task CompleteAsync(string reason, CancellationToken token) =>
            Storage.CompleteAsync(reason, token);
    }

    private sealed record IncomingStream(
        string SourceId,
        SupportLogSourceKind Kind);

    private sealed record PeerSupportResumeState(
        Guid SessionId,
        ulong ResumeAfterSequence);

    private sealed record PeerSupportCancelMessage(string Reason);

    private sealed record SupportTransportEvent(
        DateTimeOffset AtUtc,
        string Type,
        string PeerIdentityId,
        string Reason,
        IReadOnlyDictionary<string, string>? Details = null);

    private sealed record SupportCollectorEvent(
        DateTimeOffset AtUtc,
        string Type,
        string SourceId,
        string LogicalName,
        string Message);
}

internal sealed class SupportCollectorSpoolWindow
{
    public const int DefaultMaxBytes = 512 * 1024;

    private readonly object _gate = new();
    private readonly int _maxBytes;
    private readonly SortedDictionary<ulong, int> _frames = [];
    private TaskCompletionSource _spaceAvailable = CreateSignal();
    private long _outstandingBytes;
    private long _reservedBytes;

    public SupportCollectorSpoolWindow(int maxBytes = DefaultMaxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _maxBytes = maxBytes;
    }

    internal long OutstandingBytes
    {
        get
        {
            lock (_gate) return _outstandingBytes;
        }
    }

    internal long ReservedBytes
    {
        get
        {
            lock (_gate) return _reservedBytes;
        }
    }

    public async ValueTask ReserveAsync(
        int byteCount,
        CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);
        if (byteCount > _maxBytes)
        {
            throw new InvalidDataException(
                "A collector frame exceeds the diagnostics spool-ahead window.");
        }

        while (true)
        {
            Task waitForSpace;
            lock (_gate)
            {
                if (_outstandingBytes + _reservedBytes <=
                    _maxBytes - byteCount)
                {
                    _reservedBytes = checked(_reservedBytes + byteCount);
                    return;
                }
                waitForSpace = _spaceAvailable.Task;
            }
            await waitForSpace.WaitAsync(token).ConfigureAwait(false);
        }
    }

    public ValueTask ReserveIfCollectorAsync(
        bool collectorFrame,
        int byteCount,
        CancellationToken token) =>
        collectorFrame
            ? ReserveAsync(byteCount, token)
            : ValueTask.CompletedTask;

    public void Commit(ulong sequence, int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);
        lock (_gate)
        {
            if (_reservedBytes < byteCount)
            {
                throw new InvalidOperationException(
                    "The collector spool reservation is unavailable.");
            }
            if (_frames.ContainsKey(sequence))
            {
                throw new InvalidOperationException(
                    "The collector spool sequence is already tracked.");
            }
            _reservedBytes -= byteCount;
            _outstandingBytes = checked(_outstandingBytes + byteCount);
            _frames.Add(sequence, byteCount);
        }
    }

    public void CancelReservation(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);
        lock (_gate)
        {
            if (_reservedBytes < byteCount)
            {
                throw new InvalidOperationException(
                    "The collector spool reservation is unavailable.");
            }
            _reservedBytes -= byteCount;
            SignalSpaceAvailableNoLock();
        }
    }

    public void Remove(ulong sequence)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sequence);
        lock (_gate)
        {
            if (!_frames.Remove(sequence, out var byteCount))
            {
                return;
            }
            _outstandingBytes = Math.Max(0, _outstandingBytes - byteCount);
            SignalSpaceAvailableNoLock();
        }
    }

    public void AcknowledgeThrough(ulong sequence)
    {
        lock (_gate)
        {
            var released = RemoveWhereNoLock(
                candidate => candidate <= sequence);
            if (released > 0)
            {
                SignalSpaceAvailableNoLock();
            }
        }
    }

    public void DiscardAfter(ulong sequence)
    {
        lock (_gate)
        {
            var released = RemoveWhereNoLock(
                candidate => candidate > sequence);
            if (released > 0)
            {
                SignalSpaceAvailableNoLock();
            }
        }
    }

    private long RemoveWhereNoLock(Func<ulong, bool> predicate)
    {
        long released = 0;
        foreach (var sequence in _frames.Keys.Where(predicate).ToArray())
        {
            released = checked(released + _frames[sequence]);
            _frames.Remove(sequence);
        }
        _outstandingBytes = Math.Max(0, _outstandingBytes - released);
        return released;
    }

    private void SignalSpaceAvailableNoLock()
    {
        var previous = _spaceAvailable;
        _spaceAvailable = CreateSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
