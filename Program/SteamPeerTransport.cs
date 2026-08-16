using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
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
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(2);
    private const int MaxMessagesPerPoll = 32;

    // A world transfer moves gigabytes: Steam's 1 MB/s default would turn a
    // 3 GB world into a 50-minute wait even on a fast link.
    private const int SendRateMaxBytesPerSecond = 100 * 1024 * 1024;
    private const int SendBufferBytes = 16 * 1024 * 1024;
    private const int ConnectionTimeoutMilliseconds = 10_000;

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

    public async Task<PeerConnection> ConnectAsync(
        SteamId64 peer,
        string protocolName,
        CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!peer.IsValid) throw new ArgumentException("The peer is not a Steam account.", nameof(peer));
        if (!IsAvailable) throw new InvalidOperationException(UnavailableReason);

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
            _logger?.Info($"Steam connection to {peer} is up ({DescribeRoute(handle)}).");
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
        SetGlobalInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, SendRateMaxBytesPerSecond / 100);
        SetGlobalInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, SendBufferBytes);
        SetGlobalInt32(
            ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable,
            Constants.k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_All);
        SetGlobalInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutInitial, ConnectionTimeoutMilliseconds);
        SetGlobalInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected, ConnectionTimeoutMilliseconds);
    }

    private static void SetGlobalInt32(ESteamNetworkingConfigValue value, int amount)
    {
        var handle = GCHandle.Alloc(amount, GCHandleType.Pinned);
        try
        {
            SteamNetworkingUtils.SetConfigValue(
                value,
                ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global,
                IntPtr.Zero,
                ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                handle.AddrOfPinnedObject());
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
        _logger?.Info($"Accepted a launcher connection from {channel.Peer} ({DescribeRoute(handle)}).");
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
                    if (!flush.IsCompleted)
                    {
                        // The consumer is behind; waiting here is the backpressure.
                        await flush.ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    _logger?.Warn($"Steam connection to {channel.Peer} was closed while receiving: {ex.Message}");
                    Close(channel.Handle, "receive after close");
                }
            }

            if (idle)
            {
                try
                {
                    await Task.Delay(PumpInterval, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void Fail(HSteamNetConnection handle, string reason)
    {
        if (_channels.TryRemove(handle.m_HSteamNetConnection, out var channel))
        {
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
        SteamNetworkingSockets.GetConnectionRealTimeStatus(connection, ref status, 0, ref lanes);
        var route = SteamNetworkingSockets.GetConnectionInfo(connection, out var info)
            ? info.m_idPOPRelay.m_SteamNetworkingPOPID == 0 ? "direct" : "relay"
            : "unknown route";
        return $"{route}, ping {status.m_nPing} ms";
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

        public HSteamNetConnection Handle { get; } = handle;
        public SteamId64 Peer { get; } = peer;
        public bool IsIncoming { get; } = isIncoming;
        public SteamConnectionStream Stream => _stream!;

        public void Attach(SteamConnectionStream stream) => _stream = stream;
    }
}
