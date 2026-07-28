using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft;

public sealed class LanRelayService : IAsyncDisposable
{
    public const string ProtocolName = "MinecraftPortableLanRelay";
    public const int LegacyProtocolVersion = 1;
    public const int ProtocolVersion = LegacyProtocolVersion;
    public const int ResumableProtocolVersion = 2;
    internal const int MaxHandshakeBytes = 16 * 1024;
    private const int MaxHostResumableTunnels = 32;
    private const int MaxResumableTunnelsPerPeer = 4;
    private const int MaxLanSessionIdChars = 128;
    private const int MaxIdentityIdChars = 64;
    private const int MaxModeChars = 16;
    private const int MaxResumeTokenChars = 64;
    private const int MaxDiagnosticTextChars = 512;
    private readonly Logger _logger;
    private readonly ILanRelayPeerConnector _peerConnector;
    private readonly PeerRouteResolver _routes;
    private readonly INetworkClock _clock;
    private readonly SemaphoreSlim _clientRelayGate = new(1, 1);
    private readonly SemaphoreSlim _hostV2CreationGate = new(1, 1);
    private readonly Dictionary<string, ClientRelay> _clientRelays = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, HostV2Tunnel> _hostV2Tunnels = new();
    private readonly object _hostSessionGate = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private HostSession _hostSession = HostSession.Empty;
    private CancellationTokenSource _hostSessionCts = CreateCanceledTokenSource();
    private string _localIdentityId = "";
    private int _clientRelayCount;
    private int _activeClientV2Tunnels;
    private long _transportDrops;
    private long _resumeAttempts;
    private long _resumeSuccesses;
    private long _resumeFailures;
    private volatile bool _disposed;

    public LanRelayService(Logger logger, ISelectedNetworkTransport network, PeerRouteResolver routes)
        : this(
            logger,
            routes,
            new SelectedNetworkLanRelayPeerConnector(network),
            SystemNetworkClock.Instance)
    {
    }

    internal LanRelayService(
        Logger logger,
        PeerRouteResolver routes,
        ILanRelayPeerConnector peerConnector,
        INetworkClock? clock = null)
    {
        _logger = logger;
        _routes = routes;
        _peerConnector = peerConnector;
        _clock = clock ?? SystemNetworkClock.Instance;
    }

    public event Action<LanRelayDiagnosticEvent>? DiagnosticEvent;

    internal Func<LanRelayV2Frame, CancellationToken, ValueTask>?
        V2BeforeWriteFrameForTesting { get; set; }

    public void SetLocalIdentity(string? identityId)
    {
        var normalized = NormalizeIdentity(identityId);
        lock (_hostSessionGate)
        {
            if (_disposed) return;
            _localIdentityId = normalized;
        }
    }

    public void SetHostSession(int? port, string? sessionId)
    {
        var normalizedSessionId = sessionId?.Trim() ?? "";
        var next = port is > 0 and <= 65535 &&
                   !string.IsNullOrWhiteSpace(normalizedSessionId)
            ? new HostSession(port.Value, normalizedSessionId)
            : HostSession.Empty;
        CancellationTokenSource previousCts;
        lock (_hostSessionGate)
        {
            if (_disposed) return;
            if (_hostSession == next) return;
            previousCts = _hostSessionCts;
            _hostSession = next;
            _hostSessionCts = next == HostSession.Empty
                ? CreateCanceledTokenSource()
                : new CancellationTokenSource();
        }
        previousCts.Cancel();
        previousCts.Dispose();
    }

    public LanRelayDiagnosticSnapshot GetDiagnosticSnapshot()
    {
        lock (_hostSessionGate)
        {
            return new LanRelayDiagnosticSnapshot(
                _hostSession != HostSession.Empty,
                _hostSession.SessionId,
                _hostSession.Port,
                Volatile.Read(ref _clientRelayCount),
                _hostV2Tunnels.Count +
                Volatile.Read(ref _activeClientV2Tunnels),
                Interlocked.Read(ref _transportDrops),
                Interlocked.Read(ref _resumeAttempts),
                Interlocked.Read(ref _resumeSuccesses),
                Interlocked.Read(ref _resumeFailures));
        }
    }

    public async Task<ClientLanRelayInfo> GetOrCreateClientRelayAsync(
        string peerId,
        string lanSessionId,
        IReadOnlyList<PeerCandidateEndpoint> endpoints,
        int remoteLanPort,
        int peerRelayProtocolVersion = LegacyProtocolVersion)
    {
        if (remoteLanPort is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(remoteLanPort));
        var normalizedPeerId = NormalizeIdentity(peerId);
        var localIdentityId = GetLocalIdentity();
        if (normalizedPeerId.Length > 0 &&
            string.Equals(
                normalizedPeerId,
                localIdentityId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "A LAN relay cannot target the local identity.",
                nameof(peerId));
        }
        var targets = endpoints
            .Select(CreateTarget)
            .Where(target => target is not null)
            .Cast<LanRelayTarget>()
            .Distinct()
            .ToArray();
        if (targets.Length == 0) throw new ArgumentException("LAN relay has no valid peer endpoint.", nameof(endpoints));
        var key = BuildKey(peerId, lanSessionId);
        await _clientRelayGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_clientRelays.TryGetValue(key, out var existing))
            {
                existing.UpdateTargets(
                    targets,
                    remoteLanPort,
                    lanSessionId,
                    peerRelayProtocolVersion);
                return new ClientLanRelayInfo(key, existing.LocalPort);
            }

            var previousSessions = _clientRelays
                .Where(pair => string.Equals(
                    pair.Value.PeerId,
                    peerId.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (var previous in previousSessions)
            {
                _clientRelays.Remove(previous.Key);
            }
            Volatile.Write(ref _clientRelayCount, _clientRelays.Count);

            var preferredLocalPort = previousSessions
                .Select(pair => pair.Value.LocalPort)
                .FirstOrDefault();
            foreach (var previous in previousSessions)
            {
                await previous.Value.DisposeAsync().ConfigureAwait(false);
            }

            var relay = new ClientRelay(
                peerId,
                targets,
                remoteLanPort,
                lanSessionId,
                preferredLocalPort,
                _logger,
                _jsonOptions,
                _peerConnector,
                _routes,
                _clock,
                NormalizePeerProtocolVersion(peerRelayProtocolVersion),
                GetLocalIdentity,
                PublishDiagnosticEvent,
                () => V2BeforeWriteFrameForTesting,
                delta => Interlocked.Add(
                    ref _activeClientV2Tunnels,
                    delta));
            _clientRelays.Add(key, relay);
            Volatile.Write(ref _clientRelayCount, _clientRelays.Count);
            return new ClientLanRelayInfo(key, relay.LocalPort);
        }
        finally
        {
            _clientRelayGate.Release();
        }
    }

    public async Task RetainClientRelaysAsync(IReadOnlySet<string> activeKeys)
    {
        var removed = new List<ClientRelay>();
        await _clientRelayGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            foreach (var pair in _clientRelays.ToArray())
            {
                if (activeKeys.Contains(pair.Key)) continue;
                _clientRelays.Remove(pair.Key);
                removed.Add(pair.Value);
            }
            Volatile.Write(ref _clientRelayCount, _clientRelays.Count);
        }
        finally
        {
            _clientRelayGate.Release();
        }
        foreach (var relay in removed) await relay.DisposeAsync().ConfigureAwait(false);
    }

    public Task HandleIncomingAsync(
        Stream stream,
        byte[] initialFrame,
        CancellationToken token) =>
        HandleIncomingCoreAsync(stream, initialFrame, null, token);

    internal Task HandleIncomingAsync(
        Stream stream,
        byte[] initialFrame,
        PortableConnectionContext connection,
        CancellationToken token) =>
        HandleIncomingCoreAsync(stream, initialFrame, connection, token);

    private async Task HandleIncomingCoreAsync(
        Stream stream,
        byte[] initialFrame,
        PortableConnectionContext? connection,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(initialFrame);
        if (initialFrame.Length > MaxHandshakeBytes)
        {
            throw new InvalidDataException(
                "The Minecraft LAN relay handshake is too large.");
        }
        var request = PortableProtocol.Deserialize<LanRelayRequest>(
            initialFrame,
            _jsonOptions);
        if (request?.Protocol != ProtocolName)
        {
            throw new InvalidDataException("Unknown Minecraft LAN relay protocol.");
        }

        if (request.ProtocolVersion == LegacyProtocolVersion)
        {
            await HandleIncomingLegacyAsync(
                stream,
                request,
                token).ConfigureAwait(false);
            return;
        }
        if (request.ProtocolVersion == ResumableProtocolVersion)
        {
            await HandleIncomingV2Async(
                stream,
                request,
                connection,
                token).ConfigureAwait(false);
            return;
        }

        await WriteReplyAsync(
            stream,
            request.ProtocolVersion,
            false,
            "Unsupported LAN relay protocol version.",
            token).ConfigureAwait(false);
    }

    private async Task HandleIncomingLegacyAsync(
        Stream stream,
        LanRelayRequest request,
        CancellationToken token)
    {
        using var hostSession = CaptureHostSession(token);
        if (!IsRequestForSession(request, hostSession.Session))
        {
            await WriteReplyAsync(
                stream,
                LegacyProtocolVersion,
                false,
                "LAN session is not available.",
                token).ConfigureAwait(false);
            return;
        }

        TcpClient? minecraft = null;
        var readySent = false;
        var sessionToken = hostSession.Token;
        try
        {
            minecraft = await ConnectLocalMinecraftAsync(
                hostSession.Session.Port,
                sessionToken).ConfigureAwait(false);
            if (!IsCurrentHostSession(hostSession))
            {
                throw new OperationCanceledException(sessionToken);
            }
            await WriteReplyAsync(
                stream,
                LegacyProtocolVersion,
                true,
                "",
                sessionToken).ConfigureAwait(false);
            readySent = true;
            await RelayBidirectionalAsync(
                stream,
                minecraft.GetStream(),
                sessionToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            _logger.Warn(
                $"Incoming Minecraft LAN relay v1 failed: {DescribeError(ex)}");
            if (!readySent)
            {
                await TryWriteRejectedReplyAsync(
                    stream,
                    LegacyProtocolVersion,
                    "Could not reach the local LAN world.")
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            sessionToken.IsCancellationRequested ||
            !IsCurrentHostSession(hostSession))
        {
        }
        finally
        {
            minecraft?.Dispose();
        }
    }

    private async Task HandleIncomingV2Async(
        Stream stream,
        LanRelayRequest request,
        PortableConnectionContext? connection,
        CancellationToken token)
    {
        HostSession session;
        CancellationToken hostSessionToken;
        string localIdentity;
        lock (_hostSessionGate)
        {
            session = _hostSession;
            hostSessionToken = _hostSessionCts.Token;
            localIdentity = _localIdentityId;
        }

        var rejection = ValidateV2Request(
            request,
            session,
            localIdentity,
            connection);
        if (rejection.Length > 0)
        {
            PublishDiagnosticEvent(new LanRelayDiagnosticEvent
            {
                AtUtc = _clock.UtcNow,
                Phase = "rejected",
                Role = "host",
                TunnelId = request.TunnelId.ToString("D"),
                LanSessionId = SanitizeDiagnosticText(
                    request.LanSessionId,
                    MaxLanSessionIdChars),
                PeerIdentityId = NormalizeBoundedIdentity(
                    request.SenderIdentityId),
                RemoteAddress = connection?.RemoteAddress.ToString() ?? "",
                LocalAddress = connection?.LocalAddress.ToString() ?? "",
                LocalInterfaceId = connection?.LocalInterfaceId ?? "",
                HostMinecraftPort = request.ServerPort,
                Error = rejection,
                TerminalReason = "validation_failed"
            });
            await WriteReplyAsync(
                stream,
                ResumableProtocolVersion,
                false,
                rejection,
                token).ConfigureAwait(false);
            return;
        }

        HostV2Tunnel? tunnel = null;
        var created = false;
        long attachmentLease = 0;
        Exception? attachmentError = null;
        try
        {
            (tunnel, created) = await GetOrCreateHostV2TunnelAsync(
                request,
                connection!,
                session,
                hostSessionToken,
                token).ConfigureAwait(false);

            tunnel.Engine.ValidatePeerReceivedOffset(
                request.ReceivedOffset);
            attachmentLease = tunnel.BeginAttachment(connection!);
            using var attachmentToken =
                CancellationTokenSource.CreateLinkedTokenSource(
                    token,
                    hostSessionToken);
            await PortableProtocol.WriteJsonAsync(
                stream,
                new LanRelayReply
                {
                    ProtocolVersion = ResumableProtocolVersion,
                    Ok = true,
                    TunnelId = tunnel.TunnelId,
                    ResumeToken = tunnel.ResumeToken,
                    ReceivedOffset = tunnel.Engine.InboundReceivedOffset
                },
                _jsonOptions,
                attachmentToken.Token).ConfigureAwait(false);

            PublishDiagnosticEvent(tunnel.CreateEvent(
                created
                    ? "opened"
                    : string.Equals(
                        request.Mode,
                        "open",
                        StringComparison.Ordinal)
                        ? "open_reattached"
                        : "resume_attached",
                "transport",
                request.ReceivedOffset,
                created ? 1 : tunnel.AttemptCount,
                "",
                ""));
            tunnel.ConfirmAttachment(attachmentLease);
            await tunnel.Engine.AttachAsync(
                stream,
                request.ReceivedOffset,
                attachmentToken.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            token.IsCancellationRequested ||
            hostSessionToken.IsCancellationRequested ||
            tunnel?.Engine.IsTerminal == true)
        {
            if (hostSessionToken.IsCancellationRequested)
            {
                tunnel?.Stop("host_session_changed");
            }
            else if (tunnel is not null &&
                     attachmentLease != 0 &&
                     !tunnel.Engine.IsTerminal)
            {
                attachmentError = new OperationCanceledException(
                    "The portable listener restarted.");
            }
        }
        catch (Exception ex) when (
            ex is IOException or
            SocketException or
            InvalidDataException or
            JsonException or
            InvalidOperationException)
        {
            if (attachmentLease == 0)
            {
                await TryWriteRejectedReplyAsync(
                    stream,
                    ResumableProtocolVersion,
                    ex.Message).ConfigureAwait(false);
                if (created)
                {
                    tunnel?.Stop("open_rejected");
                }
            }
            if (tunnel is not null && attachmentLease != 0)
            {
                attachmentError = ex;
            }
            else
            {
                _logger.Warn(
                    $"Incoming Minecraft LAN relay v2 failed: {DescribeError(ex)}");
            }
        }
        finally
        {
            if (tunnel is not null && attachmentLease != 0)
            {
                tunnel.EndAttachment(
                    attachmentLease,
                    attachmentError);
            }
        }
    }

    private static async Task<TcpClient> ConnectLocalMinecraftAsync(int port, CancellationToken token)
    {
        Exception? lastError = null;
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            var client = new TcpClient(address.AddressFamily);
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                attempt.CancelAfter(TimeSpan.FromSeconds(3));
                await client.ConnectAsync(address, port, attempt.Token).ConfigureAwait(false);
                return client;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                client.Dispose();
                if (token.IsCancellationRequested) throw;
                lastError = ex;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        throw new IOException("Could not reach the local Minecraft listener.", lastError);
    }

    private string ValidateV2Request(
        LanRelayRequest request,
        HostSession session,
        string localIdentity,
        PortableConnectionContext? connection)
    {
        var fieldRejection = ValidateV2RequestFields(request);
        if (fieldRejection.Length > 0)
        {
            return fieldRejection;
        }
        if (!IsRequestForSession(request, session))
        {
            return "LAN session is not available.";
        }
        if (connection is null)
        {
            return "The LAN relay connection context is unavailable.";
        }
        if (connection.IsRemoteLoopback ||
            !VirtualNetworkService.IsUsableAddress(connection.RemoteAddress) ||
            !VirtualNetworkService.IsUsableAddress(connection.LocalAddress) ||
            string.IsNullOrWhiteSpace(connection.LocalInterfaceId))
        {
            return "The LAN relay did not arrive through a usable selected route.";
        }
        var senderIdentity = NormalizeIdentity(request.SenderIdentityId);
        var recipientIdentity = NormalizeIdentity(request.RecipientIdentityId);
        if (senderIdentity.Length == 0 ||
            recipientIdentity.Length == 0 ||
            !string.Equals(
                recipientIdentity,
                localIdentity,
                StringComparison.OrdinalIgnoreCase))
        {
            return "The LAN relay identity is invalid.";
        }
        if (string.Equals(
                senderIdentity,
                recipientIdentity,
                StringComparison.OrdinalIgnoreCase))
        {
            return "A LAN relay cannot connect the local identity to itself.";
        }
        if (!_routes.IsKnownEndpoint(
                request.SenderIdentityId,
                connection.RemoteAddress,
                connection.LocalInterfaceId))
        {
            return "The LAN relay peer route is unknown.";
        }
        if (request.TunnelId == Guid.Empty || request.ReceivedOffset < 0)
        {
            return "The LAN relay tunnel state is invalid.";
        }
        if (string.Equals(request.Mode, "open", StringComparison.Ordinal))
        {
            return IsValidResumeToken(request.ResumeToken)
                ? ""
                : "A new LAN relay tunnel requires a valid resume token.";
        }
        if (string.Equals(request.Mode, "resume", StringComparison.Ordinal))
        {
            return IsValidResumeToken(request.ResumeToken)
                ? ""
                : "The LAN relay resume token is invalid.";
        }
        return "The LAN relay operation is invalid.";
    }

    private async Task<(HostV2Tunnel Tunnel, bool Created)>
        GetOrCreateHostV2TunnelAsync(
            LanRelayRequest request,
            PortableConnectionContext connection,
            HostSession session,
            CancellationToken hostSessionToken,
            CancellationToken requestToken)
    {
        await _hostV2CreationGate.WaitAsync(requestToken).ConfigureAwait(false);
        try
        {
            if (_hostV2Tunnels.TryGetValue(request.TunnelId, out var existing))
            {
                if (!existing.CanResume(request, connection))
                {
                    throw new InvalidDataException(
                        "The LAN relay tunnel identity or route does not match.");
                }
                return (existing, false);
            }
            if (string.Equals(
                    request.Mode,
                    "resume",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The LAN relay resume request is stale.");
            }
            if (_hostV2Tunnels.Count >= MaxHostResumableTunnels)
            {
                throw new InvalidOperationException(
                    "The host has reached the resumable LAN relay limit.");
            }
            if (_hostV2Tunnels.Values.Count(candidate =>
                    string.Equals(
                        candidate.PeerIdentityId,
                        NormalizeIdentity(request.SenderIdentityId),
                        StringComparison.OrdinalIgnoreCase)) >=
                MaxResumableTunnelsPerPeer)
            {
                throw new InvalidOperationException(
                    "The peer has reached the resumable LAN relay limit.");
            }

            var minecraft = await ConnectLocalMinecraftAsync(
                session.Port,
                hostSessionToken).ConfigureAwait(false);
            try
            {
                hostSessionToken.ThrowIfCancellationRequested();
                var created = CreateHostV2Tunnel(
                    request,
                    connection,
                    minecraft,
                    hostSessionToken);
                if (!_hostV2Tunnels.TryAdd(request.TunnelId, created))
                {
                    throw new InvalidOperationException(
                        "The LAN relay tunnel registry changed unexpectedly.");
                }
                _ = ObserveHostTunnelAsync(created);
                return (created, true);
            }
            catch
            {
                minecraft.Dispose();
                throw;
            }
        }
        finally
        {
            _hostV2CreationGate.Release();
        }
    }

    private HostV2Tunnel CreateHostV2Tunnel(
        LanRelayRequest request,
        PortableConnectionContext connection,
        TcpClient minecraft,
        CancellationToken hostSessionToken)
    {
        HostV2Tunnel? hostTunnel = null;
        var engine = new LanRelayV2Tunnel(
            minecraft,
            _clock,
            signal =>
            {
                var tunnel = hostTunnel;
                if (tunnel is not null)
                {
                    PublishDiagnosticEvent(tunnel.CreateEvent(signal));
                }
            },
            hostSessionToken,
            V2BeforeWriteFrameForTesting);
        hostTunnel = new HostV2Tunnel(
            request.TunnelId,
            request.ResumeToken,
            request.SenderIdentityId,
            request.RecipientIdentityId,
            request.LanSessionId,
            request.ServerPort,
            connection,
            engine,
            _clock,
            PublishDiagnosticEvent);
        return hostTunnel;
    }

    private async Task ObserveHostTunnelAsync(HostV2Tunnel tunnel)
    {
        try
        {
            await tunnel.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _hostV2Tunnels.TryRemove(
                new KeyValuePair<Guid, HostV2Tunnel>(
                    tunnel.TunnelId,
                    tunnel));
            await tunnel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task WriteReplyAsync(
        Stream stream,
        int protocolVersion,
        bool ok,
        string message,
        CancellationToken token)
    {
        await PortableProtocol.WriteJsonAsync(
            stream,
            new LanRelayReply
            {
                ProtocolVersion = protocolVersion,
                Ok = ok,
                Message = SanitizeDiagnosticText(
                    message,
                    MaxDiagnosticTextChars)
            },
            _jsonOptions,
            token).ConfigureAwait(false);
    }

    private async Task TryWriteRejectedReplyAsync(
        Stream stream,
        int protocolVersion,
        string message)
    {
        try
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(2));
            await WriteReplyAsync(
                stream,
                protocolVersion,
                false,
                message,
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException or
            SocketException or
            OperationCanceledException or
            ObjectDisposedException)
        {
        }
    }

    private static bool IsRequestForSession(
        LanRelayRequest request,
        HostSession session) =>
        request.ServerPort is > 0 and <= 65535 &&
        request.ServerPort == session.Port &&
        IsBoundedProtocolText(
            request.LanSessionId,
            MaxLanSessionIdChars) &&
        string.Equals(
            request.LanSessionId,
            session.SessionId,
            StringComparison.Ordinal);

    private static bool IsValidResumeToken(string? value)
    {
        if (value is null ||
            value.Length is <= 0 or > MaxResumeTokenChars ||
            value.Any(char.IsControl))
        {
            return false;
        }
        try
        {
            return Convert.FromBase64String(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private string GetLocalIdentity()
    {
        lock (_hostSessionGate) return _localIdentityId;
    }

    private static string NormalizeIdentity(string? identityId) =>
        Guid.TryParse(identityId, out var value)
            ? value.ToString("D")
            : "";

    private static string NormalizeBoundedIdentity(string? identityId) =>
        identityId is { Length: > 0 and <= MaxIdentityIdChars } &&
        !identityId.Any(char.IsControl)
            ? NormalizeIdentity(identityId)
            : "";

    private static string ValidateV2RequestFields(LanRelayRequest request)
    {
        if (!IsBoundedProtocolText(
                request.LanSessionId,
                MaxLanSessionIdChars))
        {
            return "The LAN relay session identifier is invalid.";
        }
        if (!IsBoundedProtocolText(request.Mode, MaxModeChars))
        {
            return "The LAN relay operation is invalid.";
        }
        if (NormalizeBoundedIdentity(request.SenderIdentityId).Length == 0 ||
            NormalizeBoundedIdentity(request.RecipientIdentityId).Length == 0)
        {
            return "The LAN relay identity is invalid.";
        }
        if (request.ResumeToken is null ||
            request.ResumeToken.Length > MaxResumeTokenChars ||
            request.ResumeToken.Any(char.IsControl))
        {
            return "The LAN relay resume token is invalid.";
        }
        return "";
    }

    private static bool IsBoundedProtocolText(string? value, int maxChars) =>
        value is { Length: > 0 } &&
        value.Length <= maxChars &&
        !value.Any(char.IsControl);

    private static string SanitizeDiagnosticText(
        string? value,
        int maxChars)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0) return "";
        if (text.Length > maxChars) text = text[..maxChars];
        var characters = text.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (char.IsControl(characters[index]))
            {
                characters[index] = ' ';
            }
        }
        return new string(characters).Trim();
    }

    private static int NormalizePeerProtocolVersion(int value) =>
        value >= ResumableProtocolVersion
            ? ResumableProtocolVersion
            : LegacyProtocolVersion;

    private void PublishDiagnosticEvent(LanRelayDiagnosticEvent value)
    {
        switch (value.Phase)
        {
            case "transport_lost":
                Interlocked.Increment(ref _transportDrops);
                break;
            case "reconnect_attempt":
                Interlocked.Increment(ref _resumeAttempts);
                break;
            case "resumed":
            case "resume_attached":
                Interlocked.Increment(ref _resumeSuccesses);
                break;
            case "grace_expired":
                Interlocked.Increment(ref _resumeFailures);
                break;
        }

        var suffix = value.TerminalReason.Length > 0
            ? $" terminal={value.TerminalReason}"
            : "";
        var error = value.Error.Length > 0
            ? $" error={value.Error}"
            : "";
        _logger.Info(
            "LAN relay " +
            $"phase={value.Phase} role={value.Role} " +
            $"tunnel={value.TunnelId} session={value.LanSessionId} " +
            $"peer={value.PeerIdentityId} remote={value.RemoteAddress} " +
            $"local={value.LocalAddress} interface={value.LocalInterfaceId} " +
            $"relayPort={value.LocalRelayPort.ToString(CultureInfo.InvariantCulture)} " +
            $"hostPort={value.HostMinecraftPort.ToString(CultureInfo.InvariantCulture)} " +
            $"direction={value.Direction} " +
            $"produced={value.OutboundProducedOffset.ToString(CultureInfo.InvariantCulture)} " +
            $"acked={value.OutboundAcknowledgedOffset.ToString(CultureInfo.InvariantCulture)} " +
            $"received={value.InboundReceivedOffset.ToString(CultureInfo.InvariantCulture)} " +
            $"buffered={value.BufferedBytes.ToString(CultureInfo.InvariantCulture)} " +
            $"attempt={value.Attempt.ToString(CultureInfo.InvariantCulture)} " +
            $"downtimeMs={value.Downtime.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)}" +
            error +
            suffix);

        var handlers = DiagnosticEvent;
        if (handlers is null) return;
        foreach (Action<LanRelayDiagnosticEvent> handler in
                 handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch (Exception ex)
            {
                _logger.Warn(
                    $"LAN relay diagnostic subscriber failed: {ex.Message}");
            }
        }
    }

    private static string DescribeError(Exception ex)
    {
        Exception? current = ex;
        SocketException? socket = null;
        while (current is not null)
        {
            if (current is SocketException found)
            {
                socket = found;
                break;
            }
            current = current.InnerException;
        }
        return socket is null
            ? $"{ex.GetType().Name}: {ex.Message}"
            : $"{ex.GetType().Name}/{socket.SocketErrorCode}: {ex.Message}";
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource hostSessionCts;
        lock (_hostSessionGate)
        {
            if (_disposed) return;
            _disposed = true;
            _hostSession = HostSession.Empty;
            hostSessionCts = _hostSessionCts;
        }
        hostSessionCts.Cancel();
        ClientRelay[] relays;
        await _clientRelayGate.WaitAsync().ConfigureAwait(false);
        try
        {
            relays = _clientRelays.Values.ToArray();
            _clientRelays.Clear();
            Volatile.Write(ref _clientRelayCount, 0);
        }
        finally
        {
            _clientRelayGate.Release();
        }
        foreach (var relay in relays) await relay.DisposeAsync().ConfigureAwait(false);
        var hostTunnels = _hostV2Tunnels.Values.ToArray();
        foreach (var tunnel in hostTunnels)
        {
            tunnel.Stop("service_disposed");
        }
        foreach (var tunnel in hostTunnels)
        {
            await tunnel.DisposeAsync().ConfigureAwait(false);
        }
        _hostV2Tunnels.Clear();
        hostSessionCts.Dispose();
    }

    private HostSessionContext CaptureHostSession(CancellationToken externalToken)
    {
        lock (_hostSessionGate)
        {
            if (_disposed)
            {
                return new HostSessionContext(
                    HostSession.Empty,
                    CreateCanceledTokenSource());
            }
            return new HostSessionContext(
                _hostSession,
                CancellationTokenSource.CreateLinkedTokenSource(
                    externalToken,
                    _hostSessionCts.Token));
        }
    }

    private bool IsCurrentHostSession(HostSessionContext expected)
    {
        lock (_hostSessionGate)
        {
            return !expected.Token.IsCancellationRequested &&
                   _hostSession == expected.Session;
        }
    }

    private static CancellationTokenSource CreateCanceledTokenSource()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        return cts;
    }

    private static string BuildKey(
        string peerId,
        string lanSessionId) =>
        string.IsNullOrWhiteSpace(peerId)
            ? throw new ArgumentException("LAN relay requires a peer identity.", nameof(peerId))
            : string.IsNullOrWhiteSpace(lanSessionId)
                ? throw new ArgumentException("LAN relay requires a session id.", nameof(lanSessionId))
                : $"{peerId.Trim()}|{lanSessionId.Trim()}";

    private static LanRelayTarget? CreateTarget(PeerCandidateEndpoint endpoint)
    {
        if (!IPAddress.TryParse(endpoint.Address, out var remoteAddress) ||
            !VirtualNetworkService.IsUsableAddress(remoteAddress) ||
            !IPAddress.TryParse(endpoint.LocalAddress, out var localAddress) ||
            !VirtualNetworkService.IsUsableAddress(localAddress) ||
            localAddress.AddressFamily != remoteAddress.AddressFamily ||
            string.IsNullOrWhiteSpace(endpoint.LocalInterfaceId))
        {
            return null;
        }

        return new LanRelayTarget(
            remoteAddress,
            localAddress.ToString(),
            endpoint.LocalInterfaceId.Trim());
    }

    private static async Task RelayBidirectionalAsync(Stream first, Stream second, CancellationToken token)
    {
        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var firstToSecond = first.CopyToAsync(second, relayCts.Token);
        var secondToFirst = second.CopyToAsync(first, relayCts.Token);
        try
        {
            var completed = await Task.WhenAny(
                firstToSecond,
                secondToFirst).ConfigureAwait(false);
            var direction = ReferenceEquals(completed, firstToSecond)
                ? "client_to_host"
                : "host_to_client";
            try
            {
                await completed.ConfigureAwait(false);
                throw new EndOfStreamException(
                    $"LAN relay v1 {direction} reached EOF.");
            }
            catch (IOException ex)
            {
                throw new IOException(
                    $"LAN relay v1 {direction} stream failed.",
                    ex);
            }
        }
        finally
        {
            relayCts.Cancel();
            foreach (var task in new[] { firstToSecond, secondToFirst })
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private sealed class ClientRelay : IAsyncDisposable
    {
        private readonly string _peerId;
        private readonly object _targetGate = new();
        private IReadOnlyList<LanRelayTarget> _targets;
        private int _remoteLanPort;
        private string _lanSessionId;
        private int _peerRelayProtocolVersion;
        private readonly Logger _logger;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly ILanRelayPeerConnector _peerConnector;
        private readonly PeerRouteResolver _routes;
        private readonly INetworkClock _clock;
        private readonly Func<string> _localIdentityProvider;
        private readonly Action<LanRelayDiagnosticEvent> _diagnostic;
        private readonly Func<
            Func<LanRelayV2Frame, CancellationToken, ValueTask>?>
            _beforeWriteFrameProvider;
        private readonly Action<int> _activeV2Delta;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptTask;
        private readonly ConcurrentDictionary<long, Task> _clientTasks = new();
        private long _nextClientTaskId;
        private int _activeV2TunnelCount;
        private int _disposeStarted;

        public ClientRelay(
            string peerId,
            IReadOnlyList<LanRelayTarget> targets,
            int remoteLanPort,
            string lanSessionId,
            int preferredLocalPort,
            Logger logger,
            JsonSerializerOptions jsonOptions,
            ILanRelayPeerConnector peerConnector,
            PeerRouteResolver routes,
            INetworkClock clock,
            int peerRelayProtocolVersion,
            Func<string> localIdentityProvider,
            Action<LanRelayDiagnosticEvent> diagnostic,
            Func<Func<LanRelayV2Frame, CancellationToken, ValueTask>?>
                beforeWriteFrameProvider,
            Action<int> activeV2Delta)
        {
            _peerId = peerId.Trim();
            _targets = targets;
            _remoteLanPort = remoteLanPort;
            _lanSessionId = lanSessionId;
            _logger = logger;
            _jsonOptions = jsonOptions;
            _peerConnector = peerConnector;
            _routes = routes;
            _clock = clock;
            _peerRelayProtocolVersion = peerRelayProtocolVersion;
            _localIdentityProvider = localIdentityProvider;
            _diagnostic = diagnostic;
            _beforeWriteFrameProvider = beforeWriteFrameProvider;
            _activeV2Delta = activeV2Delta;
            _listener = StartLocalListener(preferredLocalPort);
            LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptTask = AcceptLoopAsync(_cts.Token);
        }

        public int LocalPort { get; }
        public string PeerId => _peerId;

        private static TcpListener StartLocalListener(int preferredPort)
        {
            if (preferredPort is > 0 and <= 65535)
            {
                var preferred = new TcpListener(IPAddress.Loopback, preferredPort);
                try
                {
                    preferred.Start();
                    return preferred;
                }
                catch (SocketException)
                {
                    preferred.Stop();
                }
                catch
                {
                    preferred.Stop();
                    throw;
                }
            }

            var fallback = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                fallback.Start();
                return fallback;
            }
            catch
            {
                fallback.Stop();
                throw;
            }
        }

        public void UpdateTargets(
            IReadOnlyList<LanRelayTarget> targets,
            int remoteLanPort,
            string lanSessionId,
            int peerRelayProtocolVersion)
        {
            lock (_targetGate)
            {
                _targets = targets.ToArray();
                _remoteLanPort = remoteLanPort;
                _lanSessionId = lanSessionId?.Trim() ?? "";
                _peerRelayProtocolVersion =
                    NormalizePeerProtocolVersion(peerRelayProtocolVersion);
            }
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    var taskId = Interlocked.Increment(ref _nextClientTaskId);
                    var task = HandleClientAsync(client, token);
                    _clientTasks[taskId] = task;
                    _ = ObserveClientTaskAsync(taskId, task);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    if (!token.IsCancellationRequested) _logger.Warn($"Local Minecraft LAN relay listener failed: {ex.Message}");
                    break;
                }
            }
        }

        private async Task ObserveClientTaskAsync(long taskId, Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
            {
            }
            catch (Exception ex)
            {
                _logger.Warn($"Local Minecraft LAN relay task failed: {ex.Message}");
            }
            finally
            {
                _clientTasks.TryRemove(taskId, out _);
            }
        }

        private async Task HandleClientAsync(TcpClient localClient, CancellationToken token)
        {
            int peerProtocol;
            lock (_targetGate)
            {
                peerProtocol = _peerRelayProtocolVersion;
            }
            if (peerProtocol >= ResumableProtocolVersion &&
                NormalizeIdentity(_localIdentityProvider()).Length > 0)
            {
                if (Interlocked.Increment(ref _activeV2TunnelCount) >
                    MaxResumableTunnelsPerPeer)
                {
                    Interlocked.Decrement(ref _activeV2TunnelCount);
                    using (localClient)
                    {
                        var snapshot = CaptureTargetSnapshot();
                        var target = snapshot.Targets.Count > 0
                            ? snapshot.Targets[0]
                            : null;
                        _diagnostic(new LanRelayDiagnosticEvent
                        {
                            AtUtc = _clock.UtcNow,
                            Phase = "local_limit_rejected",
                            Role = "client",
                            LanSessionId = snapshot.LanSessionId,
                            PeerIdentityId = _peerId,
                            RemoteAddress =
                                target?.Address.ToString() ?? "",
                            LocalAddress = target?.LocalAddress ?? "",
                            LocalInterfaceId =
                                target?.LocalInterfaceId ?? "",
                            LocalRelayPort = LocalPort,
                            HostMinecraftPort =
                                snapshot.RemoteLanPort,
                            Direction = "local",
                            Error =
                                "The peer has reached the resumable LAN relay limit.",
                            TerminalReason = "tunnel_limit"
                        });
                    }
                    return;
                }
                _activeV2Delta(1);
                try
                {
                    await HandleClientV2Async(
                        localClient,
                        token).ConfigureAwait(false);
                }
                finally
                {
                    _activeV2Delta(-1);
                    Interlocked.Decrement(ref _activeV2TunnelCount);
                }
                return;
            }

            await HandleClientLegacyAsync(localClient, token).ConfigureAwait(false);
        }

        private async Task HandleClientLegacyAsync(
            TcpClient localClient,
            CancellationToken token)
        {
            using (localClient)
            {
                var failures = new List<string>();
                var snapshot = CaptureTargetSnapshot();
                foreach (var target in snapshot.Targets)
                {
                    try
                    {
                        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        connectCts.CancelAfter(TimeSpan.FromSeconds(4));
                        using var remoteClient = await _peerConnector.ConnectAsync(
                            target,
                            WorldTransferService.TransferPort,
                            connectCts.Token).ConfigureAwait(false);
                        await using var remoteStream = remoteClient.GetStream();
                        await PortableProtocol.WriteJsonAsync(remoteStream, new LanRelayRequest
                        {
                            ProtocolVersion = LegacyProtocolVersion,
                            ServerPort = snapshot.RemoteLanPort,
                            LanSessionId = snapshot.LanSessionId
                        }, _jsonOptions, connectCts.Token).ConfigureAwait(false);
                        var replyFrame = await PortableProtocol.ReadFrameAsync(
                            remoteStream,
                            connectCts.Token,
                            MaxHandshakeBytes).ConfigureAwait(false);
                        var reply = PortableProtocol.Deserialize<LanRelayReply>(replyFrame, _jsonOptions);
                        if (reply is null || reply.Protocol != ProtocolName ||
                            reply.ProtocolVersion != LegacyProtocolVersion || !reply.Ok)
                        {
                            var message = SanitizeDiagnosticText(
                                reply?.Message,
                                MaxDiagnosticTextChars);
                            throw new IOException(
                                message.Length > 0
                                    ? message
                                    : "Remote LAN relay was rejected.");
                        }
                        _routes.MarkEndpointHealthy(_peerId, target.ToCandidate());
                        await RelayBidirectionalAsync(
                            localClient.GetStream(),
                            remoteStream,
                            token).ConfigureAwait(false);
                        return;
                    }
                    catch (Exception ex) when (
                        ex is SocketException or
                        IOException or
                        OperationCanceledException or
                        InvalidDataException or
                        JsonException)
                    {
                        if (token.IsCancellationRequested) return;
                        _routes.MarkEndpointUnhealthy(_peerId, target.ToCandidate());
                        failures.Add($"{target.Address}: {DescribeError(ex)}");
                    }
                }
                _logger.Warn(
                    "Minecraft LAN relay v1 failed: " +
                    string.Join("; ", failures.Take(3)));
            }
        }

        private async Task HandleClientV2Async(
            TcpClient localClient,
            CancellationToken token)
        {
            var tunnelId = Guid.NewGuid();
            var resumeToken = Convert.ToBase64String(
                RandomNumberGenerator.GetBytes(32));
            LanRelayTarget? currentTarget = null;
            var attempt = 0;
            var opened = false;
            DateTimeOffset? detachedAt = null;
            var startedAt = _clock.UtcNow;
            var initialSnapshot = CaptureTargetSnapshot();
            var initialLanSessionId = initialSnapshot.LanSessionId;
            var initialRemotePort = initialSnapshot.RemoteLanPort;

            await using var engine = new LanRelayV2Tunnel(
                localClient,
                _clock,
                signal => _diagnostic(CreateClientEvent(
                    signal,
                    tunnelId,
                    currentTarget,
                    attempt,
                    detachedAt,
                    CaptureTargetSnapshot())),
                token,
                _beforeWriteFrameProvider());

            while (!token.IsCancellationRequested && !engine.IsTerminal)
            {
                var snapshot = CaptureTargetSnapshot();
                if (!string.Equals(
                        snapshot.LanSessionId,
                        initialLanSessionId,
                        StringComparison.Ordinal) ||
                    snapshot.RemoteLanPort != initialRemotePort)
                {
                    engine.Stop("host_session_changed");
                    return;
                }

                var deadline = opened
                    ? (detachedAt ?? _clock.UtcNow) +
                      LanRelayV2Protocol.ReconnectGrace
                    : startedAt + LanRelayV2Protocol.ReconnectGrace;
                if (_clock.UtcNow >= deadline)
                {
                    engine.Stop("reconnect_grace_expired");
                    _diagnostic(CreateClientEvent(
                        null,
                        tunnelId,
                        currentTarget,
                        attempt,
                        detachedAt,
                        snapshot,
                        "grace_expired",
                        "",
                        "reconnect_grace_expired"));
                    return;
                }

                var failures = new List<string>();
                var attached = false;
                foreach (var target in snapshot.Targets)
                {
                    attempt++;
                    currentTarget = target;
                    _diagnostic(CreateClientEvent(
                        null,
                        tunnelId,
                        target,
                        attempt,
                        detachedAt,
                        snapshot,
                        opened
                            ? "reconnect_attempt"
                            : "connect_attempt",
                        "",
                        ""));
                    try
                    {
                        using var connectCts =
                            CancellationTokenSource.CreateLinkedTokenSource(token);
                        var remaining = deadline - _clock.UtcNow;
                        if (remaining <= TimeSpan.Zero) break;
                        connectCts.CancelAfter(
                            remaining < TimeSpan.FromSeconds(4)
                                ? remaining
                                : TimeSpan.FromSeconds(4));
                        using var remoteClient = await _peerConnector.ConnectAsync(
                            target,
                            WorldTransferService.TransferPort,
                            connectCts.Token).ConfigureAwait(false);
                        await using var remoteStream = remoteClient.GetStream();

                        var mode = opened ? "resume" : "open";
                        await PortableProtocol.WriteJsonAsync(
                            remoteStream,
                            new LanRelayRequest
                            {
                                ProtocolVersion = ResumableProtocolVersion,
                                ServerPort = snapshot.RemoteLanPort,
                                LanSessionId = snapshot.LanSessionId,
                                Mode = mode,
                                TunnelId = tunnelId,
                                ResumeToken = resumeToken,
                                SenderIdentityId = NormalizeIdentity(
                                    _localIdentityProvider()),
                                RecipientIdentityId = NormalizeIdentity(_peerId),
                                ReceivedOffset =
                                    engine.InboundReceivedOffset
                            },
                            _jsonOptions,
                            connectCts.Token).ConfigureAwait(false);
                        var replyFrame = await PortableProtocol.ReadFrameAsync(
                            remoteStream,
                            connectCts.Token,
                            MaxHandshakeBytes).ConfigureAwait(false);
                        var reply = PortableProtocol.Deserialize<LanRelayReply>(
                            replyFrame,
                            _jsonOptions);
                        if (reply is null ||
                            reply.Protocol != ProtocolName ||
                            reply.ProtocolVersion != ResumableProtocolVersion ||
                             !reply.Ok ||
                             reply.TunnelId != tunnelId ||
                             !IsValidResumeToken(reply.ResumeToken) ||
                             !CryptographicOperations.FixedTimeEquals(
                                 Encoding.UTF8.GetBytes(reply.ResumeToken),
                                 Encoding.UTF8.GetBytes(resumeToken)) ||
                            reply.ReceivedOffset < 0)
                        {
                            var message = SanitizeDiagnosticText(
                                reply?.Message,
                                MaxDiagnosticTextChars);
                            throw new IOException(
                                message.Length > 0
                                    ? message
                                    : "Remote resumable LAN relay was rejected.");
                        }
                        engine.ValidatePeerReceivedOffset(
                            reply.ReceivedOffset);

                        _routes.MarkEndpointHealthy(
                            _peerId,
                            target.ToCandidate());
                        var wasResume = opened;
                        opened = true;
                        attached = true;
                        _diagnostic(CreateClientEvent(
                            null,
                            tunnelId,
                            target,
                            attempt,
                            detachedAt,
                            snapshot,
                            wasResume ? "resumed" : "attached",
                            "",
                            ""));
                        detachedAt = null;

                        await engine.AttachAsync(
                            remoteStream,
                            reply.ReceivedOffset,
                            token).ConfigureAwait(false);
                        return;
                    }
                    catch (Exception ex) when (
                        ex is SocketException or
                        IOException or
                        OperationCanceledException or
                        InvalidDataException or
                        JsonException)
                    {
                        if (token.IsCancellationRequested || engine.IsTerminal)
                        {
                            return;
                        }
                        _routes.MarkEndpointUnhealthy(
                            _peerId,
                            target.ToCandidate());
                        failures.Add(
                            $"{target.Address}: {DescribeError(ex)}");
                        detachedAt ??= _clock.UtcNow;
                        _diagnostic(CreateClientEvent(
                            null,
                            tunnelId,
                            target,
                            attempt,
                            detachedAt,
                            snapshot,
                            attached ? "reconnect_pending" : "connect_failed",
                            DescribeError(ex),
                            ""));
                        attached = false;
                    }
                }

                if (failures.Count > 0)
                {
                    _logger.Warn(
                        "Minecraft LAN relay v2 transport attempt failed: " +
                        string.Join("; ", failures.Take(3)));
                }

                var delay = TimeSpan.FromMilliseconds(
                    Math.Min(
                        2000,
                        250 * Math.Pow(2, Math.Min(3, Math.Max(0, attempt - 1)))));
                var remainingDelay = deadline - _clock.UtcNow;
                if (remainingDelay <= TimeSpan.Zero) continue;
                await _clock.DelayAsync(
                    delay < remainingDelay ? delay : remainingDelay,
                    token).ConfigureAwait(false);
            }
        }

        private ClientTargetSnapshot CaptureTargetSnapshot()
        {
            lock (_targetGate)
            {
                return new ClientTargetSnapshot(
                    _targets.ToArray(),
                    _remoteLanPort,
                    _lanSessionId);
            }
        }

        private LanRelayDiagnosticEvent CreateClientEvent(
            LanRelayTunnelSignal? signal,
            Guid tunnelId,
            LanRelayTarget? target,
            int attempt,
            DateTimeOffset? detachedAt,
            ClientTargetSnapshot snapshot,
            string? phase = null,
            string error = "",
            string terminalReason = "") =>
            new()
            {
                AtUtc = signal?.AtUtc ?? _clock.UtcNow,
                Phase = phase ?? signal?.Phase ?? "state",
                Role = "client",
                TunnelId = tunnelId.ToString("D"),
                LanSessionId = snapshot.LanSessionId,
                PeerIdentityId = _peerId,
                RemoteAddress = target?.Address.ToString() ?? "",
                LocalAddress = target?.LocalAddress ?? "",
                LocalInterfaceId = target?.LocalInterfaceId ?? "",
                LocalRelayPort = LocalPort,
                HostMinecraftPort = snapshot.RemoteLanPort,
                Direction = signal?.Direction ?? "transport",
                OutboundProducedOffset =
                    signal?.OutboundProducedOffset ?? 0,
                OutboundAcknowledgedOffset =
                    signal?.OutboundAcknowledgedOffset ?? 0,
                InboundReceivedOffset =
                    signal?.InboundReceivedOffset ?? 0,
                BufferedBytes = signal?.BufferedBytes ?? 0,
                Attempt = attempt,
                Downtime = detachedAt.HasValue
                    ? _clock.UtcNow - detachedAt.Value
                    : TimeSpan.Zero,
                Error = error.Length > 0 ? error : signal?.Error ?? "",
                TerminalReason = terminalReason.Length > 0
                    ? terminalReason
                    : signal?.TerminalReason ?? ""
            };

        private sealed record ClientTargetSnapshot(
            IReadOnlyList<LanRelayTarget> Targets,
            int RemoteLanPort,
            string LanSessionId);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
            try
            {
                _cts.Cancel();
                _listener.Stop();
                try
                {
                    await _acceptTask.ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    ex is OperationCanceledException or
                    ObjectDisposedException or
                    SocketException)
                {
                }

                var clientTasks = _clientTasks.Values.ToArray();
                if (clientTasks.Length == 0) return;
                try
                {
                    await Task.WhenAll(clientTasks).ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    ex is OperationCanceledException or
                    IOException or
                    SocketException)
                {
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Minecraft LAN relay shutdown observed a failed client task: {ex.Message}");
                }
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }

    private sealed class HostV2Tunnel : IAsyncDisposable
    {
        private PortableConnectionContext _connection;
        private readonly INetworkClock _clock;
        private readonly Action<LanRelayDiagnosticEvent> _diagnostic;
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private CancellationTokenSource? _expiry;
        private DateTimeOffset? _detachedAt;
        private long _activeAttachmentLease;
        private long _nextAttachmentLease;
        private int _attemptCount;
        private int _disposeStarted;
        private bool _stopping;

        public HostV2Tunnel(
            Guid tunnelId,
            string resumeToken,
            string peerIdentityId,
            string localIdentityId,
            string lanSessionId,
            int hostMinecraftPort,
            PortableConnectionContext connection,
            LanRelayV2Tunnel engine,
            INetworkClock clock,
            Action<LanRelayDiagnosticEvent> diagnostic)
        {
            TunnelId = tunnelId;
            ResumeToken = resumeToken;
            PeerIdentityId = NormalizeIdentity(peerIdentityId);
            LocalIdentityId = NormalizeIdentity(localIdentityId);
            LanSessionId = lanSessionId;
            HostMinecraftPort = hostMinecraftPort;
            _connection = connection;
            Engine = engine;
            _clock = clock;
            _diagnostic = diagnostic;
            _ = ObserveEngineAsync();
        }

        public Guid TunnelId { get; }
        public string ResumeToken { get; }
        public string PeerIdentityId { get; }
        public string LocalIdentityId { get; }
        public string LanSessionId { get; }
        public int HostMinecraftPort { get; }
        public LanRelayV2Tunnel Engine { get; }
        public Task Completion => _completion.Task;
        public int AttemptCount => Volatile.Read(ref _attemptCount);

        public bool CanResume(
            LanRelayRequest request,
            PortableConnectionContext connection)
        {
            lock (_gate)
            {
                if (_stopping ||
                    Engine.IsTerminal ||
                    request.TunnelId != TunnelId ||
                    !FixedTimeTokenEquals(request.ResumeToken, ResumeToken) ||
                    !string.Equals(
                        NormalizeIdentity(request.SenderIdentityId),
                        PeerIdentityId,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        NormalizeIdentity(request.RecipientIdentityId),
                        LocalIdentityId,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        request.LanSessionId,
                        LanSessionId,
                        StringComparison.Ordinal) ||
                    request.ServerPort != HostMinecraftPort ||
                    !string.Equals(
                        connection.LocalInterfaceId,
                        _connection.LocalInterfaceId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                return true;
            }
        }

        public long BeginAttachment(PortableConnectionContext connection)
        {
            CancellationTokenSource? expiry;
            long lease;
            lock (_gate)
            {
                if (_stopping || Engine.IsTerminal)
                {
                    throw new InvalidOperationException(
                        "The LAN relay tunnel has already ended.");
                }
                if (_activeAttachmentLease != 0)
                {
                    throw new InvalidOperationException(
                        "The LAN relay tunnel already has an active transport.");
                }
                lease = checked(++_nextAttachmentLease);
                if (lease == 0)
                {
                    lease = checked(++_nextAttachmentLease);
                }
                _activeAttachmentLease = lease;
                _connection = connection;
                _attemptCount = checked(_attemptCount + 1);
                expiry = _expiry;
                _expiry = null;
            }
            CancelAndDisposeExpiry(expiry);
            return lease;
        }

        public void ConfirmAttachment(long lease)
        {
            lock (_gate)
            {
                if (_activeAttachmentLease == lease)
                {
                    _detachedAt = null;
                }
            }
        }

        public void EndAttachment(long lease, Exception? error)
        {
            var terminal = false;
            var detached = false;
            lock (_gate)
            {
                if (_activeAttachmentLease != lease) return;
                _activeAttachmentLease = 0;
                terminal = _stopping || Engine.IsTerminal;
                if (!terminal && error is not null)
                {
                    _detachedAt ??= _clock.UtcNow;
                    detached = true;
                }
            }
            if (terminal)
            {
                _completion.TrySetResult();
                return;
            }
            if (!detached || error is null) return;

            _diagnostic(CreateEvent(
                "reconnect_pending",
                "transport",
                Engine.InboundReceivedOffset,
                AttemptCount,
                DescribeError(error),
                ""));
            ScheduleExpiry();
        }

        public void Stop(string reason)
        {
            lock (_gate) _stopping = true;
            Engine.Stop(reason);
            _completion.TrySetResult();
        }

        public LanRelayDiagnosticEvent CreateEvent(
            LanRelayTunnelSignal signal) =>
            CreateEvent(
                signal.Phase,
                signal.Direction,
                signal.InboundReceivedOffset,
                AttemptCount,
                signal.Error,
                signal.TerminalReason,
                signal);

        public LanRelayDiagnosticEvent CreateEvent(
            string phase,
            string direction,
            long receivedOffset,
            int attempt,
            string error,
            string terminalReason,
            LanRelayTunnelSignal? signal = null)
        {
            DateTimeOffset? detachedAt;
            PortableConnectionContext connection;
            lock (_gate)
            {
                detachedAt = _detachedAt;
                connection = _connection;
            }
            return new LanRelayDiagnosticEvent
            {
                AtUtc = signal?.AtUtc ?? _clock.UtcNow,
                Phase = phase,
                Role = "host",
                TunnelId = TunnelId.ToString("D"),
                LanSessionId = LanSessionId,
                PeerIdentityId = PeerIdentityId,
                RemoteAddress = connection.RemoteAddress.ToString(),
                LocalAddress = connection.LocalAddress.ToString(),
                LocalInterfaceId = connection.LocalInterfaceId,
                HostMinecraftPort = HostMinecraftPort,
                Direction = direction,
                OutboundProducedOffset =
                    signal?.OutboundProducedOffset ??
                    Engine.OutboundProducedOffset,
                OutboundAcknowledgedOffset =
                    signal?.OutboundAcknowledgedOffset ??
                    Engine.OutboundAcknowledgedOffset,
                InboundReceivedOffset = receivedOffset,
                BufferedBytes = signal?.BufferedBytes ?? Engine.BufferedBytes,
                Attempt = attempt,
                Downtime = detachedAt.HasValue
                    ? _clock.UtcNow - detachedAt.Value
                    : TimeSpan.Zero,
                Error = error,
                TerminalReason = terminalReason
            };
        }

        private void ScheduleExpiry()
        {
            CancellationTokenSource expiry;
            lock (_gate)
            {
                if (_stopping ||
                    Engine.IsTerminal ||
                    _activeAttachmentLease != 0 ||
                    _expiry is not null)
                {
                    return;
                }
                _expiry = expiry = new CancellationTokenSource();
            }
            _ = ExpireAsync(expiry);
        }

        private async Task ExpireAsync(CancellationTokenSource expiry)
        {
            try
            {
                await _clock.DelayAsync(
                    LanRelayV2Protocol.ReconnectGrace,
                    expiry.Token).ConfigureAwait(false);
                var shouldExpire = false;
                lock (_gate)
                {
                    if (ReferenceEquals(_expiry, expiry) &&
                        !expiry.IsCancellationRequested &&
                        !_stopping &&
                        !Engine.IsTerminal &&
                        _activeAttachmentLease == 0)
                    {
                        _expiry = null;
                        _stopping = true;
                        shouldExpire = true;
                    }
                }
                if (shouldExpire)
                {
                    _diagnostic(CreateEvent(
                        "grace_expired",
                        "transport",
                        Engine.InboundReceivedOffset,
                        AttemptCount,
                        "",
                        "reconnect_grace_expired"));
                    Stop("reconnect_grace_expired");
                }
            }
            catch (OperationCanceledException) when (
                expiry.IsCancellationRequested)
            {
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_expiry, expiry))
                    {
                        _expiry = null;
                    }
                }
                expiry.Dispose();
            }
        }

        private async Task ObserveEngineAsync()
        {
            try
            {
                await Engine.Completion.ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is IOException or
                SocketException or
                OperationCanceledException or
                ObjectDisposedException)
            {
            }
            finally
            {
                if (Engine.IsTerminal)
                {
                    _completion.TrySetResult();
                }
            }
        }

        private static bool FixedTimeTokenEquals(
            string candidate,
            string expected)
        {
            try
            {
                return CryptographicOperations.FixedTimeEquals(
                    Convert.FromBase64String(candidate),
                    Convert.FromBase64String(expected));
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static void CancelAndDisposeExpiry(
            CancellationTokenSource? expiry)
        {
            if (expiry is null) return;
            try
            {
                expiry.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            try
            {
                expiry.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
            CancellationTokenSource? expiry;
            lock (_gate)
            {
                _stopping = true;
                expiry = _expiry;
                _expiry = null;
            }
            CancelAndDisposeExpiry(expiry);
            await Engine.DisposeAsync().ConfigureAwait(false);
            _completion.TrySetResult();
        }
    }

    private sealed class LanRelayRequest
    {
        public string Protocol { get; set; } = ProtocolName;
        public int ProtocolVersion { get; set; } = LanRelayService.ProtocolVersion;
        public int ServerPort { get; set; }
        public string LanSessionId { get; set; } = "";
        public string Mode { get; set; } = "open";
        public Guid TunnelId { get; set; }
        public string ResumeToken { get; set; } = "";
        public string SenderIdentityId { get; set; } = "";
        public string RecipientIdentityId { get; set; } = "";
        public long ReceivedOffset { get; set; }
    }

    private sealed class LanRelayReply
    {
        public string Protocol { get; set; } = ProtocolName;
        public int ProtocolVersion { get; set; } = LanRelayService.ProtocolVersion;
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public Guid TunnelId { get; set; }
        public string ResumeToken { get; set; } = "";
        public long ReceivedOffset { get; set; }
    }

    private sealed record HostSession(int Port, string SessionId)
    {
        public static HostSession Empty { get; } = new(0, "");
    }

    private sealed class HostSessionContext(
        HostSession session,
        CancellationTokenSource cancellation) : IDisposable
    {
        public HostSession Session { get; } = session;
        public CancellationToken Token => cancellation.Token;
        public void Dispose() => cancellation.Dispose();
    }
}

public sealed record LanRelayDiagnosticSnapshot(
    bool IsHosting,
    string LanSessionId,
    int LocalMinecraftPort,
    int ClientRelayCount,
    int ActiveResumableTunnels = 0,
    long TransportDrops = 0,
    long ResumeAttempts = 0,
    long ResumeSuccesses = 0,
    long ResumeFailures = 0);

public sealed record LanRelayDiagnosticEvent
{
    public DateTimeOffset AtUtc { get; init; }
    public string Phase { get; init; } = "";
    public string Role { get; init; } = "";
    public string TunnelId { get; init; } = "";
    public string LanSessionId { get; init; } = "";
    public string PeerIdentityId { get; init; } = "";
    public string RemoteAddress { get; init; } = "";
    public string LocalAddress { get; init; } = "";
    public string LocalInterfaceId { get; init; } = "";
    public int LocalRelayPort { get; init; }
    public int HostMinecraftPort { get; init; }
    public string Direction { get; init; } = "";
    public long OutboundProducedOffset { get; init; }
    public long OutboundAcknowledgedOffset { get; init; }
    public long InboundReceivedOffset { get; init; }
    public int BufferedBytes { get; init; }
    public int Attempt { get; init; }
    public TimeSpan Downtime { get; init; }
    public string Error { get; init; } = "";
    public string TerminalReason { get; init; } = "";
}

public sealed record ClientLanRelayInfo(string Key, int LocalPort);

internal sealed record LanRelayTarget(
    IPAddress Address,
    string LocalAddress,
    string LocalInterfaceId)
{
    public PeerCandidateEndpoint ToCandidate() => new()
    {
        Address = Address.ToString(),
        LocalAddress = LocalAddress,
        LocalInterfaceId = LocalInterfaceId
    };
}
