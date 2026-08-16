using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using Steamworks;

namespace Minecraft;

/// <summary>
/// A Steam P2P connection seen as an ordinary duplex stream, so every protocol
/// the launcher already speaks - world transfer, waypoints, skins, diagnostics -
/// works over Steam without knowing it. Steam guarantees reliable, ordered
/// delivery of whole messages; this splits writes into messages and glues the
/// received ones back into a byte stream.
///
/// Reading is fed by the transport's pump thread rather than pulled here:
/// Steam delivers messages per connection, and only one thread may drain them.
/// </summary>
internal sealed class SteamConnectionStream : Stream, IQueuedByteSink
{
    /// <summary>
    /// Steam's own message limit is 512 KiB; half of that keeps each send well
    /// inside it after framing and leaves room for the buffer to drain.
    /// </summary>
    internal const int MaxMessageBytes = 256 * 1024;

    /// <summary>Stop feeding the send buffer past this; the peer is behind.</summary>
    internal const int PendingReliableLimitBytes = 8 * 1024 * 1024;

    // Task.Delay rounds up to the system timer, which on Windows is about
    // 15.6 ms unless something has asked for better. Two milliseconds was
    // therefore never two milliseconds: at one 256 KiB message per tick this
    // capped the connection near 16 MiB/s no matter what rate Steam permitted -
    // invisible at today's 8 MiB/s and the first wall we would hit on raising
    // it. A periodic timer keeps the intent without the rounding.
    private static readonly TimeSpan SendRetryDelay = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan SendRetryPollFloor = TimeSpan.FromMicroseconds(250);

    private readonly HSteamNetConnection _connection;
    private readonly Pipe _inbound;
    private readonly Action _onDispose;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposed;

    /// <summary>
    /// Steam's account of why this connection ended. It used to reach only the
    /// reader, as the exception completing the inbound pipe - so a sender,
    /// which is writing and never reading, saw nothing but its next message
    /// being refused and reported "k_EResultNoConnection", the symptom.
    /// </summary>
    private volatile string? _endReason;

    internal void RecordEndReason(string? reason) =>
        _endReason = string.IsNullOrWhiteSpace(reason) ? null : reason;

    internal SteamConnectionStream(HSteamNetConnection connection, Action onDispose)
    {
        _connection = connection;
        _onDispose = onDispose;
        _inbound = new Pipe(new PipeOptions(
            pauseWriterThreshold: 16L * 1024 * 1024,
            resumeWriterThreshold: 4L * 1024 * 1024,
            useSynchronizationContext: false));
    }

    /// <summary>The pump writes received messages here; the reader side is this stream.</summary>
    internal PipeWriter InboundWriter => _inbound.Writer;

    private PipeReader Reader => _inbound.Reader;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        while (true)
        {
            var result = await Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var sequence = result.Buffer;
            if (sequence.Length > 0)
            {
                var count = (int)Math.Min(sequence.Length, buffer.Length);
                sequence.Slice(0, count).CopyTo(buffer.Span);
                Reader.AdvanceTo(sequence.GetPosition(count));
                return count;
            }

            Reader.AdvanceTo(sequence.Start, sequence.End);
            if (result.IsCompleted) return 0;
            if (result.IsCanceled) throw new IOException("The Steam connection was closed while reading.");
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var remaining = buffer;
            while (!remaining.IsEmpty)
            {
                var chunk = remaining[..Math.Min(MaxMessageBytes, remaining.Length)];
                await SendChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
                remaining = remaining[chunk.Length..];
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) return Task.CompletedTask;
        SteamNetworkingSockets.FlushMessagesOnConnection(_connection);
        return Task.CompletedTask;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>
    /// Hands one chunk to Steam, waiting when the send buffer is full: a world
    /// is gigabytes and the sender is always faster than the wire.
    /// </summary>
    private async Task SendChunkAsync(ReadOnlyMemory<byte> chunk, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(chunk.Length);
        var handle = default(GCHandle);
        try
        {
            chunk.Span.CopyTo(buffer);
            handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

                if (GetPendingReliableBytes() > PendingReliableLimitBytes)
                {
                    await WaitForDrainAsync(token).ConfigureAwait(false);
                    continue;
                }

                var result = SteamNetworkingSockets.SendMessageToConnection(
                    _connection,
                    handle.AddrOfPinnedObject(),
                    (uint)chunk.Length,
                    Constants.k_nSteamNetworkingSend_Reliable,
                    out _);
                switch (result)
                {
                    case EResult.k_EResultOK:
                        return;
                    case EResult.k_EResultLimitExceeded:
                        await Task.Delay(SendRetryDelay, token).ConfigureAwait(false);
                        continue;
                    default:
                        throw new IOException(_endReason is null
                            ? $"Steam refused a message: {result}."
                            : $"Steam ended the connection: {_endReason}");
                }
            }
        }
        finally
        {
            if (handle.IsAllocated) handle.Free();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>What Steam has taken from us and not yet put on the wire.</summary>
    public long QueuedBytes => GetPendingReliableBytes();

    public string DescribeLink() => SteamPeerTransport.DescribeLink(_connection);

    /// <summary>
    /// Waits for the send queue to drain a little without paying the system
    /// timer's rounding on every message.
    /// </summary>
    private static async Task WaitForDrainAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(SendRetryDelay);
        if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            await Task.Delay(SendRetryPollFloor, token).ConfigureAwait(false);
        }
    }

    private int GetPendingReliableBytes()
    {
        var status = default(SteamNetConnectionRealTimeStatus_t);
        var lanes = default(SteamNetConnectionRealTimeLaneStatus_t);
        return SteamNetworkingSockets.GetConnectionRealTimeStatus(_connection, ref status, 0, ref lanes) ==
               EResult.k_EResultOK
            ? status.m_cbPendingReliable
            : 0;
    }

    /// <summary>Called by the pump when Steam reports the connection is gone.</summary>
    internal void CompleteInbound(Exception? failure = null)
    {
        try
        {
            _inbound.Writer.Complete(failure);
        }
        catch (InvalidOperationException)
        {
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CompleteInbound();
            _onDispose();
            _writeGate.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            // Let Steam push out what is already queued before tearing the
            // connection down; the final ack of a transfer lives in there.
            SteamNetworkingSockets.FlushMessagesOnConnection(_connection);
            CompleteInbound();
            _onDispose();
            _writeGate.Dispose();
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
