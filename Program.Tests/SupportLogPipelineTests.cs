using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class SupportLogPipelineTests
{
    [Fact]
    public void Sanitizer_RemovesSecretsUserContentAndLocalPaths()
    {
        var sanitizer = new SupportLogSanitizer(
            @"C:\Users\Alice",
            @"C:\Users\Alice\Portable Minecraft");
        var identity = Guid.NewGuid().ToString("D");
        var input = string.Join(
            Environment.NewLine,
            @"Authorization: Bearer secret-token-value",
            @"password=""two word password"" accessToken=abc123456",
            @"https://example.invalid/callback?refresh_token=refresh-secret&safe=1",
            $@"C:\Users\Alice\Portable Minecraft\Minecraft\Personal\logs.log ip=10.8.0.4 id={identity}",
            "[Render thread/INFO] [CHAT] private chat text",
            "[Server thread/INFO]: <Bob> standard server chat text",
            "[Server thread/INFO]: [Not Secure] <Carol> unsigned server chat text",
            """{"chat":"structured private chat text"}""",
            """{"command":"/give Bob op"}""",
            "[Server thread/INFO] Alice issued server command: /op Alice");

        var result = sanitizer.SanitizeText(input);

        Assert.DoesNotContain("secret-token-value", result, StringComparison.Ordinal);
        Assert.DoesNotContain("two word password", result, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123456", result, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-secret", result, StringComparison.Ordinal);
        Assert.DoesNotContain("private chat text", result, StringComparison.Ordinal);
        Assert.DoesNotContain("standard server chat text", result, StringComparison.Ordinal);
        Assert.DoesNotContain("unsigned server chat text", result, StringComparison.Ordinal);
        Assert.DoesNotContain("structured private chat text", result, StringComparison.Ordinal);
        Assert.DoesNotContain("/give Bob op", result, StringComparison.Ordinal);
        Assert.DoesNotContain("/op Alice", result, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\Alice", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<APP_ROOT>", result, StringComparison.Ordinal);
        Assert.Contains("10.8.0.4", result, StringComparison.Ordinal);
        Assert.Contains(identity, result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Storage_UsesReceiverNamesAndMaintainsAtomicActiveIndex()
    {
        using var fixture = new TemporaryPortableRoot();
        var storage = new SupportLogStorage(fixture.Paths);
        var peer = "76561198000000002";
        var sessionId = Guid.NewGuid();
        var session = await storage.CreateSessionAsync(new SupportLogSessionDescriptor(
            sessionId,
            peer,
            "Alice",
            DateTimeOffset.UtcNow));

        var receiverName = await session.RegisterSourceAsync(new SupportLogStreamDescriptor(
            "source_1",
            SupportLogSourceKind.Game,
            "../../latest.log"));
        await session.AppendLogAsync(
            "source_1",
            "address=10.8.0.4 accessToken=do-not-store\n[CHAT] hidden\n");
        await session.AppendEventAsync(new { type = "route", address = "10.8.0.4" });
        await session.AppendNetworkAsync(new { rtt = 42, loss = 1.5 });

        Assert.Matches("^game-[0-9]{4}\\.log$", receiverName);
        Assert.True(File.Exists(Path.Combine(session.SessionDirectory, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(session.SessionDirectory, "events.ndjson")));
        Assert.True(File.Exists(Path.Combine(session.SessionDirectory, "network.ndjson")));
        var log = await File.ReadAllTextAsync(Path.Combine(session.SessionDirectory, receiverName));
        Assert.Contains("10.8.0.4", log, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-store", log, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", log, StringComparison.Ordinal);

        var activePath = Path.Combine(fixture.Paths.SupportLogs, "active-sessions.json");
        using (var activeDocument = JsonDocument.Parse(await File.ReadAllTextAsync(activePath)))
        {
            Assert.Single(activeDocument.RootElement.GetProperty("sessions").EnumerateArray());
        }

        await session.CompleteAsync("completed");
        using var completedDocument = JsonDocument.Parse(await File.ReadAllTextAsync(activePath));
        Assert.Empty(completedDocument.RootElement.GetProperty("sessions").EnumerateArray());
    }

    [Fact]
    public async Task Storage_ResumesDeterministicSessionAndPersistsAcceptedFrameState()
    {
        using var fixture = new TemporaryPortableRoot();
        var peer = "76561198000000002";
        var sessionId = Guid.NewGuid();
        var startedAt = new DateTimeOffset(2026, 7, 26, 10, 11, 12, TimeSpan.Zero);
        var descriptor = new SupportLogSessionDescriptor(
            sessionId,
            peer,
            "Alice",
            startedAt,
            new Dictionary<string, string>
            {
                ["route"] = "10.8.0.4",
                ["access_token"] = "never-persist-this",
                ["session_id"] = "never-persist-session-id"
            });
        var firstStorage = new SupportLogStorage(fixture.Paths);
        var first = await firstStorage.CreateSessionAsync(descriptor);
        await first.RegisterSourceAsync(new SupportLogStreamDescriptor(
            "stream_0000001",
            SupportLogSourceKind.Environment,
            "environment"));
        var receiverName = await first.RegisterSourceAsync(new SupportLogStreamDescriptor(
            "stream_0000100",
            SupportLogSourceKind.Launcher,
            "launcher/logs.log"));
        var payload = Encoding.UTF8.GetBytes("first accepted payload address=10.8.0.4\n");
        var hash = Convert.ToHexString(SHA256.HashData(payload));

        var applied = await first.CommitAcceptedFrameAsync(
            1,
            hash,
            token => first.AppendLogAsync("stream_0000100", payload, token));

        Assert.True(applied);
        Assert.Equal(1UL, first.HighestAcceptedSequence);
        Assert.Equal(hash, first.HighestAcceptedHash);
        Assert.DoesNotContain(
            "never-persist-this",
            await File.ReadAllTextAsync(Path.Combine(first.SessionDirectory, "manifest.json")),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "never-persist-session-id",
            await File.ReadAllTextAsync(Path.Combine(first.SessionDirectory, "manifest.json")),
            StringComparison.Ordinal);

        // Simulate a damaged/missing index left by an abrupt launcher exit. Eager
        // construction must still close the orphan manifest and publish an empty index.
        File.Delete(Path.Combine(fixture.Paths.SupportLogs, "active-sessions.json"));
        var restartedStorage = new SupportLogStorage(fixture.Paths);
        using (var staleManifest = JsonDocument.Parse(await File.ReadAllTextAsync(
                   Path.Combine(first.SessionDirectory, "manifest.json"))))
        {
            Assert.False(staleManifest.RootElement.GetProperty("isActive").GetBoolean());
            Assert.Equal(
                "launcher_restarted",
                staleManifest.RootElement.GetProperty("stopReason").GetString());
        }
        using (var activeIndex = JsonDocument.Parse(await File.ReadAllTextAsync(
                   Path.Combine(fixture.Paths.SupportLogs, "active-sessions.json"))))
        {
            Assert.Empty(activeIndex.RootElement.GetProperty("sessions").EnumerateArray());
        }

        var resumed = await restartedStorage.CreateSessionAsync(descriptor);
        Assert.Equal(first.SessionDirectory, resumed.SessionDirectory);
        Assert.Equal(1UL, resumed.HighestAcceptedSequence);
        Assert.Equal(hash, resumed.HighestAcceptedHash);
        var restoredProtocolStreams =
            await resumed.GetPersistedProtocolStreamsAsync();
        Assert.Equal(SupportLogSourceKind.Environment, restoredProtocolStreams[1]);
        Assert.Equal(SupportLogSourceKind.Events, restoredProtocolStreams[2]);
        Assert.Equal(SupportLogSourceKind.Network, restoredProtocolStreams[3]);
        Assert.Equal(SupportLogSourceKind.Launcher, restoredProtocolStreams[100]);
        Assert.Equal(
            receiverName,
            await resumed.RegisterSourceAsync(new SupportLogStreamDescriptor(
                "stream_0000100",
                SupportLogSourceKind.Launcher,
                "launcher/logs.log")));

        var duplicateCallbackCount = 0;
        Assert.False(await resumed.CommitAcceptedFrameAsync(
            1,
            hash.ToLowerInvariant(),
            _ =>
            {
                duplicateCallbackCount++;
                return Task.CompletedTask;
            }));
        Assert.Equal(0, duplicateCallbackCount);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            resumed.CommitAcceptedFrameAsync(
                1,
                new string('A', 64),
                _ => Task.CompletedTask));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            resumed.CommitAcceptedFrameAsync(
                3,
                new string('B', 64),
                _ => Task.CompletedTask));
        Assert.True(await resumed.CommitAcceptedFrameAsync(
            2,
            new string('C', 64),
            _ => Task.CompletedTask));

        var secondRestart = new SupportLogStorage(fixture.Paths);
        var resumedAgain = await secondRestart.CreateSessionAsync(descriptor);
        Assert.Equal(2UL, resumedAgain.HighestAcceptedSequence);
        Assert.Equal(new string('C', 64), resumedAgain.HighestAcceptedHash);
        Assert.Equal(
            1,
            (await File.ReadAllLinesAsync(
                Path.Combine(resumedAgain.SessionDirectory, receiverName)))
            .Count(line => line.Contains("first accepted payload", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Storage_ReservationsAreAtomicAcrossConcurrentSessions()
    {
        using var fixture = new TemporaryPortableRoot();
        var storage = new SupportLogStorage(
            fixture.Paths,
            sanitizer: null,
            timeProvider: null,
            maxSessionBytes: 1024 * 1024,
            maxTotalBytes: 1024 * 1024,
            minimumFreeBytes: 0,
            freeSpaceProbe: _ => true);
        var first = await storage.CreateSessionAsync(new SupportLogSessionDescriptor(
            Guid.NewGuid(),
            "76561198000000002",
            "Alice",
            DateTimeOffset.UtcNow));
        var second = await storage.CreateSessionAsync(new SupportLogSessionDescriptor(
            Guid.NewGuid(),
            "76561198000000002",
            "Bob",
            DateTimeOffset.UtcNow));

        var held = await storage.ReserveWriteAsync(first, 700 * 1024, CancellationToken.None);
        try
        {
            await Assert.ThrowsAsync<SupportLogStorageLimitException>(async () =>
            {
                await using var rejected = await storage.ReserveWriteAsync(
                    second,
                    400 * 1024,
                    CancellationToken.None);
            });
        }
        finally
        {
            await held.DisposeAsync();
        }

        await using var afterRelease = await storage.ReserveWriteAsync(
            second,
            400 * 1024,
            CancellationToken.None);
    }

    [Fact]
    public async Task Storage_ExplicitRetentionSweepDeletesExpiredCompletedSession()
    {
        using var fixture = new TemporaryPortableRoot();
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var storage = new SupportLogStorage(fixture.Paths, timeProvider: time);
        var session = await storage.CreateSessionAsync(new SupportLogSessionDescriptor(
            Guid.NewGuid(),
            "76561198000000002",
            "Alice",
            time.GetUtcNow()));
        var directory = session.SessionDirectory;
        await session.CompleteAsync("completed");

        time.Advance(SupportLogStorage.Retention + TimeSpan.FromMinutes(1));
        await storage.PruneExpiredAsync();

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task Spool_ReplaysInSequenceAndDeletesAcknowledgedRecords()
    {
        using var fixture = new TemporaryPortableRoot();
        var spool = new SupportLogSpool(fixture.Paths, Guid.NewGuid(), 1024);
        await spool.EnqueueAsync(2, Encoding.UTF8.GetBytes("two"));
        await spool.EnqueueAsync(1, Encoding.UTF8.GetBytes("one"));

        var replay = new List<SupportLogSpoolRecord>();
        await foreach (var record in spool.ReplayFromAsync(0))
        {
            replay.Add(record);
        }

        Assert.Equal([1UL, 2UL], replay.Select(record => record.Sequence).ToArray());
        await spool.AckThroughAsync(1);
        replay.Clear();
        await foreach (var record in spool.ReplayFromAsync(0))
        {
            replay.Add(record);
        }
        Assert.Equal([2UL], replay.Select(record => record.Sequence).ToArray());
    }

    [Fact]
    public async Task CombinedQuota_CountsReceivedLogsAndOutgoingSpoolAtomically()
    {
        using var fixture = new TemporaryPortableRoot();
        const int totalQuota = 1024 * 1024;
        var storage = new SupportLogStorage(
            fixture.Paths,
            sanitizer: null,
            timeProvider: null,
            maxSessionBytes: totalQuota,
            maxTotalBytes: totalQuota,
            minimumFreeBytes: 0,
            freeSpaceProbe: _ => true);
        var session = await storage.CreateSessionAsync(new SupportLogSessionDescriptor(
            Guid.NewGuid(),
            "76561198000000002",
            "Alice",
            DateTimeOffset.UtcNow));
        await session.RegisterSourceAsync(new SupportLogStreamDescriptor(
            "game",
            SupportLogSourceKind.Game,
            "latest.log"));
        await session.AppendLogAsync("game", new string('r', 400 * 1024));

        var spool = new SupportLogSpool(
            fixture.Paths,
            Guid.NewGuid(),
            storage,
            totalQuota);
        await spool.EnqueueAsync(1, new byte[250 * 1024]);
        await spool.EnqueueAsync(2, new byte[250 * 1024]);

        await Assert.ThrowsAsync<SupportLogStorageLimitException>(() =>
            spool.EnqueueAsync(3, new byte[160 * 1024]));

        var storedBytes = Directory.EnumerateFiles(
                fixture.Paths.SupportLogs,
                "*",
                SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(
                fixture.Paths.SupportSpool,
                "*",
                SearchOption.AllDirectories))
            .Sum(path => new FileInfo(path).Length);
        Assert.InRange(storedBytes, 1, totalQuota);
    }

    [Fact]
    public async Task CombinedQuota_PrunesOldestCompletedReceiveSessionBeforeSpoolWrite()
    {
        using var fixture = new TemporaryPortableRoot();
        const int totalQuota = 850 * 1024;
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.Zero));
        var storage = new SupportLogStorage(
            fixture.Paths,
            sanitizer: null,
            timeProvider: time,
            maxSessionBytes: totalQuota,
            maxTotalBytes: totalQuota,
            minimumFreeBytes: 0,
            freeSpaceProbe: _ => true);

        async Task<(SupportLogReceiveSession Session, string Directory)> CreateCompletedAsync(
            string name)
        {
            var session = await storage.CreateSessionAsync(new SupportLogSessionDescriptor(
                Guid.NewGuid(),
                "76561198000000002",
                name,
                time.GetUtcNow()));
            await session.RegisterSourceAsync(new SupportLogStreamDescriptor(
                "game",
                SupportLogSourceKind.Game,
                "latest.log"));
            await session.AppendLogAsync("game", new string('x', 300 * 1024));
            var directory = session.SessionDirectory;
            await session.CompleteAsync("completed");
            return (session, directory);
        }

        var oldest = await CreateCompletedAsync("Alice");
        time.Advance(TimeSpan.FromMinutes(1));
        var newest = await CreateCompletedAsync("Bob");
        var spool = new SupportLogSpool(
            fixture.Paths,
            Guid.NewGuid(),
            storage,
            totalQuota);

        await spool.EnqueueAsync(1, new byte[SupportLogSpool.MaxRecordBytes]);

        Assert.False(Directory.Exists(oldest.Directory));
        Assert.True(Directory.Exists(newest.Directory));
        Assert.Equal(SupportLogSpool.MaxRecordBytes, spool.Bytes);
    }

    [Fact]
    public async Task CombinedQuota_ConcurrentReceiveReservationBlocksSpoolReservation()
    {
        using var fixture = new TemporaryPortableRoot();
        const int totalQuota = 1024 * 1024;
        var storage = new SupportLogStorage(
            fixture.Paths,
            sanitizer: null,
            timeProvider: null,
            maxSessionBytes: totalQuota,
            maxTotalBytes: totalQuota,
            minimumFreeBytes: 0,
            freeSpaceProbe: _ => true);
        var session = await storage.CreateSessionAsync(new SupportLogSessionDescriptor(
            Guid.NewGuid(),
            "76561198000000002",
            "Alice",
            DateTimeOffset.UtcNow));
        var spool = new SupportLogSpool(
            fixture.Paths,
            Guid.NewGuid(),
            storage,
            totalQuota);
        var held = await storage.ReserveWriteAsync(
            session,
            800 * 1024,
            CancellationToken.None);
        try
        {
            await Assert.ThrowsAsync<SupportLogStorageLimitException>(() =>
                spool.EnqueueAsync(1, new byte[250 * 1024]));
        }
        finally
        {
            await held.DisposeAsync();
        }

        await spool.EnqueueAsync(1, new byte[250 * 1024]);
        Assert.Equal(250 * 1024, spool.Bytes);
    }

    [Fact]
    public async Task CombinedQuota_AppliesConfiguredDiskReserveToSpool()
    {
        using var fixture = new TemporaryPortableRoot();
        var allowWrites = true;
        var storage = new SupportLogStorage(
            fixture.Paths,
            sanitizer: null,
            timeProvider: null,
            maxSessionBytes: 1024 * 1024,
            maxTotalBytes: 1024 * 1024,
            minimumFreeBytes: 0,
            freeSpaceProbe: _ => allowWrites);
        var spool = new SupportLogSpool(
            fixture.Paths,
            Guid.NewGuid(),
            storage,
            1024 * 1024);
        allowWrites = false;

        var error = await Assert.ThrowsAsync<SupportLogStorageLimitException>(() =>
            spool.EnqueueAsync(1, new byte[1024]));

        Assert.Contains("Disk reserve", error.Reason, StringComparison.Ordinal);
        Assert.Equal(0, spool.Bytes);
    }

    [Fact]
    public async Task Collector_HoldsUnterminatedLinesAndSanitizesSplitSecretsAndChat()
    {
        using var fixture = new TemporaryPortableRoot();
        var instance = fixture.Paths.CombineUnderInstances("PartialPack");
        var logs = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logs);
        var latest = Path.Combine(logs, "latest.log");
        await File.WriteAllTextAsync(latest, "Authorization: Bearer ");

        await using var collector = new SupportLogCollector(
            fixture.Paths,
            SupportLogSanitizer.CreateDefault(fixture.Paths),
            () => instance);
        await collector.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var snapshotItems = await ReadThroughAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.SnapshotCompleted,
            timeout.Token);
        Assert.DoesNotContain(
            snapshotItems,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.LogicalName.EndsWith("latest.log", StringComparison.OrdinalIgnoreCase));

        await File.AppendAllTextAsync(
            latest,
            "secret-token-value\n[CHAT] ",
            timeout.Token);
        await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
        var afterSecret = DrainAvailable(collector);

        await File.AppendAllTextAsync(
            latest,
            "private chat body\nsafe address=10.8.3.1\n",
            timeout.Token);
        var afterChat = await ReadThroughAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.Text.Contains("10.8.3.1", StringComparison.Ordinal),
            timeout.Token);
        var observed = afterSecret.Concat(afterChat).ToArray();

        Assert.Contains(
            observed,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.Text.Contains("<REDACTED>", StringComparison.Ordinal));
        Assert.DoesNotContain(
            observed,
            item => item.Text.Contains("secret-token-value", StringComparison.Ordinal));
        Assert.DoesNotContain(
            observed,
            item => item.Text.Contains("private chat body", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Collector_DoesNotTraverseJunctionOutsideSelectedInstance()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var fixture = new TemporaryPortableRoot();
        var instance = fixture.Paths.CombineUnderInstances("JunctionPack");
        var logs = Path.Combine(instance, "logs");
        var outside = Path.Combine(fixture.Root, "OutsideDiagnosticData");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(
            Path.Combine(logs, "latest.log"),
            "SAFE_INSTANCE_MARKER address=10.8.3.2\n");
        var outsideLog = Path.Combine(outside, "stolen.log");
        await File.WriteAllTextAsync(
            outsideLog,
            "JUNCTION_SECRET_MARKER password=should-never-be-read\n");
        var junction = Path.Combine(logs, "external");
        CreateJunction(junction, outside);

        try
        {
            await using var collector = new SupportLogCollector(
                fixture.Paths,
                SupportLogSanitizer.CreateDefault(fixture.Paths),
                () => instance);
            await collector.StartAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var items = await ReadThroughAsync(
                collector,
                item => item.Kind == SupportLogCollectorItemKind.SnapshotCompleted,
                timeout.Token);

            Assert.Contains(
                items,
                item => item.Kind == SupportLogCollectorItemKind.Content &&
                        item.Text.Contains("SAFE_INSTANCE_MARKER", StringComparison.Ordinal));
            Assert.DoesNotContain(
                items,
                item => item.Text.Contains("JUNCTION_SECRET_MARKER", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction);
            }
        }

        Assert.True(File.Exists(outsideLog));
    }

    [Fact]
    public async Task Collector_RejectsGzipExpansionBombWithoutPublishingPartialContent()
    {
        using var fixture = new TemporaryPortableRoot();
        var instance = fixture.Paths.CombineUnderInstances("GzipBombPack");
        var logs = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(
            Path.Combine(logs, "latest.log"),
            "active address=10.8.3.3\n");
        const string marker = "GZIP_EXPANSION_BOMB_MARKER";
        var bomb = marker + new string('X', 3 * 1024 * 1024);
        await File.WriteAllBytesAsync(
            Path.Combine(logs, "2026-07-26-bomb.log.gz"),
            CompressUtf8(bomb, CompressionLevel.SmallestSize));

        await using var collector = new SupportLogCollector(
            fixture.Paths,
            SupportLogSanitizer.CreateDefault(fixture.Paths),
            () => instance);
        await collector.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var items = await ReadThroughAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.SnapshotCompleted,
            timeout.Token);

        Assert.Contains(
            items,
            item => item.Kind == SupportLogCollectorItemKind.Warning &&
                    (item.Text.Contains("bounded expansion limit", StringComparison.Ordinal) ||
                     item.Text.Contains(
                         "overlong unterminated log line",
                         StringComparison.Ordinal)));
        Assert.DoesNotContain(
            items,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.Text.Contains(marker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Collector_DropsOverlongGzipLineAndContinuesAfterNewline()
    {
        using var fixture = new TemporaryPortableRoot();
        var instance = fixture.Paths.CombineUnderInstances("GzipLinePack");
        var logs = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(
            Path.Combine(logs, "latest.log"),
            "active address=10.8.3.4\n");
        const string overlongMarker = "OVERLONG_GZIP_SECRET_MARKER";
        const string safeMarker = "SAFE_AFTER_OVERLONG_GZIP address=10.8.3.5";
        var randomText = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(1024 * 1024 / 2 + 128));
        await File.WriteAllBytesAsync(
            Path.Combine(logs, "2026-07-26-overlong.log.gz"),
            CompressUtf8(
                overlongMarker +
                randomText +
                Environment.NewLine +
                safeMarker +
                Environment.NewLine));

        await using var collector = new SupportLogCollector(
            fixture.Paths,
            SupportLogSanitizer.CreateDefault(fixture.Paths),
            () => instance);
        await collector.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var items = await ReadThroughAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.Text.Contains(safeMarker, StringComparison.Ordinal),
            timeout.Token);

        Assert.Contains(
            items,
            item => item.Kind == SupportLogCollectorItemKind.Warning &&
                    item.Text.Contains("overlong diagnostic log line", StringComparison.Ordinal));
        Assert.DoesNotContain(
            items,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.Text.Contains(overlongMarker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Collector_DetectsFastTruncateAndRegrowWithUnchangedPrefixAndLength()
    {
        using var fixture = new TemporaryPortableRoot();
        var instance = fixture.Paths.CombineUnderInstances("FastRegrowPack");
        var logs = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logs);
        var latest = Path.Combine(logs, "latest.log");
        var stablePrefix = string.Concat(Enumerable.Range(0, 80)
            .Select(index => $"stable-prefix-{index:D3}=10.8.4.1{Environment.NewLine}"));
        var before = stablePrefix +
                     "OLD_ROUTE_MARKER " +
                     new string('x', 8 * 1024) +
                     Environment.NewLine;
        var after = stablePrefix +
                    "NEW_ROUTE_MARKER " +
                    new string('y', 8 * 1024) +
                    Environment.NewLine;
        Assert.Equal(Encoding.UTF8.GetByteCount(before), Encoding.UTF8.GetByteCount(after));
        await File.WriteAllTextAsync(latest, before);
        var creationTime = File.GetCreationTimeUtc(latest);

        await using var collector = new SupportLogCollector(
            fixture.Paths,
            SupportLogSanitizer.CreateDefault(fixture.Paths),
            () => instance);
        await collector.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ReadUntilAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.SnapshotCompleted,
            timeout.Token);

        await File.WriteAllTextAsync(latest, after, timeout.Token);
        File.SetCreationTimeUtc(latest, creationTime);

        var reset = await ReadUntilAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.SourceReset,
            timeout.Token);
        Assert.Contains("rewritten", reset.Text, StringComparison.OrdinalIgnoreCase);
        var rewritten = await ReadUntilAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.Text.Contains("NEW_ROUTE_MARKER", StringComparison.Ordinal),
            timeout.Token);
        Assert.False(rewritten.IsInitial);
    }

    [Fact]
    public async Task Collector_RetriesCorruptGzipWithoutDuplicatesAndFollowsReplacement()
    {
        using var fixture = new TemporaryPortableRoot();
        var instance = fixture.Paths.CombineUnderInstances("GzipRetryPack");
        var logs = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(
            Path.Combine(logs, "latest.log"),
            "active address=10.8.5.1\n");

        await using var collector = new SupportLogCollector(
            fixture.Paths,
            SupportLogSanitizer.CreateDefault(fixture.Paths),
            () => instance);
        await collector.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await ReadUntilAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.SnapshotCompleted,
            timeout.Token);

        const string firstMarker = "GZIP_RETRY_MARKER address=10.8.5.2";
        var valid = CompressUtf8(
            firstMarker + Environment.NewLine +
            string.Concat(Enumerable.Range(0, 200)
                .Select(index => $"tail-{index:D3}-{Guid.NewGuid():N}{Environment.NewLine}")));
        var corrupt = valid.ToArray();
        corrupt[^1] ^= 0x5a;
        var archive = Path.Combine(logs, "2026-07-26-1.log.gz");
        await File.WriteAllBytesAsync(archive, corrupt, timeout.Token);

        var retryItems = await ReadThroughAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.Warning &&
                    item.LogicalName.EndsWith(".log.gz", StringComparison.OrdinalIgnoreCase),
            timeout.Token);
        Assert.DoesNotContain(
            retryItems,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.Text.Contains(firstMarker, StringComparison.Ordinal));

        await File.WriteAllBytesAsync(archive, valid, timeout.Token);
        var acceptedItems = await ReadThroughAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.Text.Contains(firstMarker, StringComparison.Ordinal),
            timeout.Token);
        Assert.Equal(
            1,
            acceptedItems.Count(item =>
                item.Kind == SupportLogCollectorItemKind.Content &&
                item.Text.Contains(firstMarker, StringComparison.Ordinal)));
        await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
        Assert.DoesNotContain(
            DrainAvailable(collector),
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.Text.Contains(firstMarker, StringComparison.Ordinal));

        const string secondMarker = "GZIP_REPLACED_MARKER address=10.8.5.3";
        await File.WriteAllBytesAsync(
            archive,
            CompressUtf8(secondMarker + Environment.NewLine),
            timeout.Token);
        await ReadUntilAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.SourceReset &&
                    item.LogicalName.EndsWith(".log.gz", StringComparison.OrdinalIgnoreCase),
            timeout.Token);
        var replacement = await ReadUntilAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.Text.Contains(secondMarker, StringComparison.Ordinal),
            timeout.Token);
        Assert.False(replacement.IsInitial);
    }

    [Fact]
    public async Task Collector_FollowsAppendAndDetectsTruncateWithoutSharingChat()
    {
        using var fixture = new TemporaryPortableRoot();
        var instance = fixture.Paths.CombineUnderInstances("TestPack");
        var logs = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logs);
        var latest = Path.Combine(logs, "latest.log");
        await File.WriteAllTextAsync(latest, "initial ip=10.8.0.9\n");

        await using var collector = new SupportLogCollector(
            fixture.Paths,
            SupportLogSanitizer.CreateDefault(fixture.Paths),
            () => instance);
        await collector.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await ReadUntilAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.SnapshotCompleted,
            timeout.Token);
        await File.AppendAllTextAsync(
            latest,
            "[CHAT] private\nappended address=10.8.0.10 accessToken=secret-value\n",
            timeout.Token);
        var appended = await ReadUntilAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.Text.Contains("10.8.0.10", StringComparison.Ordinal),
            timeout.Token);
        Assert.DoesNotContain("secret-value", appended.Text, StringComparison.Ordinal);

        await File.WriteAllTextAsync(latest, "after truncate ip=10.8.0.11\n", timeout.Token);
        await ReadUntilAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.SourceReset,
            timeout.Token);
        var afterTruncate = await ReadUntilAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.Text.Contains("10.8.0.11", StringComparison.Ordinal),
            timeout.Token);
        Assert.DoesNotContain("private", afterTruncate.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Collector_DetectsReplacedAndRecreatedActiveLog()
    {
        using var fixture = new TemporaryPortableRoot();
        var instance = fixture.Paths.CombineUnderInstances("ReplacePack");
        var logs = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logs);
        var latest = Path.Combine(logs, "latest.log");
        await File.WriteAllTextAsync(latest, "before replacement ip=10.8.1.1\n");

        await using var collector = new SupportLogCollector(
            fixture.Paths,
            SupportLogSanitizer.CreateDefault(fixture.Paths),
            () => instance);
        await collector.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var initial = await ReadUntilAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.Text.Contains("10.8.1.1", StringComparison.Ordinal),
            timeout.Token);
        await ReadUntilAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.SnapshotCompleted,
            timeout.Token);

        File.Move(latest, Path.Combine(logs, "previous-session.txt"));
        await File.WriteAllTextAsync(latest, "after replacement ip=10.8.1.2\n", timeout.Token);

        var reset = await ReadUntilAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.SourceReset,
            timeout.Token);
        var replacement = await ReadUntilAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.Text.Contains("10.8.1.2", StringComparison.Ordinal),
            timeout.Token);

        Assert.Equal(initial.SourceId, reset.SourceId);
        Assert.Equal(initial.SourceId, replacement.SourceId);
        Assert.False(replacement.IsInitial);
    }

    [Fact]
    public async Task Collector_FollowsNewRotatedLogAndCrashReport()
    {
        using var fixture = new TemporaryPortableRoot();
        var instance = fixture.Paths.CombineUnderInstances("RotationPack");
        var logs = Path.Combine(instance, "logs");
        var crashes = Path.Combine(instance, "crash-reports");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(crashes);
        await File.WriteAllTextAsync(
            Path.Combine(logs, "latest.log"),
            "active session ip=10.8.2.1\n");

        await using var collector = new SupportLogCollector(
            fixture.Paths,
            SupportLogSanitizer.CreateDefault(fixture.Paths),
            () => instance);
        await collector.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ReadUntilAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.SnapshotCompleted,
            timeout.Token);

        var rotated = Path.Combine(logs, "2026-07-26-1.log.gz");
        await using (var output = new FileStream(
                         rotated,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         FileOptions.Asynchronous))
        await using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        await using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
        {
            await writer.WriteLineAsync("rotated session ip=10.8.2.2");
        }

        var rotatedItem = await ReadUntilAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.SourceKind == SupportLogSourceKind.Game &&
                    item.Text.Contains("10.8.2.2", StringComparison.Ordinal),
            timeout.Token);
        Assert.EndsWith(".log.gz", rotatedItem.LogicalName, StringComparison.OrdinalIgnoreCase);
        Assert.False(rotatedItem.IsInitial);

        var crash = Path.Combine(crashes, "crash-2026-07-26_17.00.00-client.txt");
        await File.WriteAllTextAsync(
            crash,
            "crash route address=10.8.2.3 accessToken=crash-secret\n",
            timeout.Token);
        var crashItem = await ReadUntilAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.SourceKind == SupportLogSourceKind.CrashReport &&
                    item.Text.Contains("10.8.2.3", StringComparison.Ordinal),
            timeout.Token);
        Assert.DoesNotContain("crash-secret", crashItem.Text, StringComparison.Ordinal);
        Assert.False(crashItem.IsInitial);
    }

    [Fact]
    public void RunCleanup_RotatesLauncherLogAndPreservesRecentInstanceDiagnostics()
    {
        using var fixture = new TemporaryPortableRoot();
        File.WriteAllText(fixture.Paths.LogFile, "launcher-current");
        var instance = fixture.Paths.CombineUnderInstances("RetentionPack");
        var recentLog = WriteDiagnosticFile(instance, "logs", "latest.log", "recent-log");
        var recentDebug = WriteDiagnosticFile(instance, "debug", "debug.log", "recent-debug");
        var recentCrash = WriteDiagnosticFile(
            instance,
            "crash-reports",
            "crash-current.txt",
            "recent-crash");
        var oldLog = WriteDiagnosticFile(instance, "logs", "old.log", "expired");
        File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow - TimeSpan.FromDays(8));

        LogCleanupService.RunCleanup(fixture.Paths);

        Assert.False(File.Exists(fixture.Paths.LogFile));
        var archived = Assert.Single(Directory.EnumerateFiles(
            fixture.Paths.Personal,
            "logs-*.log",
            SearchOption.TopDirectoryOnly));
        Assert.Equal("launcher-current", File.ReadAllText(archived));
        Assert.True(File.Exists(recentLog));
        Assert.True(File.Exists(recentDebug));
        Assert.True(File.Exists(recentCrash));
        Assert.False(File.Exists(oldLog));
    }

    [Fact]
    public async Task PackInstanceCleanup_PreservesRecentDiagnosticsWhenRemovingSessionLogs()
    {
        using var fixture = new TemporaryPortableRoot();
        var instance = fixture.Paths.CombineUnderInstances("CleanupPack");
        var recentLog = WriteDiagnosticFile(instance, "logs", "latest.log", "recent-log");
        var recentArchive = WriteDiagnosticFile(
            instance,
            "logs",
            "2026-07-26-1.log.gz",
            "recent-archive");
        var recentDebug = WriteDiagnosticFile(instance, "debug", "debug.log", "recent-debug");
        var recentCrash = WriteDiagnosticFile(
            instance,
            "crash-reports",
            "crash-current.txt",
            "recent-crash");
        var oldCrash = WriteDiagnosticFile(
            instance,
            "crash-reports",
            "crash-expired.txt",
            "expired");
        File.SetLastWriteTimeUtc(oldCrash, DateTime.UtcNow - TimeSpan.FromDays(8));

        using var service = new PackInstanceService(
            fixture.Paths,
            new Logger(fixture.Paths.LogFile));
        await service.CleanupGeneratedLocalArtifactsAsync(
            "CleanupPack",
            removeSessionLogs: true);

        Assert.True(File.Exists(recentLog));
        Assert.True(File.Exists(recentArchive));
        Assert.True(File.Exists(recentDebug));
        Assert.True(File.Exists(recentCrash));
        Assert.False(File.Exists(oldCrash));
    }

    private static string WriteDiagnosticFile(
        string instance,
        string directory,
        string fileName,
        string content)
    {
        var targetDirectory = Path.Combine(instance, directory);
        Directory.CreateDirectory(targetDirectory);
        var path = Path.Combine(targetDirectory, fileName);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromMinutes(1));
        return path;
    }

    private static async Task<SupportLogCollectorItem> ReadUntilAsync(
        SupportLogCollector collector,
        Func<SupportLogCollectorItem, bool> predicate,
        CancellationToken token)
    {
        while (await collector.Items.WaitToReadAsync(token))
        {
            while (collector.Items.TryRead(out var item))
            {
                if (predicate(item)) return item;
            }
        }
        throw new EndOfStreamException();
    }

    private static async Task<IReadOnlyList<SupportLogCollectorItem>> ReadThroughAsync(
        SupportLogCollector collector,
        Func<SupportLogCollectorItem, bool> predicate,
        CancellationToken token)
    {
        var result = new List<SupportLogCollectorItem>();
        while (await collector.Items.WaitToReadAsync(token))
        {
            while (collector.Items.TryRead(out var item))
            {
                result.Add(item);
                if (predicate(item)) return result;
            }
        }
        throw new EndOfStreamException();
    }

    private static IReadOnlyList<SupportLogCollectorItem> DrainAvailable(
        SupportLogCollector collector)
    {
        var result = new List<SupportLogCollectorItem>();
        while (collector.Items.TryRead(out var item))
        {
            result.Add(item);
        }
        return result;
    }

    private static byte[] CompressUtf8(
        string value,
        CompressionLevel compressionLevel = CompressionLevel.Fastest)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, compressionLevel, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(value));
        }
        return output.ToArray();
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /c mklink /J \"{linkPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException(
                                "Could not start junction creation.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not create test junction: " +
                $"{process.StandardError.ReadToEnd()}{process.StandardOutput.ReadToEnd()}".Trim());
        }
        Assert.True(
            (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0,
            "The test link must be a reparse point.");
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration)
        {
            _utcNow += duration;
        }
    }

    private sealed class TemporaryPortableRoot : IDisposable
    {
        public TemporaryPortableRoot()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "MinecraftSupportLogTests",
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
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
