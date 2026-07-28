using System.Buffers;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Minecraft;

internal sealed record LanRelayTunnelSignal(
    DateTimeOffset AtUtc,
    string Phase,
    string Direction,
    long OutboundProducedOffset,
    long OutboundAcknowledgedOffset,
    long InboundReceivedOffset,
    int BufferedBytes,
    string Error,
    string TerminalReason);

internal sealed class LanRelayV2Tunnel : IAsyncDisposable
{
    private readonly TcpClient _minecraftClient;
    private readonly Stream _minecraftStream;
    private readonly INetworkClock _clock;
    private readonly Action<LanRelayTunnelSignal> _signal;
    private readonly Func<LanRelayV2Frame, CancellationToken, ValueTask>?
        _beforeWriteFrame;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>?
        _beforeWriteMinecraftForTesting;
    private readonly CancellationTokenSource _stop;
    private readonly CancellationTokenRegistration _stopRegistration;
    private readonly SemaphoreSlim _attachmentGate = new(1, 1);
    private readonly SemaphoreSlim _dataAvailable = new(0, 1);
    private readonly SemaphoreSlim _bufferSpaceAvailable = new(0, 1);
    private readonly object _stateGate = new();
    private readonly object _disposeGate = new();
    private readonly LinkedList<BufferedSegment> _outbound = [];
    private readonly Task _localReaderTask;

    private long _outboundProducedOffset;
    private long _outboundAcknowledgedOffset;
    private long _inboundReceivedOffset;
    private int _bufferedBytes;
    private bool _localEof;
    private string _terminalReason = "";
    private int _disposeStarted;
    private Task? _disposeTask;

    public LanRelayV2Tunnel(
        TcpClient minecraftClient,
        INetworkClock clock,
        Action<LanRelayTunnelSignal> signal,
        CancellationToken lifetimeToken,
        Func<LanRelayV2Frame, CancellationToken, ValueTask>?
            beforeWriteFrame = null,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>?
            beforeWriteMinecraftForTesting = null)
    {
        _minecraftClient =
            minecraftClient ?? throw new ArgumentNullException(nameof(minecraftClient));
        _minecraftStream = minecraftClient.GetStream();
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _beforeWriteFrame = beforeWriteFrame;
        _beforeWriteMinecraftForTesting =
            beforeWriteMinecraftForTesting;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        _stopRegistration = _stop.Token.Register(
            static state =>
            {
                try
                {
                    ((TcpClient)state!).Dispose();
                }
                catch
                {
                }
            },
            _minecraftClient);
        _localReaderTask = ReadLocalMinecraftAsync(_stop.Token);
    }

    public bool IsTerminal
    {
        get
        {
            lock (_stateGate) return _terminalReason.Length > 0;
        }
    }

    public string TerminalReason
    {
        get
        {
            lock (_stateGate) return _terminalReason;
        }
    }

    public long OutboundProducedOffset =>
        Interlocked.Read(ref _outboundProducedOffset);

    public long OutboundAcknowledgedOffset =>
        Interlocked.Read(ref _outboundAcknowledgedOffset);

    public long InboundReceivedOffset =>
        Interlocked.Read(ref _inboundReceivedOffset);

    public int BufferedBytes => Volatile.Read(ref _bufferedBytes);

    public Task Completion => _localReaderTask;

    public void ValidatePeerReceivedOffset(long offset)
    {
        lock (_stateGate)
        {
            if (offset < _outboundAcknowledgedOffset ||
                offset > _outboundProducedOffset)
            {
                throw new InvalidDataException(
                    "The peer requested an unavailable LAN relay offset.");
            }
        }
    }

    public async Task AttachAsync(
        Stream transport,
        long peerReceivedOffset,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ThrowIfDisposing();
        if (!_attachmentGate.Wait(0, token))
        {
            ThrowIfDisposing();
            throw new InvalidOperationException(
                "The resumable LAN relay tunnel already has an active transport.");
        }

        try
        {
            ThrowIfDisposing();
            ThrowIfTerminal();
            ApplyAcknowledgement(peerReceivedOffset);
            Pulse(_dataAvailable);

            using var connection =
                CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
            using var writeGate = new SemaphoreSlim(1, 1);
            var sendTask = SendBufferedDataAsync(
                transport,
                peerReceivedOffset,
                writeGate,
                connection.Token);
            var receiveTask = ReceiveFramesAsync(
                transport,
                writeGate,
                connection.Token);
            var heartbeatTask = SendHeartbeatsAsync(
                transport,
                writeGate,
                connection.Token);

            Exception? failure = null;
            var failureDirection = "transport";
            try
            {
                var first = await Task.WhenAny(
                    sendTask,
                    receiveTask,
                    heartbeatTask).ConfigureAwait(false);
                failureDirection = ReferenceEquals(first, sendTask)
                    ? "outbound"
                    : ReferenceEquals(first, receiveTask)
                        ? "inbound"
                        : "control";
                try
                {
                    await first.ConfigureAwait(false);
                    if (!_stop.IsCancellationRequested &&
                        !token.IsCancellationRequested)
                    {
                        failure = new EndOfStreamException(
                            "The resumable LAN relay transport ended.");
                    }
                }
                catch (Exception ex) when (
                    ex is IOException or
                    SocketException or
                    EndOfStreamException or
                    TimeoutException or
                    InvalidDataException or
                    OperationCanceledException or
                    ObjectDisposedException)
                {
                    if (!_stop.IsCancellationRequested &&
                        !token.IsCancellationRequested)
                    {
                        failure = ex;
                    }
                }
            }
            finally
            {
                try
                {
                    connection.Cancel();
                }
                catch
                {
                }
                await IgnoreConnectionEndAsync(
                    sendTask,
                    receiveTask,
                    heartbeatTask).ConfigureAwait(false);
            }

            if (failure is not null)
            {
                Emit(
                    "transport_lost",
                    failureDirection,
                    failure,
                    "");
                throw new IOException(
                    "The resumable LAN relay transport was interrupted.",
                    failure);
            }
            _stop.Token.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            _attachmentGate.Release();
        }
    }

    public void Stop(string reason)
    {
        reason = NormalizeReason(reason);
        var changed = false;
        lock (_stateGate)
        {
            if (_terminalReason.Length == 0)
            {
                _terminalReason = reason;
                changed = true;
            }
        }
        if (!changed) return;

        Emit("closed", "tunnel", null, reason);
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        Pulse(_dataAvailable);
        Pulse(_bufferSpaceAvailable);
    }

    private async Task ReadLocalMinecraftAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var capacity = GetAvailableBufferCapacity();
                while (capacity == 0)
                {
                    await _bufferSpaceAvailable.WaitAsync(token)
                        .ConfigureAwait(false);
                    capacity = GetAvailableBufferCapacity();
                }

                var requested = Math.Min(
                    LanRelayV2Protocol.MaxPayloadBytes,
                    capacity);
                var rented = ArrayPool<byte>.Shared.Rent(requested);
                int read;
                byte[]? payload = null;
                try
                {
                    read = await _minecraftStream.ReadAsync(
                        rented.AsMemory(0, requested),
                        token).ConfigureAwait(false);
                    if (read > 0)
                    {
                        payload = GC.AllocateUninitializedArray<byte>(read);
                        rented.AsSpan(0, read).CopyTo(payload);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }

                if (read == 0)
                {
                    lock (_stateGate)
                    {
                        _localEof = true;
                    }
                    Pulse(_dataAvailable);
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        token).ConfigureAwait(false);
                    return;
                }

                long startOffset;
                lock (_stateGate)
                {
                    startOffset = _outboundProducedOffset;
                    _outbound.AddLast(
                        new BufferedSegment(startOffset, payload!));
                    _outboundProducedOffset = checked(
                        _outboundProducedOffset + payload!.Length);
                    _bufferedBytes = checked(
                        _bufferedBytes + payload.Length);
                }
                Pulse(_dataAvailable);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            Emit("local_stream_failed", "outbound", ex, "local_io_error");
            Stop("local_io_error");
        }
        catch (ObjectDisposedException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Emit("local_stream_failed", "outbound", ex, "local_stream_failed");
            Stop("local_stream_failed");
        }
    }

    private async Task SendBufferedDataAsync(
        Stream transport,
        long nextOffset,
        SemaphoreSlim writeGate,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var frame = GetDataFrame(nextOffset);
            if (frame is null)
            {
                if (IsLocalEofReady(nextOffset))
                {
                    await WriteFrameAsync(
                        transport,
                        LanRelayV2Frame.Close(
                            InboundReceivedOffset,
                            Encoding.UTF8.GetBytes("local_eof")),
                        writeGate,
                        token).ConfigureAwait(false);
                    Stop("local_eof");
                    return;
                }
                await _dataAvailable.WaitAsync(token).ConfigureAwait(false);
                continue;
            }

            await WriteFrameAsync(
                transport,
                frame,
                writeGate,
                token).ConfigureAwait(false);
            nextOffset = checked(frame.Offset + frame.Payload.Length);
        }
    }

    private async Task ReceiveFramesAsync(
        Stream transport,
        SemaphoreSlim writeGate,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            LanRelayV2Frame frame;
            using (var idle =
                   CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                idle.CancelAfter(LanRelayV2Protocol.TransportTimeout);
                try
                {
                    frame = await LanRelayV2Protocol.ReadFrameAsync(
                        transport,
                        idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    !token.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "The resumable LAN relay heartbeat timed out.");
                }
            }

            switch (frame.Type)
            {
                case LanRelayV2FrameType.Data:
                    await ReceiveDataAsync(
                        transport,
                        frame,
                        writeGate,
                        token).ConfigureAwait(false);
                    break;
                case LanRelayV2FrameType.Ack:
                    ApplyAcknowledgement(frame.Offset);
                    break;
                case LanRelayV2FrameType.Heartbeat:
                    ApplyAcknowledgement(frame.Offset);
                    break;
                case LanRelayV2FrameType.Close:
                    ApplyAcknowledgement(frame.Offset);
                    var reason = frame.Payload.Length == 0
                        ? "peer_close"
                        : NormalizeReason(
                            System.Text.Encoding.UTF8.GetString(frame.Payload));
                    Stop(reason);
                    return;
                default:
                    throw new InvalidDataException(
                        "Unknown resumable LAN relay frame.");
            }
        }
    }

    private async Task ReceiveDataAsync(
        Stream transport,
        LanRelayV2Frame frame,
        SemaphoreSlim writeGate,
        CancellationToken token)
    {
        var receivedOffset = InboundReceivedOffset;
        var frameEnd = checked(frame.Offset + frame.Payload.Length);
        if (frame.Offset > receivedOffset)
        {
            throw new InvalidDataException(
                "The resumable LAN relay stream has an offset gap.");
        }

        if (frameEnd <= receivedOffset)
        {
            await WriteFrameAsync(
                transport,
                LanRelayV2Frame.Ack(receivedOffset),
                writeGate,
                token).ConfigureAwait(false);
            return;
        }

        var skip = checked((int)(receivedOffset - frame.Offset));
        if (skip < frame.Payload.Length)
        {
            var payload = frame.Payload.AsMemory(skip);
            try
            {
                if (_beforeWriteMinecraftForTesting is not null)
                {
                    await _beforeWriteMinecraftForTesting(
                        payload,
                        _stop.Token).ConfigureAwait(false);
                }
                await _minecraftStream.WriteAsync(
                    payload,
                    _stop.Token).ConfigureAwait(false);
                await _minecraftStream.FlushAsync(_stop.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (
                _stop.IsCancellationRequested &&
                ex is IOException or SocketException or ObjectDisposedException)
            {
                throw new OperationCanceledException(
                    "The resumable LAN relay tunnel stopped during a local write.",
                    ex,
                    _stop.Token);
            }
            catch (Exception ex) when (
                ex is IOException or SocketException or ObjectDisposedException)
            {
                Emit(
                    "local_stream_failed",
                    "inbound",
                    ex,
                    "local_io_error");
                Stop("local_io_error");
                throw new OperationCanceledException(
                    "The local Minecraft stream failed during relay delivery.",
                    ex,
                    _stop.Token);
            }
            Interlocked.Exchange(ref _inboundReceivedOffset, frameEnd);
        }

        await WriteFrameAsync(
            transport,
            LanRelayV2Frame.Ack(InboundReceivedOffset),
            writeGate,
            token).ConfigureAwait(false);
    }

    private async Task SendHeartbeatsAsync(
        Stream transport,
        SemaphoreSlim writeGate,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await _clock.DelayAsync(
                LanRelayV2Protocol.HeartbeatInterval,
                token).ConfigureAwait(false);
            await WriteFrameAsync(
                transport,
                LanRelayV2Frame.Heartbeat(InboundReceivedOffset),
                writeGate,
                token).ConfigureAwait(false);
        }
    }

    private async ValueTask WriteFrameAsync(
        Stream transport,
        LanRelayV2Frame frame,
        SemaphoreSlim writeGate,
        CancellationToken token)
    {
        await writeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var writeTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(token);
            writeTimeout.CancelAfter(LanRelayV2Protocol.TransportTimeout);
            try
            {
                if (_beforeWriteFrame is not null)
                {
                    await _beforeWriteFrame(
                        frame,
                        writeTimeout.Token).ConfigureAwait(false);
                }
                await LanRelayV2Protocol.WriteFrameAsync(
                    transport,
                    frame,
                    writeTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !token.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "The resumable LAN relay frame write timed out.");
            }
        }
        finally
        {
            writeGate.Release();
        }
    }

    private LanRelayV2Frame? GetDataFrame(long nextOffset)
    {
        lock (_stateGate)
        {
            if (nextOffset < _outboundAcknowledgedOffset ||
                nextOffset > _outboundProducedOffset)
            {
                throw new InvalidDataException(
                    "The peer requested an unavailable LAN relay offset.");
            }

            foreach (var segment in _outbound)
            {
                var segmentEnd = checked(
                    segment.Offset + segment.Payload.Length);
                if (nextOffset >= segmentEnd) continue;
                if (nextOffset < segment.Offset)
                {
                    throw new InvalidDataException(
                        "The LAN relay resend buffer has an offset gap.");
                }

                var skip = checked((int)(nextOffset - segment.Offset));
                if (skip == 0)
                {
                    return LanRelayV2Frame.Data(
                        segment.Offset,
                        segment.Payload);
                }
                return LanRelayV2Frame.Data(
                    nextOffset,
                    segment.Payload[skip..]);
            }
            return null;
        }
    }

    private void ApplyAcknowledgement(long offset)
    {
        var freed = 0;
        lock (_stateGate)
        {
            if (offset < _outboundAcknowledgedOffset)
            {
                return;
            }
            if (offset > _outboundProducedOffset)
            {
                throw new InvalidDataException(
                    "The peer acknowledged unavailable LAN relay data.");
            }

            _outboundAcknowledgedOffset = offset;
            while (_outbound.First is not null)
            {
                var firstNode = _outbound.First;
                var first = firstNode.Value;
                var end = checked(first.Offset + first.Payload.Length);
                if (end <= offset)
                {
                    freed = checked(freed + first.Payload.Length);
                    _outbound.RemoveFirst();
                    continue;
                }
                if (offset > first.Offset)
                {
                    var acknowledgedPrefix = checked(
                        (int)(offset - first.Offset));
                    firstNode.Value = new BufferedSegment(
                        offset,
                        first.Payload[acknowledgedPrefix..]);
                    freed = checked(freed + acknowledgedPrefix);
                }
                break;
            }
            if (freed > 0)
            {
                _bufferedBytes -= freed;
            }
        }
        if (freed > 0) Pulse(_bufferSpaceAvailable);
    }

    private int GetAvailableBufferCapacity()
    {
        lock (_stateGate)
        {
            if (_outbound.Count >=
                LanRelayV2Protocol.MaxBufferedSegmentsPerDirection)
            {
                return 0;
            }
            return Math.Max(
                0,
                LanRelayV2Protocol.MaxBufferedBytesPerDirection -
                _bufferedBytes);
        }
    }

    private bool IsLocalEofReady(long nextOffset)
    {
        lock (_stateGate)
        {
            return _localEof &&
                   nextOffset == _outboundProducedOffset;
        }
    }

    private void ThrowIfDisposing()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeStarted) != 0,
            this);
    }

    private void ThrowIfTerminal()
    {
        var reason = TerminalReason;
        if (reason.Length > 0)
        {
            throw new OperationCanceledException(
                $"The resumable LAN relay tunnel ended: {reason}");
        }
    }

    private void Emit(
        string phase,
        string direction,
        Exception? error,
        string terminalReason)
    {
        try
        {
            _signal(new LanRelayTunnelSignal(
                _clock.UtcNow,
                phase,
                direction,
                OutboundProducedOffset,
                OutboundAcknowledgedOffset,
                InboundReceivedOffset,
                BufferedBytes,
                DescribeError(error),
                terminalReason));
        }
        catch
        {
        }
    }

    private static string DescribeError(Exception? error)
    {
        if (error is null) return "";
        Exception? current = error;
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
            ? $"{error.GetType().Name}: {error.Message}"
            : $"{error.GetType().Name}/{socket.SocketErrorCode}: {error.Message}";
    }

    private static string NormalizeReason(string? value)
    {
        var reason = value?.Trim() ?? "";
        if (reason.Length == 0) return "closed";
        return reason.Length <= 128 ? reason : reason[..128];
    }

    private static void Pulse(SemaphoreSlim semaphore)
    {
        try
        {
            semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task IgnoreConnectionEndAsync(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? starter = null;
        Task disposeTask;
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                Volatile.Write(ref _disposeStarted, 1);
                starter = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = starter.Task;
            }
            disposeTask = _disposeTask;
        }
        if (starter is not null)
        {
            _ = CompleteDisposeAsync(starter);
        }
        return new ValueTask(disposeTask);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Stop(TerminalReason.Length == 0 ? "disposed" : TerminalReason);
        try
        {
            await _localReaderTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException or
            SocketException or
            OperationCanceledException or
            ObjectDisposedException)
        {
        }
        await _attachmentGate.WaitAsync(CancellationToken.None)
            .ConfigureAwait(false);
        _stopRegistration.Dispose();
        _stop.Dispose();
    }

    private sealed record BufferedSegment(long Offset, byte[] Payload);
}
