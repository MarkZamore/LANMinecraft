using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Minecraft;
using static Minecraft.Tests.NetworkTestData;

namespace Minecraft.Tests;

public sealed class VoiceChannelLifecycleTests
{
    [Fact]
    public async Task FailedTransportStart_RollsBackPlaybackAndAllowsRetry()
    {
        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"minecraft-voice-channel-{Guid.NewGuid():N}.log");
        var coordinator = new VoiceNetworkCoordinator();
        var audioFactory = new FakeVoiceJoinAudioFactory();
        var service = new VoiceChannelService(
            new Logger(logPath),
            coordinator,
            new VoiceRuntimeOptions(),
            new FakeSelectedNetworkTransport(),
            routes: null,
            audioFactory);
        service.Initialize(new AppSettings
        {
            LocalIdentityId = Guid.NewGuid().ToString("D"),
            LocalIdentityName = "Test player"
        });

        try
        {
            Assert.Throws<SocketException>(() => service.Join());

            Assert.False(service.IsJoined);
            Assert.False(coordinator.Snapshot.IsJoined);
            var firstPlayback = Assert.Single(audioFactory.CreatedPlaybacks);
            Assert.True(firstPlayback.Started);
            Assert.True(firstPlayback.Disposed);

            Assert.Throws<SocketException>(() => service.Join());

            Assert.Equal(2, audioFactory.CreatedPlaybacks.Count);
            Assert.All(
                audioFactory.CreatedPlaybacks,
                playback => Assert.True(playback.Disposed));
        }
        finally
        {
            await service.DisposeAsync();
            if (File.Exists(logPath)) File.Delete(logPath);
        }
    }

    [Fact]
    public async Task LeaveCompletesBeforeConcurrentJoin_CanPublishNewTransport()
    {
        var logPath = GetTemporaryLogPath();
        var coordinator = new VoiceNetworkCoordinator();
        var audioFactory = new FakeVoiceJoinAudioFactory();
        var network = new SuccessfulVoiceNetworkTransport();
        var service = CreateService(
            logPath,
            coordinator,
            network,
            audioFactory);
        var releaseOldLeave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            service.Join();
            SetHeartbeatTask(service, releaseOldLeave.Task);

            var oldLeave = service.LeaveAsync().AsTask();
            Assert.True(SpinWait.SpinUntil(
                () => !service.IsJoined,
                TimeSpan.FromSeconds(2)));

            var newJoin = Task.Run(service.Join, timeout.Token);
            await Task.Delay(100, timeout.Token);
            Assert.False(newJoin.IsCompleted);

            releaseOldLeave.TrySetResult();
            await oldLeave.WaitAsync(timeout.Token);
            await newJoin.WaitAsync(timeout.Token);

            Assert.True(service.IsJoined);
            Assert.Equal(2, network.CreatedSockets.Count);
            Assert.NotNull(network.CreatedSockets[^1].Client);

            await service.LeaveAsync().AsTask().WaitAsync(timeout.Token);
            Assert.All(
                network.CreatedSockets,
                socket => Assert.Null(socket.Client));
            Assert.All(
                audioFactory.CreatedPlaybacks,
                playback => Assert.Equal(1, playback.DisposeCount));
        }
        finally
        {
            releaseOldLeave.TrySetResult();
            await service.DisposeAsync();
            DeleteIfExists(logPath);
        }
    }

    [Fact]
    public async Task UnexpectedBackgroundFault_DoesNotSkipLeaveCleanup()
    {
        var logPath = GetTemporaryLogPath();
        var coordinator = new VoiceNetworkCoordinator();
        var audioFactory = new FakeVoiceJoinAudioFactory();
        var network = new SuccessfulVoiceNetworkTransport();
        var service = CreateService(
            logPath,
            coordinator,
            network,
            audioFactory);

        try
        {
            service.Join();
            var originalHeartbeat = GetHeartbeatTask(service);
            SetHeartbeatTask(
                service,
                Task.WhenAll(
                    originalHeartbeat,
                    Task.FromException(new InvalidOperationException(
                        "Injected voice background failure."))));

            await service.LeaveAsync();

            Assert.False(service.IsJoined);
            Assert.False(coordinator.Snapshot.IsJoined);
            Assert.Equal(
                1,
                Assert.Single(audioFactory.CreatedPlaybacks).DisposeCount);
            Assert.Null(Assert.Single(network.CreatedSockets).Client);
        }
        finally
        {
            await service.DisposeAsync();
            DeleteIfExists(logPath);
        }
    }

    private static VoiceChannelService CreateService(
        string logPath,
        VoiceNetworkCoordinator coordinator,
        ISelectedNetworkTransport network,
        IVoiceJoinAudioFactory audioFactory)
    {
        var service = new VoiceChannelService(
            new Logger(logPath),
            coordinator,
            new VoiceRuntimeOptions(),
            network,
            routes: null,
            audioFactory);
        service.Initialize(new AppSettings
        {
            LocalIdentityId = Guid.NewGuid().ToString("D"),
            LocalIdentityName = "Test player"
        });
        return service;
    }

    private static Task GetHeartbeatTask(VoiceChannelService service) =>
        (Task)(GetHeartbeatTaskField().GetValue(service) ??
               throw new InvalidOperationException("Voice heartbeat was not started."));

    private static void SetHeartbeatTask(
        VoiceChannelService service,
        Task task) =>
        GetHeartbeatTaskField().SetValue(service, task);

    private static FieldInfo GetHeartbeatTaskField() =>
        typeof(VoiceChannelService).GetField(
            "_heartbeatTask",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException("Voice heartbeat field was not found.");

    private static string GetTemporaryLogPath() =>
        Path.Combine(
            Path.GetTempPath(),
            $"minecraft-voice-channel-{Guid.NewGuid():N}.log");

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed class FakeVoiceJoinAudioFactory : IVoiceJoinAudioFactory
    {
        private readonly object _gate = new();
        private readonly List<FakeVoicePlaybackSession> _createdPlaybacks = [];

        public IReadOnlyList<FakeVoicePlaybackSession> CreatedPlaybacks
        {
            get
            {
                lock (_gate) return _createdPlaybacks.ToArray();
            }
        }

        public VoiceJoinAudioSession Create(
            string requestedInputDeviceId,
            string requestedOutputDeviceId,
            double outputVolume)
        {
            var playback = new FakeVoicePlaybackSession();
            lock (_gate) _createdPlaybacks.Add(playback);
            return new VoiceJoinAudioSession(
                "test-input",
                "test-output",
                playback);
        }
    }

    private sealed class FakeVoicePlaybackSession : IVoicePlaybackSession
    {
        public event Action<string, bool>? SpeakingStateChanged
        {
            add { }
            remove { }
        }

        public bool Started { get; private set; }
        public bool Disposed => DisposeCount > 0;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        private int _disposeCount;

        public void Start() => Started = true;
        public void SetDeafened(bool deafened)
        {
        }
        public void SetMasterVolume(double volume)
        {
        }
        public void SetPeerVolume(string peerId, double volume)
        {
        }
        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
        }
    }

    private sealed class SuccessfulVoiceNetworkTransport : ISelectedNetworkTransport
    {
        private readonly NetworkEndpointInfo _endpoint =
            Endpoint("selected-interface", "127.0.0.1", 1);
        private readonly ConcurrentQueue<UdpClient> _createdSockets = new();

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
            throw new InvalidOperationException("TCP is not used by voice tests.");

        public UdpClient CreateBoundUdpClient(
            NetworkEndpointInfo endpoint,
            int port,
            bool reuseAddress)
        {
            var socket = new UdpClient(
                new IPEndPoint(IPAddress.Loopback, 0));
            _createdSockets.Enqueue(socket);
            return socket;
        }

        public Task<IReadOnlyList<IPAddress>> GetDynamicPeerTargetsAsync(
            NetworkEnvironmentSnapshot snapshot,
            CancellationToken token) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([]);
    }
}
