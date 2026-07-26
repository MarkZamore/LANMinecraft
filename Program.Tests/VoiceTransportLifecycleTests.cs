using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Minecraft;
using static Minecraft.Tests.NetworkTestData;

namespace Minecraft.Tests;

public sealed class VoiceTransportLifecycleTests
{
    [Fact]
    public async Task ConcurrentStarts_AreSerialized_AndStopOwnsEveryCreatedSocket()
    {
        var transport = new BlockingVoiceNetworkTransport(
            Endpoint("selected-interface", "127.0.0.1", 1));
        var logPath = GetTemporaryLogPath();
        var voice = new VoiceTransport(new Logger(logPath), transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var secondStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var firstStart = Task.Run(
                () => voice.StartListening(
                    IPAddress.Any,
                    0,
                    static (_, _, _, _) => Task.CompletedTask),
                timeout.Token);
            await transport.FirstCreateEntered.Task.WaitAsync(timeout.Token);

            var secondStart = Task.Run(() =>
            {
                secondStarted.TrySetResult();
                voice.StartListening(
                    IPAddress.Any,
                    0,
                    static (_, _, _, _) => Task.CompletedTask);
            }, timeout.Token);
            await secondStarted.Task.WaitAsync(timeout.Token);
            await Task.Delay(100, timeout.Token);

            Assert.Equal(1, transport.CreateCallCount);

            transport.ReleaseFirstCreate();
            await Task.WhenAll(firstStart, secondStart).WaitAsync(timeout.Token);
            Assert.Equal(2, transport.CreateCallCount);

            await voice.StopAsync().AsTask().WaitAsync(timeout.Token);
            Assert.All(
                transport.CreatedSockets,
                socket => Assert.Null(socket.Client));
        }
        finally
        {
            transport.ReleaseFirstCreate();
            await voice.DisposeAsync();
            DeleteIfExists(logPath);
        }
    }

    [Fact]
    public async Task DisposedReceiveSocket_CompletesLoopWithoutCancellingSession()
    {
        var transport = new BlockingVoiceNetworkTransport(
            Endpoint("selected-interface", "127.0.0.1", 1),
            blockFirstCreate: false);
        var logPath = GetTemporaryLogPath();
        var voice = new VoiceTransport(new Logger(logPath), transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            voice.StartListening(
                IPAddress.Any,
                0,
                static (_, _, _, _) => Task.CompletedTask);
            var completion = voice.GetReceiveCompletionTask();

            Assert.False(completion.IsCompleted);
            Assert.Single(transport.CreatedSockets).Dispose();

            await completion.WaitAsync(timeout.Token);
            await voice.StopAsync().AsTask().WaitAsync(timeout.Token);
        }
        finally
        {
            await voice.DisposeAsync();
            DeleteIfExists(logPath);
        }
    }

    [Fact]
    public async Task SendSnapshot_WithConcurrentlyDisposedSocket_DoesNotThrow()
    {
        var endpoint = Endpoint("selected-interface", "127.0.0.1", 1);
        var transport = new BlockingVoiceNetworkTransport(
            endpoint,
            blockFirstCreate: false);
        var logPath = GetTemporaryLogPath();
        var voice = new VoiceTransport(new Logger(logPath), transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            voice.StartListening(
                IPAddress.Any,
                0,
                static (_, _, _, _) => Task.CompletedTask);
            Assert.Single(transport.CreatedSockets).Dispose();

            await voice.SendAsync(
                [
                    new VoiceRouteTarget(
                        new IPEndPoint(IPAddress.Loopback, 35657),
                        endpoint.NetworkAddress,
                        endpoint.InterfaceId)
                ],
                [1, 2, 3],
                timeout.Token).WaitAsync(timeout.Token);

            await voice.StopAsync().AsTask().WaitAsync(timeout.Token);
        }
        finally
        {
            await voice.DisposeAsync();
            DeleteIfExists(logPath);
        }
    }

    [Fact]
    public async Task DisposeAsync_WaitsForInFlightStart_ThenPreventsRestart()
    {
        var transport = new BlockingVoiceNetworkTransport(
            Endpoint("selected-interface", "127.0.0.1", 1));
        var logPath = GetTemporaryLogPath();
        var voice = new VoiceTransport(new Logger(logPath), transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var disposeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var start = Task.Run(
                () => voice.StartListening(
                    IPAddress.Any,
                    0,
                    static (_, _, _, _) => Task.CompletedTask),
                timeout.Token);
            await transport.FirstCreateEntered.Task.WaitAsync(timeout.Token);

            var dispose = Task.Run(async () =>
            {
                disposeStarted.TrySetResult();
                await voice.DisposeAsync();
            }, timeout.Token);
            await disposeStarted.Task.WaitAsync(timeout.Token);
            await Task.Delay(100, timeout.Token);
            Assert.False(dispose.IsCompleted);

            transport.ReleaseFirstCreate();
            await Task.WhenAll(start, dispose).WaitAsync(timeout.Token);

            Assert.All(
                transport.CreatedSockets,
                socket => Assert.Null(socket.Client));
            Assert.Throws<ObjectDisposedException>(() =>
                voice.StartListening(
                    IPAddress.Any,
                    0,
                    static (_, _, _, _) => Task.CompletedTask));
        }
        finally
        {
            transport.ReleaseFirstCreate();
            await voice.DisposeAsync();
            DeleteIfExists(logPath);
        }
    }

    private static string GetTemporaryLogPath() =>
        Path.Combine(
            Path.GetTempPath(),
            $"minecraft-voice-transport-{Guid.NewGuid():N}.log");

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed class BlockingVoiceNetworkTransport : ISelectedNetworkTransport
    {
        private readonly NetworkEndpointInfo _endpoint;
        private readonly bool _blockFirstCreate;
        private readonly ManualResetEventSlim _releaseFirstCreate = new(false);
        private readonly ConcurrentQueue<UdpClient> _createdSockets = new();
        private int _createCallCount;

        public BlockingVoiceNetworkTransport(
            NetworkEndpointInfo endpoint,
            bool blockFirstCreate = true)
        {
            _endpoint = endpoint;
            _blockFirstCreate = blockFirstCreate;
        }

        public TaskCompletionSource FirstCreateEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CreateCallCount => Volatile.Read(ref _createCallCount);

        public IReadOnlyList<UdpClient> CreatedSockets =>
            _createdSockets.ToArray();

        public NetworkEnvironmentSnapshot GetSnapshot() => new()
        {
            Endpoints = [_endpoint],
            PrimaryEndpoint = _endpoint
        };

        public TcpClient CreateBoundTcpClient(
            IPAddress remoteAddress,
            string? localAddress = null,
            string? localInterfaceId = null) =>
            throw new InvalidOperationException("TCP is not used by voice transport tests.");

        public UdpClient CreateBoundUdpClient(
            NetworkEndpointInfo endpoint,
            int port,
            bool reuseAddress)
        {
            var call = Interlocked.Increment(ref _createCallCount);
            if (call == 1)
            {
                FirstCreateEntered.TrySetResult();
                if (_blockFirstCreate)
                {
                    _releaseFirstCreate.Wait();
                }
            }

            var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            _createdSockets.Enqueue(socket);
            return socket;
        }

        public Task<IReadOnlyList<IPAddress>> GetDynamicPeerTargetsAsync(
            NetworkEnvironmentSnapshot snapshot,
            CancellationToken token) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([]);

        public void ReleaseFirstCreate() => _releaseFirstCreate.Set();
    }
}
