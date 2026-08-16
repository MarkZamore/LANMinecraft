using System.Text;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The diagnostics session as the receiving launcher sees it. Authorisation
/// used to be a pinned TLS fingerprint plus a matching IP route; Steam
/// authenticates the account behind every connection, so what is pinned here is
/// that the frames may not claim to be anyone else.
/// </summary>
public sealed class PeerSupportLogServiceTests
{
    private const ulong LocalSteamId = 76561198000000001;
    private const ulong RemoteSteamId = 76561198000000002;

    [Fact]
    public async Task ReceiverRestart_RestoresProtocolStreamsBeforeNextData()
    {
        using var fixture = new TemporaryPortableRoot();
        var descriptor = new SupportLogSessionDescriptor(
            Guid.NewGuid(),
            RemoteSteamId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Remote",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>());
        var firstStorage = new SupportLogStorage(fixture.Paths);
        var first = await firstStorage.CreateSessionAsync(descriptor);
        var receiverName = await first.RegisterSourceAsync(
            new SupportLogStreamDescriptor(
                "stream_0000100",
                SupportLogSourceKind.Game,
                "latest.log"));
        await first.CommitAcceptedFrameAsync(
            1,
            new string('A', 64),
            _ => Task.CompletedTask);

        var restartedStorage = new SupportLogStorage(fixture.Paths);
        var resumed = await restartedStorage.CreateSessionAsync(descriptor);
        var payload = Encoding.UTF8.GetBytes("continued after reconnect\n");
        await PeerSupportLogService.AppendResumedFrameForTestingAsync(
            resumed,
            new PeerSupportFrame(
                PeerSupportFrameType.Data,
                100,
                2,
                1,
                payload));

        Assert.Contains(
            "continued after reconnect",
            await File.ReadAllTextAsync(
                Path.Combine(resumed.SessionDirectory, receiverName)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncomingPeerValidation_RequiresTheHelloToMatchTheSteamConnection()
    {
        using var fixture = new TemporaryPortableRoot();
        var now = DateTimeOffset.UtcNow;
        Assert.True(SteamId64.TryFrom(RemoteSteamId, out var remotePeer));
        await using var service = CreateService(fixture.Paths, now);
        service.ObservePeer(RemotePresence(remotePeer));

        var hello = NewHello(now);
        var context = new PeerConnectionContext(remotePeer, "Remote", IsFriend: true, now);

        service.ValidateIncomingPeerForTesting(hello, context);

        // A hello addressed to somebody else, or claiming another account, or
        // arriving from a non-friend, is refused.
        Assert.Throws<InvalidDataException>(() =>
            service.ValidateIncomingPeerForTesting(
                hello with { RecipientIdentityId = "76561198000000009" },
                context));
        Assert.Throws<InvalidDataException>(() =>
            service.ValidateIncomingPeerForTesting(
                hello with { SenderIdentityId = "76561198000000009" },
                context));
        Assert.Throws<InvalidDataException>(() =>
            service.ValidateIncomingPeerForTesting(
                hello,
                context with { IsFriend = false }));
        Assert.Throws<InvalidDataException>(() =>
            service.ValidateIncomingPeerForTesting(
                hello,
                context with { AcceptedAtUtc = now.AddMinutes(-5) }));
    }

    /// <summary>
    /// A friend whose rich presence has not been read yet is still a legitimate
    /// sender: Steam already proved who they are.
    /// </summary>
    [Fact]
    public async Task AnUnannouncedFriend_IsStillAcceptedAsASender()
    {
        using var fixture = new TemporaryPortableRoot();
        var now = DateTimeOffset.UtcNow;
        Assert.True(SteamId64.TryFrom(RemoteSteamId, out var remotePeer));
        await using var service = CreateService(fixture.Paths, now);

        service.ValidateIncomingPeerForTesting(
            NewHello(now),
            new PeerConnectionContext(remotePeer, "Remote", IsFriend: true, now));
    }

    [Fact]
    public async Task LosingSteam_ClearsTheSelectedTarget()
    {
        using var fixture = new TemporaryPortableRoot();
        var now = DateTimeOffset.UtcNow;
        Assert.True(SteamId64.TryFrom(RemoteSteamId, out var remotePeer));
        await using var service = CreateService(fixture.Paths, now);
        service.ObservePeer(RemotePresence(remotePeer));

        await service.SetTargetAsync(new DiagnosticLogTargetOption(remotePeer, "Remote"));
        Assert.Equal(remotePeer.ToString(), service.CurrentTargetIdentityId);

        await service.OnTransportUnavailableAsync("steam_unavailable");

        Assert.Empty(service.CurrentTargetIdentityId);
        Assert.Contains("Steam", service.StatusText, StringComparison.Ordinal);
    }

    /// <summary>A peer that speaks another diagnostics version is not a target.</summary>
    [Fact]
    public async Task APeerOnAnotherProtocolVersion_CancelsTheTarget()
    {
        using var fixture = new TemporaryPortableRoot();
        var now = DateTimeOffset.UtcNow;
        Assert.True(SteamId64.TryFrom(RemoteSteamId, out var remotePeer));
        await using var service = CreateService(fixture.Paths, now);
        service.ObservePeer(RemotePresence(remotePeer));
        await service.SetTargetAsync(new DiagnosticLogTargetOption(remotePeer, "Remote"));

        service.ObservePeer(RemotePresence(remotePeer) with { DiagnosticProtocolVersion = 99 });

        Assert.Empty(service.CurrentTargetIdentityId);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetTargetAsync(new DiagnosticLogTargetOption(remotePeer, "Remote")));
    }

    private static SteamPeerPresence RemotePresence(SteamId64 peer) => new()
    {
        SteamId = peer,
        PersonaName = "Remote",
        ProtocolVersion = SteamPresenceCodec.ProtocolVersion,
        PlayerName = "Remote",
        DiagnosticProtocolVersion = PeerSupportProtocol.ProtocolVersion
    };

    private static PeerSupportHello NewHello(DateTimeOffset now) => new()
    {
        SessionId = Guid.NewGuid(),
        SenderIdentityId = RemoteSteamId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        RecipientIdentityId = LocalSteamId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        StartedAtUtc = now
    };

    private static PeerSupportLogService CreateService(AppPaths paths, DateTimeOffset now) =>
        new(
            paths,
            new NullPeerTransport(),
            () => (LocalSteamId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Local"),
            () => null,
            _ => Task.FromResult(new SupportEnvironmentSnapshot(
                now,
                "22",
                "22",
                ".NET",
                "Windows",
                "X64",
                "Java",
                "Pack",
                "hash",
                [],
                SteamDiagnosticContext.Unavailable,
                new Dictionary<string, string>(),
                string.Empty)),
            () => new SupportNetworkMetrics(
                now,
                LocalSteamId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Ready",
                1,
                1,
                false,
                0,
                0,
                0,
                0,
                0,
                0,
                new Dictionary<string, string>()));

    private sealed class TemporaryPortableRoot : IDisposable
    {
        public TemporaryPortableRoot()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "MinecraftPeerSupportTests",
                Guid.NewGuid().ToString("N"));
            Paths = new AppPaths(Root);
            Paths.Ensure();
        }

        public string Root { get; }
        public AppPaths Paths { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
