using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Minecraft;

namespace Minecraft.Tests;

public sealed class LanRelayV2EngineTests
{
    [Theory]
    [InlineData(99, 0L, 1)]
    [InlineData((int)LanRelayV2FrameType.Ack, 0L, 1)]
    [InlineData((int)LanRelayV2FrameType.Heartbeat, 0L, 1)]
    [InlineData((int)LanRelayV2FrameType.Data, -1L, 1)]
    [InlineData((int)LanRelayV2FrameType.Data, long.MaxValue, 1)]
    [InlineData(
        (int)LanRelayV2FrameType.Close,
        0L,
        LanRelayV2Protocol.MaxCloseReasonBytes + 1)]
    public async Task Protocol_RejectsInvalidHeaderBeforeReadingBody(
        int frameType,
        long offset,
        int payloadLength)
    {
        var header = new byte[13];
        header[0] = checked((byte)frameType);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(1, 8), offset);
        BinaryPrimitives.WriteInt32BigEndian(
            header.AsSpan(9, 4),
            payloadLength);
        await using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LanRelayV2Protocol.ReadFrameAsync(
                stream,
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Protocol_BoundsCloseReasonTo128WireBytes()
    {
        Assert.Equal(128, LanRelayV2Protocol.MaxCloseReasonBytes);
        Assert.Equal(
            128,
            LanRelayV2Protocol.MaxBufferedSegmentsPerDirection);
        await using var accepted = new MemoryStream();
        await LanRelayV2Protocol.WriteFrameAsync(
            accepted,
            LanRelayV2Frame.Close(
                0,
                new byte[LanRelayV2Protocol.MaxCloseReasonBytes]),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LanRelayV2Protocol.WriteFrameAsync(
                new MemoryStream(),
                LanRelayV2Frame.Close(
                    0,
                    new byte[
                        LanRelayV2Protocol.MaxCloseReasonBytes + 1]),
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task PartialAcknowledgement_FreesPrefixAndReplaysSuffix()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = timeout.Token;
        var payload = Enumerable.Range(0, 257)
            .Select(index => (byte)index)
            .ToArray();
        const int acknowledgedPrefix = 17;
        var (minecraftPeer, engineMinecraft) =
            await CreateConnectedPairAsync(token);
        using (minecraftPeer)
        await using (var tunnel = new LanRelayV2Tunnel(
                         engineMinecraft,
                         SystemNetworkClock.Instance,
                         _ => { },
                         token))
        {
            await minecraftPeer.GetStream().WriteAsync(payload, token);
            await WaitUntilAsync(
                () => tunnel.OutboundProducedOffset == payload.Length,
                token);

            var (transportPeer, transportEngine) =
                await CreateConnectedPairAsync(token);
            using (transportPeer)
            using (transportEngine)
            {
                var attachment = tunnel.AttachAsync(
                    transportEngine.GetStream(),
                    acknowledgedPrefix,
                    token);
                await WaitUntilAsync(
                    () => tunnel.OutboundAcknowledgedOffset ==
                          acknowledgedPrefix,
                    token);
                Assert.Equal(
                    payload.Length - acknowledgedPrefix,
                    tunnel.BufferedBytes);

                var replayed = new List<byte>();
                long? firstOffset = null;
                while (replayed.Count < payload.Length - acknowledgedPrefix)
                {
                    var frame = await LanRelayV2Protocol.ReadFrameAsync(
                        transportPeer.GetStream(),
                        token);
                    if (frame.Type != LanRelayV2FrameType.Data) continue;
                    firstOffset ??= frame.Offset;
                    replayed.AddRange(frame.Payload);
                }

                Assert.Equal(acknowledgedPrefix, firstOffset);
                Assert.Equal(
                    payload[acknowledgedPrefix..],
                    replayed.ToArray());
                tunnel.Stop("test_complete");
                await IgnoreConnectionEndAsync(attachment);
            }
        }
    }

    [Fact]
    public async Task LocalEof_CloseFailureRemainsResumableThenSendsClose()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(7));
        var token = timeout.Token;
        var payload = Encoding.UTF8.GetBytes(
            "buffered Minecraft tail before clean EOF");
        var closeAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var closeAttempts = 0;
        var (minecraftPeer, engineMinecraft) =
            await CreateConnectedPairAsync(token);
        using (minecraftPeer)
        await using (var tunnel = new LanRelayV2Tunnel(
                         engineMinecraft,
                         SystemNetworkClock.Instance,
                         _ => { },
                         token,
                         (frame, _) =>
                         {
                             if (frame.Type ==
                                     LanRelayV2FrameType.Close &&
                                 Interlocked.Increment(
                                     ref closeAttempts) == 1)
                             {
                                 closeAttempted.TrySetResult();
                                 throw new IOException(
                                     "Injected lost terminal frame.");
                             }
                             return ValueTask.CompletedTask;
                         }))
        {
            var (transportPeer1, transportEngine1) =
                await CreateConnectedPairAsync(token);
            using (transportPeer1)
            using (transportEngine1)
            {
                var firstAttachment = tunnel.AttachAsync(
                    transportEngine1.GetStream(),
                    0,
                    token);
                await minecraftPeer.GetStream().WriteAsync(payload, token);
                minecraftPeer.Client.Shutdown(SocketShutdown.Send);

                var received = new List<byte>();
                while (received.Count < payload.Length)
                {
                    var frame = await LanRelayV2Protocol.ReadFrameAsync(
                        transportPeer1.GetStream(),
                        token);
                    if (frame.Type == LanRelayV2FrameType.Data)
                    {
                        received.AddRange(frame.Payload);
                    }
                }
                Assert.Equal(payload, received.ToArray());
                await closeAttempted.Task.WaitAsync(token);
                await Assert.ThrowsAsync<IOException>(
                    () => firstAttachment);
            }

            Assert.False(tunnel.IsTerminal);
            Assert.Equal("", tunnel.TerminalReason);

            var (transportPeer2, transportEngine2) =
                await CreateConnectedPairAsync(token);
            using (transportPeer2)
            using (transportEngine2)
            {
                var secondAttachment = tunnel.AttachAsync(
                    transportEngine2.GetStream(),
                    payload.Length,
                    token);
                var close = await LanRelayV2Protocol.ReadFrameAsync(
                    transportPeer2.GetStream(),
                    token);

                Assert.Equal(LanRelayV2FrameType.Close, close.Type);
                Assert.Equal(0, close.Offset);
                Assert.Equal(
                    "local_eof",
                    Encoding.UTF8.GetString(close.Payload));
                await WaitUntilAsync(() => tunnel.IsTerminal, token);
                Assert.Equal("local_eof", tunnel.TerminalReason);
                await IgnoreConnectionEndAsync(secondAttachment);
            }
        }
    }

    [Fact]
    public async Task UnexpectedWriterFailure_CancelsAndAwaitsReaderSibling()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = timeout.Token;
        var (minecraftPeer, engineMinecraft) =
            await CreateConnectedPairAsync(token);
        using (minecraftPeer)
        await using (var tunnel = new LanRelayV2Tunnel(
                         engineMinecraft,
                         SystemNetworkClock.Instance,
                         _ => { },
                         token,
                         (frame, _) => frame.Type ==
                                       LanRelayV2FrameType.Data
                             ? new ValueTask(Task.FromException(
                                 new InvalidOperationException(
                                     "Injected unexpected writer failure.")))
                             : ValueTask.CompletedTask))
        {
            await minecraftPeer.GetStream().WriteAsync(
                new byte[] { 1, 2, 3, 4 },
                token);
            await WaitUntilAsync(
                () => tunnel.OutboundProducedOffset == 4,
                token);
            await using var transport = new CancellationProbeStream();

            var attachment = tunnel.AttachAsync(transport, 0, token);
            await transport.ReadStarted.WaitAsync(token);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => attachment);
            await transport.ReadCanceled.WaitAsync(token);
        }
    }

    [Fact]
    public async Task ConcurrentDisposeCalls_AwaitTheSameTeardown()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = timeout.Token;
        var (minecraftPeer, engineMinecraft) =
            await CreateConnectedPairAsync(token);
        using (minecraftPeer)
        {
            var tunnel = new LanRelayV2Tunnel(
                engineMinecraft,
                SystemNetworkClock.Instance,
                _ => { },
                token);
            await using var transport = new ManuallyReleasedReadStream();
            var attachment = tunnel.AttachAsync(transport, 0, token);
            await transport.ReadStarted.WaitAsync(token);

            var firstDispose = tunnel.DisposeAsync().AsTask();
            var secondDispose = tunnel.DisposeAsync().AsTask();
            Assert.False(firstDispose.IsCompleted);
            Assert.False(secondDispose.IsCompleted);

            transport.Release();
            await Task.WhenAll(firstDispose, secondDispose).WaitAsync(token);
            await IgnoreConnectionEndAsync(attachment);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                tunnel.AttachAsync(
                    Stream.Null,
                    0,
                    CancellationToken.None));
        }
    }

    [Fact]
    public async Task LocalMinecraftWriteFailure_IsTerminalAndDirectional()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = timeout.Token;
        var signals = new ConcurrentQueue<LanRelayTunnelSignal>();
        var (minecraftPeer, engineMinecraft) =
            await CreateConnectedPairAsync(token);
        using (minecraftPeer)
        await using (var tunnel = new LanRelayV2Tunnel(
                         engineMinecraft,
                         SystemNetworkClock.Instance,
                         signals.Enqueue,
                         token,
                         beforeWriteMinecraftForTesting: (_, _) =>
                             new ValueTask(Task.FromException(
                                 new IOException(
                                     "Injected local write failure.")))))
        {
            var (transportPeer, transportEngine) =
                await CreateConnectedPairAsync(token);
            using (transportPeer)
            using (transportEngine)
            {
                var attachment = tunnel.AttachAsync(
                    transportEngine.GetStream(),
                    0,
                    token);
                await LanRelayV2Protocol.WriteFrameAsync(
                    transportPeer.GetStream(),
                    LanRelayV2Frame.Data(0, new byte[] { 42 }),
                    token);

                await WaitUntilAsync(() => tunnel.IsTerminal, token);
                Assert.Equal("local_io_error", tunnel.TerminalReason);
                Assert.Contains(
                    signals,
                    signal =>
                        signal.Phase == "local_stream_failed" &&
                        signal.Direction == "inbound" &&
                        signal.TerminalReason == "local_io_error");
                await IgnoreConnectionEndAsync(attachment);
            }
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken token)
    {
        while (!condition())
        {
            await Task.Delay(10, token);
        }
    }

    private static async Task<(TcpClient Peer, TcpClient Engine)>
        CreateConnectedPairAsync(CancellationToken token)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var peer = new TcpClient(AddressFamily.InterNetwork);
            var accept = listener.AcceptTcpClientAsync(token).AsTask();
            await peer.ConnectAsync(
                endpoint.Address,
                endpoint.Port,
                token);
            return (peer, await accept);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task IgnoreConnectionEndAsync(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task;
            }
            catch
            {
            }
        }
    }

    private sealed class CancellationProbeStream : Stream
    {
        private readonly TaskCompletionSource _readStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readCanceled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadStarted => _readStarted.Task;
        public Task ReadCanceled => _readCanceled.Task;
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
            _readStarted.TrySetResult();
            using var registration = cancellationToken.Register(
                () => _readCanceled.TrySetResult());
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            return 0;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class ManuallyReleasedReadStream : Stream
    {
        private readonly TaskCompletionSource _readStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadStarted => _readStarted.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Release() => _release.TrySetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult();
            await _release.Task;
            return 0;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
