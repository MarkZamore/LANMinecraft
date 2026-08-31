using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using System.IO;
using System.Runtime.InteropServices;
using Steamworks;

namespace Minecraft;

/// <summary>
/// Launcher-to-launcher connections over Steam's peer-to-peer sockets. It is
/// the successor of the VPN-era socket layer: no addresses, no adapter choice,
/// no firewall rules - Steam routes to an account, directly when the network
/// allows and through its relays otherwise.
///
/// e4steam carries the game's own traffic over ISteamNetworkingMessages on
/// channel 480, which is a different interface from the sockets used here, so
/// the two coexist inside the same App ID.
/// </summary>
public sealed class SteamPeerTransport : IPeerTransport
{
    /// <summary>
    /// The launcher's virtual port. It is not a TCP port and nothing outside
    /// Steam sees it; it only separates our listen socket from anything else
    /// the same account runs.
    /// </summary>
    internal const int VirtualPort = 35656;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    /// <summary>Between polls while data is flowing; keeps one core free.</summary>
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(1);

    /// <summary>Between polls when nothing is arriving.</summary>
    private static readonly TimeSpan IdlePumpInterval = TimeSpan.FromMilliseconds(10);
    private const int MaxMessagesPerPoll = 32;

    // A world transfer moves gigabytes and Steam's own default ceiling is
    // 1 MB/s, which alone turns a 3 GB world into a 50-minute wait. The floor
    // matters just as much: the rate estimator starts there and climbs, so a
    // 1 MB/s floor spends the first minutes of every transfer ramping up.
    private const int SendRateMaxBytesPerSecond = 256 * 1024 * 1024;
    private const int SendRateMinBytesPerSecond = 8 * 1024 * 1024;
    private const int SendBufferBytes = 16 * 1024 * 1024;

    // The receiving end sets the real ceiling. Steam will not let a sender get
    // further ahead than the receiver has buffered for, so a 512 KB default
    // receive buffer caps a relayed transfer at about that much per round trip
    // - a few MB/s at relay latency, however high the send rate is allowed to
    // go. Raising the send rate alone was half an answer.
    private const int RecvBufferBytes = 16 * 1024 * 1024;
    private const int RecvBufferMessages = 1024;
    // Steam's own default is 10 s, so setting 10 s bought nothing while
    // looking deliberate. A world transfer holds one connection open for many
    // minutes across a relay, and it has to survive a re-route and a receiver
    // whose disk stalls, without dying below the transfer's own bounds - the
    // app-level timeout is the one that can produce a sentence a player can
    // act on, so Steam must not get there first.
    private const int ConnectedTimeoutMilliseconds = 60_000;

    /// <summary>First contact is the fragile part; the app allows 30 s too.</summary>
    private const int InitialTimeoutMilliseconds = 30_000;

    private readonly SteamClientService _client;
    private readonly Logger? _logger;
    private readonly ConcurrentDictionary<uint, PeerChannel> _channels = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<PeerConnectionContext>> _dialing = new();
    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _shutdown = new();

    private Callback<SteamNetConnectionStatusChangedCallback_t>? _statusCallback;
    private HSteamListenSocket _listenSocket = HSteamListenSocket.Invalid;
    private Task? _pumpTask;
    private bool _disposed;

    public SteamPeerTransport(SteamClientService client, Logger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger;
    }

    public event EventHandler<PeerConnection>? ConnectionAccepted;

    public bool IsAvailable => _client.Status.IsReady;

    public string UnavailableReason =>
        IsAvailable ? string.Empty : _client.Status.Message;

    public IReadOnlyCollection<SteamId64> ConnectedPeers =>
        _channels.Values.Select(channel => channel.Peer).Distinct().ToArray();

    public async Task<PeerConnection> ConnectAsync(
        SteamId64 peer,
        string protocolName,
        CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!peer.IsValid) throw new ArgumentException("The peer is not a Steam account.", nameof(peer));
        if (!IsAvailable) throw new InvalidOperationException(UnavailableReason);

        // Applied before the connection exists: Steam reads these when it
        // creates one, and a launcher that dials before it listens would
        // otherwise get the 1 MB/s default.
        ApplyConnectionTuning();
        var identity = new SteamNetworkingIdentity();
        identity.SetSteamID64(peer.Value);
        var handle = SteamNetworkingSockets.ConnectP2P(ref identity, VirtualPort, 0, null);
        if (handle == HSteamNetConnection.Invalid)
        {
            throw new IOException($"Steam could not start a connection to {peer}.");
        }

        var channel = Register(handle, peer, isIncoming: false);
        var dialed = new TaskCompletionSource<PeerConnectionContext>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _dialing[handle.m_HSteamNetConnection] = dialed;
        SteamNetworkingSockets.SetConnectionName(
            handle,
            $"lanmc:{protocolName}");
        // Steam dispatches state changes on the callback pump, which may have
        // reported Connected while this method was still registering; without
        // this the connection would sit there until the timeout.
        if (SteamNetworkingSockets.GetConnectionInfo(handle, out var current) &&
            current.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
        {
            OnConnected(handle, peer.Value);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
            timeout.CancelAfter(ConnectTimeout);
            var context = await dialed.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            // The full link, not just the route. This line is the only record
            // of a shared session that survives it, and a guest timed out by a
            // saturated path looks exactly like one timed out by a stalled
            // server unless somebody wrote down how deep the queue was. Route
            // and ping alone said nothing, and the permit beside them reads
            // like a speed while being a permission - which is how three
            // evenings of guessing at bandwidth began.
            _logger?.Info($"Steam connection to {peer} is up ({DescribeLink(handle)}).");
            return new PeerConnection(context, channel.Stream);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Close(handle, "connect timed out");
            throw new IOException(
                $"Не удалось соединиться с игроком {peer} через Steam за {ConnectTimeout.TotalSeconds:F0} с.");
        }
        catch
        {
            Close(handle, "connect failed");
            throw;
        }
        finally
        {
            _dialing.TryRemove(handle.m_HSteamNetConnection, out _);
        }
    }

    public Task StartListeningAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lifecycleGate)
        {
            if (_listenSocket != HSteamListenSocket.Invalid) return Task.CompletedTask;
            if (!IsAvailable) throw new InvalidOperationException(UnavailableReason);

            ApplyConnectionTuning();
            // Registered before the socket exists: the first inbound connection
            // can arrive between the two calls otherwise.
            _statusCallback ??= Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
            _listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, 0, null);
            if (_listenSocket == HSteamListenSocket.Invalid)
            {
                throw new IOException("Steam refused to open the launcher's listen socket.");
            }

            _pumpTask ??= Task.Run(PumpAsync, CancellationToken.None);
            _logger?.Info($"Listening for launcher connections on Steam virtual port {VirtualPort}.");
        }
        return Task.CompletedTask;
    }

    public Task StopListeningAsync()
    {
        lock (_lifecycleGate)
        {
            if (_listenSocket == HSteamListenSocket.Invalid) return Task.CompletedTask;
            SteamNetworkingSockets.CloseListenSocket(_listenSocket);
            _listenSocket = HSteamListenSocket.Invalid;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Steam's defaults are tuned for game packets, not for moving a world, and
    /// a direct route beats a relay when the players' networks allow one.
    /// </summary>
    private void ApplyConnectionTuning()
    {
        SetGlobalInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, SendRateMaxBytesPerSecond);
        SetGlobalInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, SendRateMinBytesPerSecond);
        SetGlobalInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, SendBufferBytes);
        SetGlobalInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_RecvBufferSize, RecvBufferBytes);
        SetGlobalInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_RecvBufferMessages, RecvBufferMessages);
        SetGlobalInt32(
            ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable,
            Constants.k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_All);
        SetGlobalInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutInitial, InitialTimeoutMilliseconds);
        SetGlobalInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected, ConnectedTimeoutMilliseconds);
    }

    /// <summary>
    /// Applies one tuning value, and says so if Steam declined it. The return
    /// value used to be discarded, which meant nothing in this program had ever
    /// confirmed that any of this tuning took effect - and if the send rate
    /// silently failed to apply, Steam's own formula permits 4.38e9 divided by
    /// the ping in microseconds, which at a 100 ms relay is 42 KiB/s. A
    /// multi-gigabyte world at 42 KiB/s is a progress bar that never visibly
    /// moves, which is exactly what a player reported.
    /// </summary>
    private void SetGlobalInt32(ESteamNetworkingConfigValue value, int amount)
    {
        var handle = GCHandle.Alloc(amount, GCHandleType.Pinned);
        try
        {
            var applied = SteamNetworkingUtils.SetConfigValue(
                value,
                ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global,
                IntPtr.Zero,
                ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                handle.AddrOfPinnedObject());
            if (!applied)
            {
                _logger?.Warn($"Steam refused the connection setting {value} = {amount}.");
            }
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Connection state arrives on the thread that runs Steam callbacks, which
    /// is <see cref="SteamClientService"/>'s pump.
    /// </summary>
    private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t change)
    {
        var handle = change.m_hConn;
        var key = handle.m_HSteamNetConnection;
        var remote = change.m_info.m_identityRemote.GetSteamID64();

        switch (change.m_info.m_eState)
        {
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting
                when change.m_info.m_hListenSocket != HSteamListenSocket.Invalid:
                AcceptIncoming(handle, remote);
                break;

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                OnConnected(handle, remote);
                break;

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                if (_dialing.TryRemove(key, out var dialing))
                {
                    dialing.TrySetException(new IOException(
                        $"Steam closed the connection to {remote}: {change.m_info.m_szEndDebug}"));
                }
                Fail(handle, change.m_info.m_szEndDebug);
                break;
        }
    }

    private void AcceptIncoming(HSteamNetConnection handle, ulong remote)
    {
        // Steam has already proved which account is calling, so membership of
        // the friend list is the whole authorisation decision.
        if (!SteamId64.TryFrom(remote, out var peer) || !IsFriend(remote))
        {
            _logger?.Warn($"Refused a Steam connection from {remote}: not a friend running this launcher.");
            Close(handle, "not a friend");
            return;
        }

        if (SteamNetworkingSockets.AcceptConnection(handle) != EResult.k_EResultOK)
        {
            _logger?.Warn($"Steam refused the incoming connection from {peer}.");
            Close(handle, "accept failed");
            return;
        }

        Register(handle, peer, isIncoming: true);
    }

    private void OnConnected(HSteamNetConnection handle, ulong remote)
    {
        if (!_channels.TryGetValue(handle.m_HSteamNetConnection, out var channel)) return;
        var context = new PeerConnectionContext(
            channel.Peer,
            ResolvePersonaName(remote),
            IsFriend(remote),
            DateTimeOffset.UtcNow);

        if (_dialing.TryGetValue(handle.m_HSteamNetConnection, out var dialing))
        {
            dialing.TrySetResult(context);
            return;
        }

        if (!channel.IsIncoming) return;
        // No route and no ping on this line. Steam has not measured either yet
        // when a connection is accepted: four of these read 147 to 150 ms with
        // the next line of the same channel showing 3 to 4, and one said "relay
        // sto" two seconds before the same connection turned out to be direct.
        // The line below, written once the link is up, has the measured ones.
        _logger?.Info($"Accepted a launcher connection from {channel.Peer}.");
        ConnectionAccepted?.Invoke(this, new PeerConnection(context, channel.Stream));
    }

    private PeerChannel Register(HSteamNetConnection handle, SteamId64 peer, bool isIncoming)
    {
        var key = handle.m_HSteamNetConnection;
        var channel = new PeerChannel(handle, peer, isIncoming);
        channel.Attach(new SteamConnectionStream(handle, () => Close(handle, "closed by the launcher")));
        _channels[key] = channel;
        return channel;
    }

    private bool IsFriend(ulong steamId64) =>
        _client.Friends.Any(friend => friend.SteamId64 == steamId64);

    private string ResolvePersonaName(ulong steamId64) =>
        _client.Friends.FirstOrDefault(friend => friend.SteamId64 == steamId64)?.PersonaName ??
        steamId64.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Drains every open connection. Steam hands over whole messages and only
    /// one thread may take them, so this is the single reader for all peers.
    /// </summary>
    private async Task PumpAsync()
    {
        var messages = new IntPtr[MaxMessagesPerPoll];
        while (!_shutdown.IsCancellationRequested)
        {
            var idle = true;
            foreach (var channel in _channels.Values)
            {
                // Skip a channel whose reader is still behind rather than
                // waiting on it here. Steam keeps that connection's own receive
                // buffer filling and applies the backpressure per connection,
                // which is what backpressure is supposed to mean; awaiting in
                // the shared loop applied it to every peer at once.
                if (!channel.PendingFlush.IsCompleted)
                {
                    idle = false;
                    continue;
                }

                var count = SteamNetworkingSockets.ReceiveMessagesOnConnection(
                    channel.Handle, messages, messages.Length);
                if (count <= 0) continue;
                idle = false;

                // A stream disposed between the poll and the write is normal -
                // the peer hung up, or the protocol finished. It ends that
                // connection, never the pump every other peer shares.
                try
                {

                for (var index = 0; index < count; index++)
                {
                    try
                    {
                        var message = Marshal.PtrToStructure<SteamNetworkingMessage_t>(messages[index]);
                        if (message.m_cbSize > 0)
                        {
                            // Steam hands out unmanaged memory that is valid until
                            // Release, so the bytes are copied into the pipe here.
                            var scratch = ArrayPool<byte>.Shared.Rent(message.m_cbSize);
                            try
                            {
                                Marshal.Copy(message.m_pData, scratch, 0, message.m_cbSize);
                                channel.Stream.InboundWriter.Write(scratch.AsSpan(0, message.m_cbSize));
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(scratch);
                            }
                        }
                    }
                    finally
                    {
                        SteamNetworkingMessage_t.Release(messages[index]);
                    }
                }

                    var flush = channel.Stream.InboundWriter.FlushAsync(_shutdown.Token);
                    if (flush.IsCompleted)
                    {
                        _ = flush.Result;
                    }
                    else
                    {
                        // Remembered, not awaited: the next pass skips this
                        // channel until its reader catches up and services
                        // everyone else meanwhile.
                        channel.PendingFlush = flush;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    _logger?.Warn($"Steam connection to {channel.Peer} was closed while receiving: {ex.Message}");
                    Close(channel.Handle, "receive after close");
                }
            }

            try
            {
                // Always yield: a busy transfer would otherwise spin a core flat
                // out for the hour it takes to move a world. At 32 messages of
                // 256 KiB per poll this pause cannot be the bottleneck.
                await Task.Delay(idle ? IdlePumpInterval : PumpInterval, _shutdown.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Fail(HSteamNetConnection handle, string reason)
    {
        // Steam's own account of why a live connection ended used to go only
        // into the read side's exception and then into CloseConnection, so a
        // transfer that died mid-flight left nothing behind but the sender's
        // next write failing with NoConnection - the symptom, on whichever
        // side happened to notice second. The reason itself is the diagnosis.
        if (_channels.TryGetValue(handle.m_HSteamNetConnection, out var dying))
        {
            _logger?.Warn(
                $"Steam ended the connection to {dying.Peer}: " +
                $"{(string.IsNullOrWhiteSpace(reason) ? "no reason given" : reason)} " +
                $"({DescribeRoute(handle)}).");
        }

        if (_channels.TryRemove(handle.m_HSteamNetConnection, out var channel))
        {
            channel.Stream.RecordEndReason(reason);
            channel.Stream.CompleteInbound(
                string.IsNullOrWhiteSpace(reason) ? null : new IOException(reason));
        }
        SteamNetworkingSockets.CloseConnection(handle, 0, reason, false);
    }

    private void Close(HSteamNetConnection handle, string reason)
    {
        if (_channels.TryRemove(handle.m_HSteamNetConnection, out var channel))
        {
            channel.Stream.CompleteInbound();
        }
        SteamNetworkingSockets.CloseConnection(handle, 0, reason, true);
    }

    private static string DescribeRoute(HSteamNetConnection connection)
    {
        var status = default(SteamNetConnectionRealTimeStatus_t);
        var lanes = default(SteamNetConnectionRealTimeLaneStatus_t);
        if (SteamNetworkingSockets.GetConnectionRealTimeStatus(connection, ref status, 0, ref lanes) !=
            EResult.k_EResultOK)
        {
            // Without this the failed call left a zeroed struct behind and the
            // line went out as a confident "ping 0 ms".
            return "route unknown";
        }

        // m_nSendRateBytesPerSecond is a permission, not a measurement - Steam
        // fixes it when the connection is set up and never revises it. It was
        // dropped from this line for reading like the speed of the link, which
        // was the wrong lesson: it is the read-back of the one setting that
        // decides how fast we are allowed to go. If it says 0,04 MB/s when
        // 8 MB/s was configured, the floor never reached the connection, and
        // that is a different problem from a slow relay. Named as a permit so
        // it cannot be mistaken for throughput again.
        return $"{DescribeConnectionRoute(connection)}, ping {status.m_nPing} ms, " +
               $"permit {status.m_nSendRateBytesPerSecond / (1024d * 1024d):F2} MiB/s";
    }

    /// <summary>
    /// Direct or relayed, and through which of Valve's clusters. The old test
    /// asked whether a relay POP was set; the connection flags say it outright,
    /// and the cluster is four packed ASCII bytes - two players in one country
    /// routed through a far cluster would explain a 100 ms ping by itself.
    /// </summary>
    private static string DescribeConnectionRoute(HSteamNetConnection connection)
    {
        if (!SteamNetworkingSockets.GetConnectionInfo(connection, out var info)) return "unknown route";

        var relayed = (info.m_nFlags & Constants.k_nSteamNetworkConnectionInfoFlags_Relayed) != 0;
        var pop = DescribePop(info.m_idPOPRelay.m_SteamNetworkingPOPID);
        if (relayed) return pop.Length == 0 ? "relay" : $"relay {pop}";
        return (info.m_nFlags & Constants.k_nSteamNetworkConnectionInfoFlags_Fast) != 0
            ? "direct/LAN"
            : "direct";
    }

    private static string DescribePop(uint pop)
    {
        if (pop == 0) return string.Empty;
        Span<char> name = stackalloc char[4];
        var length = 0;
        for (var shift = 24; shift >= 0; shift -= 8)
        {
            var c = (char)((pop >> shift) & 0xFF);
            if (c is >= ' ' and <= '~') name[length++] = c;
        }
        return new string(name[..length]).Trim();
    }

    /// <summary>
    /// One line of what the wire is actually doing, for the transfer to log
    /// while it runs. Everything here is measured except the permit; the
    /// achieved rate is Steam's own five-second average, so sampling it faster
    /// than that only re-reads the same number.
    /// </summary>
    internal static string DescribeLink(HSteamNetConnection connection)
    {
        var status = default(SteamNetConnectionRealTimeStatus_t);
        var lanes = default(SteamNetConnectionRealTimeLaneStatus_t);
        if (SteamNetworkingSockets.GetConnectionRealTimeStatus(connection, ref status, 0, ref lanes) !=
            EResult.k_EResultOK)
        {
            return "link unreadable";
        }

        return $"{DescribeConnectionRoute(connection)}, out {status.m_flOutBytesPerSec / (1024d * 1024d):F2} MiB/s, " +
               $"permit {status.m_nSendRateBytesPerSecond / (1024d * 1024d):F2} MiB/s, " +
               $"queued {status.m_cbPendingReliable / 1024d:F0} KiB, " +
               $"unacked {status.m_cbSentUnackedReliable / 1024d:F0} KiB, " +
               $"ping {status.m_nPing} ms, " +
               // Local quality watches what arrives here, which for a sender is
               // only acks - nearly blind to loss on the path our bytes take.
               // Remote quality is the peer's view of that path and the one
               // number that says whether the wire is dropping what we send.
               $"quality in {status.m_flConnectionQualityLocal:F2} out {status.m_flConnectionQualityRemote:F2}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await StopListeningAsync().ConfigureAwait(false);

        foreach (var channel in _channels.Values)
        {
            Close(channel.Handle, "launcher is closing");
        }

        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _statusCallback?.Dispose();
        _statusCallback = null;
        _shutdown.Dispose();
    }

    private sealed class PeerChannel(HSteamNetConnection handle, SteamId64 peer, bool isIncoming)
    {
        private SteamConnectionStream? _stream;

        /// <summary>
        /// A flush this channel's reader has not caught up with yet. It is kept
        /// per channel so a peer nobody is reading cannot stop the pump for
        /// everyone else - which is what happened while a world transfer sat
        /// beside two idle connections to the same player and stopped being
        /// drained at all.
        /// </summary>
        public ValueTask<FlushResult> PendingFlush { get; set; }

        public HSteamNetConnection Handle { get; } = handle;
        public SteamId64 Peer { get; } = peer;
        public bool IsIncoming { get; } = isIncoming;
        public SteamConnectionStream Stream => _stream!;

        public void Attach(SteamConnectionStream stream) => _stream = stream;
    }
}
