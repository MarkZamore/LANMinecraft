using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Minecraft.Tests;

/// <summary>
/// Delivery is confirmed by the matching Stored acknowledgement, not by
/// writing all archive bytes or receiving permission to start the upload.
/// </summary>
public sealed class BugReportAcknowledgementTests : IDisposable
{
    private const ulong SenderId = 76561198256236531;
    private const ulong ReceiverId = 76561198050776152;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bug-report-ack-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public async Task SendingAllBytesDoesNotCompleteUntilTheReceiverConfirmsStored()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var handler = new ScriptedReceiver(holdStored: true);
        var (sender, router, recipient) = CreatePeers(handler);
        await using var routerScope = router;
        await router.StartAsync(timeout.Token);
        var sending = sender.SendAsync(recipient, "fixture report", progress: null, timeout.Token);
        try
        {
            var received = await handler.ArchiveReceived.Task.WaitAsync(timeout.Token);
            Assert.True(received.Bytes > 0);
            Assert.Equal(received.Manifest.ArchiveBytes, received.Bytes);
            Assert.Equal(received.Manifest.ArchiveSha256, received.Sha256);
            // Give a premature completion a chance to run after payload Flush.
            var completed = await Task.WhenAny(sending, Task.Delay(100, timeout.Token));
            Assert.NotSame(sending, completed);
            Assert.False(sending.IsCompleted);

            handler.ReleaseStored.TrySetResult();
            var sent = await sending.WaitAsync(timeout.Token);
            Assert.Equal(received.Manifest.ReportId, sent.ReportId);
            Assert.Equal(received.Manifest.ArchiveSha256, sent.ArchiveSha256);
        }
        finally
        {
            handler.ReleaseStored.TrySetResult();
            await handler.Finished.Task.WaitAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task MatchingAcknowledgementsFromACompatibleOlderProtocolStillConfirmDelivery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var handler = new ScriptedReceiver(wireVersion: BugReportManifest.ProtocolVersion - 1);
        var (sender, router, recipient) = CreatePeers(handler);
        await using var routerScope = router;
        await router.StartAsync(timeout.Token);

        var sent = await sender.SendAsync(recipient, "fixture report", progress: null, timeout.Token);
        var received = await handler.ArchiveReceived.Task.WaitAsync(timeout.Token);
        await handler.Finished.Task.WaitAsync(timeout.Token);

        Assert.True(PortableFormat.CanSpeak(BugReportManifest.ProtocolVersion - 1));
        Assert.Equal(received.Manifest.ReportId, sent.ReportId);
        Assert.Equal(received.Manifest.ArchiveSha256, received.Sha256);
    }

    [Theory]
    [InlineData("Accepted", "id")]
    [InlineData("Accepted", "protocol")]
    [InlineData("Accepted", "stage")]
    [InlineData("Accepted", "newer-version")]
    [InlineData("Accepted", "zero-version")]
    [InlineData("Accepted", "missing-protocol")]
    [InlineData("Accepted", "missing-version")]
    [InlineData("Accepted", "missing-stage")]
    [InlineData("Accepted", "missing-id")]
    [InlineData("Stored", "id")]
    [InlineData("Stored", "protocol")]
    [InlineData("Stored", "stage")]
    [InlineData("Stored", "newer-version")]
    [InlineData("Stored", "zero-version")]
    [InlineData("Stored", "missing-protocol")]
    [InlineData("Stored", "missing-version")]
    [InlineData("Stored", "missing-stage")]
    [InlineData("Stored", "missing-id")]
    public async Task APositiveAcknowledgementMustMatchThisReportAndProtocolStage(string stage, string changed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var handler = new ScriptedReceiver(stage, changed);
        var (sender, router, recipient) = CreatePeers(handler);
        await using var routerScope = router;
        await router.StartAsync(timeout.Token);
        var progress = new InlineProgress();

        await Assert.ThrowsAsync<InvalidDataException>(() => sender.SendAsync(recipient, "fixture report", progress, timeout.Token));
        await handler.Finished.Task.WaitAsync(timeout.Token);

        if (stage == "Accepted") Assert.Empty(progress.Values);
        else Assert.True(handler.ArchiveReceived.Task.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData("Accepted", "rejected")]
    [InlineData("Accepted", "null")]
    [InlineData("Stored", "rejected")]
    [InlineData("Stored", "null")]
    public async Task RejectedOrMissingAcknowledgementsCannotConfirmDelivery(string stage, string changed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var handler = new ScriptedReceiver(stage, changed);
        var (sender, router, recipient) = CreatePeers(handler);
        await using var routerScope = router;
        await router.StartAsync(timeout.Token);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(recipient, "fixture report", progress: null, timeout.Token));
        await handler.Finished.Task.WaitAsync(timeout.Token);

        if (changed == "rejected") Assert.Contains("FIXTURE_REJECTED", error.Message, StringComparison.Ordinal);
        if (stage == "Stored") Assert.True(handler.ArchiveReceived.Task.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData("Accepted")]
    [InlineData("Stored")]
    public async Task ADisconnectedReceiverCannotConfirmDelivery(string stage)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var handler = new ScriptedReceiver(stage, "eof");
        var (sender, router, recipient) = CreatePeers(handler);
        await using var routerScope = router;
        await router.StartAsync(timeout.Token);

        await Assert.ThrowsAnyAsync<IOException>(() => sender.SendAsync(recipient, "fixture report", progress: null, timeout.Token));
        await handler.Finished.Task.WaitAsync(timeout.Token);

        if (stage == "Stored") Assert.True(handler.ArchiveReceived.Task.IsCompletedSuccessfully);
    }

    private (BugReportService Sender, PeerConnectionRouter Router, SteamId64 Recipient) CreatePeers(ScriptedReceiver handler)
    {
        var network = new InMemoryPeerNetwork();
        network.MakeFriends(SenderId, ReceiverId);
        var senderTransport = network.CreateTransport(SenderId, "Fixture sender");
        var receiverTransport = network.CreateTransport(ReceiverId, "Fixture recipient");
        var paths = new AppPaths(Path.Combine(_root, "sender"));
        paths.Ensure();
        Assert.True(SteamId64.TryFrom(SenderId, out var senderId));
        Assert.True(SteamId64.TryFrom(ReceiverId, out var recipient));
        var sender = new BugReportService(
            paths, new Logger(paths.LogFile), senderTransport,
            instanceDirectoryProvider: () => null,
            contextProvider: () => new BugReportContext(senderId, "Fixture sender", "Fixture player",
                "11111111-1111-4111-8111-111111111111", "fixture", "Fixture pack", new string('a', 64), false),
            environmentProvider: _ => Task.FromResult(new SupportEnvironmentSnapshot(
                DateTimeOffset.UnixEpoch, "fixture", "fixture", ".NET fixture", "OS fixture", "X64",
                "Java fixture", "Fixture pack", new string('a', 64), [], [], SteamDiagnosticContext.Unavailable,
                new Dictionary<string, string>(), "")),
            steamLogsDirectoryProvider: () => null);
        var router = new PeerConnectionRouter(receiverTransport);
        router.Register(handler);
        return (sender, router, recipient);
    }

    private sealed class InlineProgress : IProgress<BugReportProgress>
    {
        public List<BugReportProgress> Values { get; } = [];
        public void Report(BugReportProgress value) => Values.Add(value);
    }

    private sealed record ReceivedArchive(BugReportManifest Manifest, long Bytes, string Sha256);

    private sealed class ScriptedReceiver(
        string alteredStage = "",
        string changed = "",
        bool holdStored = false,
        int wireVersion = BugReportManifest.ProtocolVersion) : IPortableProtocolHandler
    {
        public string ProtocolName => BugReportManifest.ProtocolName;
        public TaskCompletionSource<ReceivedArchive> ArchiveReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStored { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task HandleAsync(Stream stream, byte[] initialFrame, PeerConnectionContext context, CancellationToken token)
        {
            try
            {
                var manifest = PortableProtocol.Deserialize<BugReportManifest>(initialFrame, JsonOptions)
                    ?? throw new InvalidDataException("The fixture received no manifest.");
                if (!await WriteAcknowledgementAsync(stream, manifest, "Accepted", token)) return;

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var remaining = manifest.ArchiveBytes;
                var buffer = new byte[4096];
                while (remaining > 0)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                    if (read == 0) throw new EndOfStreamException("The fixture archive ended early.");
                    hash.AppendData(buffer, 0, read);
                    remaining -= read;
                }
                ArchiveReceived.TrySetResult(new ReceivedArchive(manifest, manifest.ArchiveBytes,
                    Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
                if (holdStored) await ReleaseStored.Task.WaitAsync(token);
                _ = await WriteAcknowledgementAsync(stream, manifest, "Stored", token);
                Finished.TrySetResult();
            }
            catch (Exception error)
            {
                ArchiveReceived.TrySetException(error);
                Finished.TrySetException(error);
                throw;
            }
            finally { Finished.TrySetResult(); }
        }

        private async Task<bool> WriteAcknowledgementAsync(Stream stream, BugReportManifest manifest, string stage, CancellationToken token)
        {
            var alter = alteredStage == stage;
            if (alter && changed == "eof") return false;
            if (alter && changed == "null")
            {
                await PortableProtocol.WriteJsonAsync<BugReportAck?>(stream, null, JsonOptions, token);
                return false;
            }
            var ack = new BugReportAck
            {
                Ok = true,
                Stage = stage,
                ReportId = manifest.ReportId,
                Version = wireVersion
            };
            if (alter && changed.StartsWith("missing-", StringComparison.Ordinal))
            {
                var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    JsonSerializer.Serialize(ack, JsonOptions), JsonOptions)!;
                var field = changed["missing-".Length..];
                Assert.True(fields.Remove(field == "id" ? "reportId" : field));
                await PortableProtocol.WriteJsonAsync(stream, fields, JsonOptions, token);
                return false;
            }
            if (alter)
            {
                ack = changed switch
                {
                    "id" => ack with { ReportId = new string('0', 32) },
                    "protocol" => ack with { Protocol = "AnotherProtocol" },
                    "stage" => ack with { Stage = stage == "Accepted" ? "Stored" : "Accepted" },
                    "newer-version" => ack with { Version = BugReportManifest.ProtocolVersion + 1 },
                    "zero-version" => ack with { Version = 0 },
                    "rejected" => ack with { Ok = false, Stage = "Rejected", Message = "FIXTURE_REJECTED" },
                    _ => throw new InvalidOperationException("Unknown fixture acknowledgement change.")
                };
            }
            await PortableProtocol.WriteJsonAsync(stream, ack, JsonOptions, token);
            return !alter;
        }
    }
}
