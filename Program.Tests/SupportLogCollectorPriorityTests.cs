using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class SupportLogCollectorPriorityTests
{
    [Fact]
    public void RuntimeEventSanitization_RedactsBeforeJsonAndKeepsNetworkEvidence()
    {
        using var fixture = new TemporaryPortableRoot();
        var userHome = Path.Combine(fixture.Root, "UserHome");
        var applicationRoot = Path.Combine(fixture.Root, "Application");
        Directory.CreateDirectory(userHome);
        Directory.CreateDirectory(applicationRoot);
        var sanitizer = new SupportLogSanitizer(userHome, applicationRoot);
        const string interfaceId = "06f677a4-5afd-4a57-83c0-32f9ec8556fb";
        var secret = "secret-token-value";
        var privatePath = Path.Combine(userHome, "private", "latest.log");

        var details = PeerSupportLogService.SanitizeRuntimeDetails(
            sanitizer,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["remoteAddress"] = "10.94.154.244",
                ["localInterfaceId"] = interfaceId,
                ["outboundProducedOffset"] = "65536",
                ["accessToken"] = secret,
                ["chat"] = "private player chat text",
                ["command"] = "/op Player",
                ["error"] = $"Read failed at {privatePath}"
            });
        var reason = PeerSupportLogService.SanitizeRuntimeField(
            sanitizer,
            "reason",
            $"Authorization: Bearer {secret}; path={privatePath}");
        var json = JsonSerializer.Serialize(new { reason, details });

        Assert.NotNull(details);
        Assert.Equal("10.94.154.244", details["remoteAddress"]);
        Assert.Equal(interfaceId, details["localInterfaceId"]);
        Assert.Equal("65536", details["outboundProducedOffset"]);
        Assert.Equal("<REDACTED>", details["accessToken"]);
        Assert.DoesNotContain("chat", details.Keys);
        Assert.DoesNotContain("command", details.Keys);
        Assert.Contains("<USER_HOME>", details["error"], StringComparison.Ordinal);
        Assert.Contains("<USER_HOME>", reason, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(userHome, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            userHome.Replace("\\", "\\\\", StringComparison.Ordinal),
            json,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CollectorSpoolWindow_BoundsArchivesWhileRuntimeBypassesGate()
    {
        var window = new SupportCollectorSpoolWindow();
        const int halfWindow = SupportCollectorSpoolWindow.DefaultMaxBytes / 2;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await window.ReserveIfCollectorAsync(
            collectorFrame: true,
            halfWindow,
            timeout.Token);
        window.Commit(1, halfWindow);

        await window.ReserveIfCollectorAsync(
            collectorFrame: false,
            halfWindow,
            timeout.Token);

        await window.ReserveIfCollectorAsync(
            collectorFrame: true,
            halfWindow,
            timeout.Token);
        window.Commit(3, halfWindow);
        Assert.Equal(
            SupportCollectorSpoolWindow.DefaultMaxBytes,
            window.OutstandingBytes);

        var blockedCollector = window.ReserveIfCollectorAsync(
                collectorFrame: true,
                1,
                timeout.Token)
            .AsTask();
        Assert.False(blockedCollector.IsCompleted);

        var runtimeEvent = window.ReserveIfCollectorAsync(
                collectorFrame: false,
                64 * 1024,
                timeout.Token)
            .AsTask();
        Assert.True(runtimeEvent.IsCompletedSuccessfully);

        window.AcknowledgeThrough(1);
        await blockedCollector.WaitAsync(timeout.Token);
        window.Commit(5, 1);

        Assert.Equal(halfWindow + 1, window.OutstandingBytes);
        Assert.Equal(0, window.ReservedBytes);
        window.AcknowledgeThrough(ulong.MaxValue);
        Assert.Equal(0, window.OutstandingBytes);
    }

    [Fact]
    public async Task PostSnapshotLatestAppend_OvertakesBothArchiveBacklogs()
    {
        using var fixture = new TemporaryPortableRoot();
        var instance = fixture.Paths.CombineUnderInstances("LiveBacklogPack");
        var logs = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logs);
        var latest = Path.Combine(logs, "latest.log");
        await File.WriteAllTextAsync(
            latest,
            "LATEST_INITIAL address=10.26.0.1\n");

        await using var collector = new SupportLogCollector(
            fixture.Paths,
            SupportLogSanitizer.CreateDefault(fixture.Paths),
            () => instance,
            queueCapacity: 4);
        await collector.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _ = await ReadThroughSnapshotAsync(collector, timeout.Token);

        var archiveLine = new string('h', 32 * 1024 - 1) + "\n";
        var archiveBacklog = string.Concat(
            Enumerable.Repeat(archiveLine, 64));
        await File.WriteAllTextAsync(
            Path.Combine(logs, "historical-uncompressed.log"),
            archiveBacklog,
            timeout.Token);
        await File.WriteAllBytesAsync(
            Path.Combine(logs, "historical-compressed.log.gz"),
            CompressUtf8(archiveBacklog),
            timeout.Token);

        const string tailMarker =
            "LATEST_POST_SNAPSHOT_TAIL address=10.26.0.2";
        var latestBacklog = string.Concat(
                                Enumerable.Repeat(archiveLine, 96)) +
                            tailMarker +
                            "\n";
        await File.AppendAllTextAsync(latest, latestBacklog, timeout.Token);

        var stopwatch = Stopwatch.StartNew();
        var observed = await ReadThroughAsync(
            collector,
            item =>
                item.Kind == SupportLogCollectorItemKind.Content &&
                item.Text.Contains(tailMarker, StringComparison.Ordinal),
            timeout.Token);
        stopwatch.Stop();

        Assert.Contains(
            observed,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.LogicalName.EndsWith(
                        "/historical-uncompressed.log",
                        StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            observed,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.LogicalName.EndsWith(
                        "/historical-compressed.log.gz",
                        StringComparison.OrdinalIgnoreCase));
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(6),
            $"The live latest.log tail was delayed for {stopwatch.Elapsed}.");
        Assert.True(
            SupportLogCollector.MaxBytesPerSourcePerPass /
            SupportLogCollector.HighPriorityBacklogPollInterval.TotalSeconds >
            SupportLogRateLimiter.DefaultBytesPerSecond);
    }

    [Fact]
    public async Task LatestAppend_IsServicedDuringCompressedArchivePreparation()
    {
        using var fixture = new TemporaryPortableRoot();
        var instance = fixture.Paths.CombineUnderInstances("GzipCheckpointPack");
        var logs = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logs);
        var latest = Path.Combine(logs, "latest.log");
        await File.WriteAllTextAsync(
            latest,
            "LATEST_BEFORE_GZIP address=10.27.0.1\n");

        var randomBytes = new byte[5 * 1024 * 1024];
        new Random(2701).NextBytes(randomBytes);
        var base64 = Convert.ToBase64String(randomBytes);
        var archive = new StringBuilder(base64.Length + 1024);
        const int archiveLineCharacters = 32 * 1024;
        for (var offset = 0; offset < base64.Length;)
        {
            var count = Math.Min(
                archiveLineCharacters,
                base64.Length - offset);
            archive.Append(base64, offset, count);
            archive.Append('\n');
            offset += count;
        }
        await File.WriteAllBytesAsync(
            Path.Combine(logs, "large-initial.log.gz"),
            CompressUtf8(archive.ToString()));

        var checkpointEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCheckpoint = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpointCount = 0;
        await using var collector = new SupportLogCollector(
            fixture.Paths,
            SupportLogSanitizer.CreateDefault(fixture.Paths),
            () => instance,
            queueCapacity: 4)
        {
            CompressedPreparationCheckpointForTesting =
                async (_, token) =>
                {
                    if (Interlocked.Increment(ref checkpointCount) != 1)
                    {
                        return;
                    }
                    checkpointEntered.TrySetResult();
                    await releaseCheckpoint.Task.WaitAsync(token);
                }
        };
        await collector.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        const string marker =
            "LATEST_DURING_GZIP_PREPARATION address=10.27.0.2";
        var observedTask = ReadThroughAsync(
            collector,
            item =>
                item.Kind == SupportLogCollectorItemKind.Content &&
                item.Text.Contains(marker, StringComparison.Ordinal),
            timeout.Token);

        IReadOnlyList<SupportLogCollectorItem> observed;
        try
        {
            await checkpointEntered.Task.WaitAsync(timeout.Token);
            await File.AppendAllTextAsync(
                latest,
                marker + "\n",
                timeout.Token);
            releaseCheckpoint.TrySetResult();
            observed = await observedTask;
        }
        finally
        {
            releaseCheckpoint.TrySetResult();
        }

        Assert.Contains(
            observed,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.Text.Contains(marker, StringComparison.Ordinal));
        Assert.DoesNotContain(
            observed,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.LogicalName.EndsWith(
                        ".log.gz",
                        StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InitialSnapshot_PublishesActiveLogsBeforeHistoricalSources()
    {
        using var fixture = new TemporaryPortableRoot();
        var instance = fixture.Paths.CombineUnderInstances("PriorityPack");
        var logs = Path.Combine(instance, "logs");
        var crashes = Path.Combine(instance, "crash-reports");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(crashes);

        await File.WriteAllTextAsync(
            fixture.Paths.LogFile,
            "CURRENT_LAUNCHER address=10.21.0.1\n");
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Paths.Personal, "logs-2026-07-27.log"),
            "OLD_LAUNCHER address=10.21.0.2\n");
        await File.WriteAllTextAsync(
            Path.Combine(logs, "latest.log"),
            "CURRENT_LATEST address=10.21.0.3\n");
        // The game's DEBUG copy is deliberately never collected; LogVolumeTests
        // pins that, so the ordering below no longer mentions it.
        await File.WriteAllTextAsync(
            Path.Combine(logs, "other.log"),
            "OTHER_GAME_LOG address=10.21.0.5\n");
        await File.WriteAllTextAsync(
            Path.Combine(crashes, "crash-current.txt"),
            "CURRENT_CRASH address=10.21.0.6\n");
        await File.WriteAllBytesAsync(
            Path.Combine(logs, "2026-07-27-1.log.gz"),
            CompressUtf8("OLD_COMPRESSED address=10.21.0.7\n"));

        await using var collector = new SupportLogCollector(
            fixture.Paths,
            SupportLogSanitizer.CreateDefault(fixture.Paths),
            () => instance);
        await collector.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var items = await ReadThroughSnapshotAsync(collector, timeout.Token);

        var content = items
            .Where(item => item.Kind == SupportLogCollectorItemKind.Content)
            .ToArray();
        Assert.True(IndexOf(content, "CURRENT_LAUNCHER") <
                    IndexOf(content, "CURRENT_LATEST"));
        Assert.True(IndexOf(content, "CURRENT_LATEST") <
                    IndexOf(content, "CURRENT_CRASH"));
        Assert.True(IndexOf(content, "CURRENT_CRASH") <
                    IndexOf(content, "OTHER_GAME_LOG"));
        Assert.True(IndexOf(content, "OTHER_GAME_LOG") <
                    IndexOf(content, "OLD_LAUNCHER"));
        Assert.True(IndexOf(content, "OLD_LAUNCHER") <
                    IndexOf(content, "OLD_COMPRESSED"));
    }

    [Fact]
    public async Task InitialSnapshot_RoundRobinsLargeSourcesAfter256KiB()
    {
        using var fixture = new TemporaryPortableRoot();
        var instance = fixture.Paths.CombineUnderInstances("FairnessPack");
        var logs = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logs);

        var lineBreak = Environment.NewLine;
        var line = new string(
                       'a',
                       32 * 1024 - Encoding.UTF8.GetByteCount(lineBreak)) +
                   lineBreak;
        await File.WriteAllTextAsync(
            Path.Combine(logs, "a.log"),
            string.Concat(Enumerable.Repeat(line, 10)) +
            "A_AFTER_FIRST_QUANTUM address=10.22.0.1\n");
        await File.WriteAllTextAsync(
            Path.Combine(logs, "b.log"),
            "B_NOT_STARVED address=10.22.0.2\n");

        await using var collector = new SupportLogCollector(
            fixture.Paths,
            SupportLogSanitizer.CreateDefault(fixture.Paths),
            () => instance);
        await collector.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var items = await ReadThroughSnapshotAsync(collector, timeout.Token);
        var content = items
            .Where(item => item.Kind == SupportLogCollectorItemKind.Content)
            .ToArray();

        var bIndex = IndexOf(content, "B_NOT_STARVED");
        var secondPassIndex = IndexOf(content, "A_AFTER_FIRST_QUANTUM");
        Assert.True(bIndex < secondPassIndex);

        var bytesFromAFirstPass = content
            .Take(bIndex)
            .Where(item => item.LogicalName.EndsWith(
                "/a.log",
                StringComparison.OrdinalIgnoreCase))
            .Sum(item => Encoding.UTF8.GetByteCount(item.Text));
        Assert.InRange(bytesFromAFirstPass, 1, 256 * 1024);
    }

    [Fact]
    public async Task CompressedBacklog_DoesNotStarveLiveLatestLogAppend()
    {
        using var fixture = new TemporaryPortableRoot();
        var instance = fixture.Paths.CombineUnderInstances("LivePriorityPack");
        var logs = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logs);
        var latest = Path.Combine(logs, "latest.log");
        await File.WriteAllTextAsync(
            latest,
            "LATEST_INITIAL address=10.23.0.1\n");

        var archiveLine = new string('z', 32 * 1024 - 1) + "\n";
        await File.WriteAllBytesAsync(
            Path.Combine(logs, "2026-07-27-1.log.gz"),
            CompressUtf8(string.Concat(Enumerable.Repeat(archiveLine, 24))));

        await using var collector = new SupportLogCollector(
            fixture.Paths,
            SupportLogSanitizer.CreateDefault(fixture.Paths),
            () => instance,
            queueCapacity: 1);
        await collector.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await ReadUntilAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.LogicalName.EndsWith(
                        ".log.gz",
                        StringComparison.OrdinalIgnoreCase),
            timeout.Token);
        await File.AppendAllTextAsync(
            latest,
            "LATEST_LIVE_APPEND address=10.23.0.2\n",
            timeout.Token);

        var beforeLive = await ReadThroughAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.Text.Contains(
                        "LATEST_LIVE_APPEND",
                        StringComparison.Ordinal),
            timeout.Token);

        Assert.DoesNotContain(
            beforeLive,
            item => item.Kind == SupportLogCollectorItemKind.SnapshotCompleted);
        var interveningArchiveBytes = beforeLive
            .Where(item =>
                item.Kind == SupportLogCollectorItemKind.Content &&
                item.LogicalName.EndsWith(
                    ".log.gz",
                    StringComparison.OrdinalIgnoreCase))
            .Sum(item => Encoding.UTF8.GetByteCount(item.Text));
        Assert.InRange(interveningArchiveBytes, 0, 256 * 1024);
    }

    [Fact]
    public async Task CompressedReplay_SurvivesSingleDiscoveryMiss()
    {
        using var fixture = new TemporaryPortableRoot();
        var instance = fixture.Paths.CombineUnderInstances("DiscoveryGapPack");
        var logs = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logs);
        var archiveLine = new string('q', 32 * 1024 - 1) + "\n";
        const string tailMarker =
            "COMPRESSED_TAIL_AFTER_DISCOVERY_GAP address=10.24.0.1";
        await File.WriteAllBytesAsync(
            Path.Combine(logs, "2026-07-27-1.log.gz"),
            CompressUtf8(
                string.Concat(Enumerable.Repeat(archiveLine, 24)) +
                tailMarker +
                "\n"));

        var discoveryCalls = 0;
        await using var collector = new SupportLogCollector(
            fixture.Paths,
            SupportLogSanitizer.CreateDefault(fixture.Paths),
            () => Interlocked.Increment(ref discoveryCalls) == 2
                ? null
                : instance);
        await collector.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var items = await ReadThroughSnapshotAsync(collector, timeout.Token);

        Assert.True(Volatile.Read(ref discoveryCalls) >= 3);
        Assert.Contains(
            items,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.Text.Contains(tailMarker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompressedBacklog_UsesOnlyOneActiveReplaySpool()
    {
        using var fixture = new TemporaryPortableRoot();
        var instance = fixture.Paths.CombineUnderInstances("BoundedReplayPack");
        var logs = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(
            Path.Combine(logs, "latest.log"),
            "LATEST_BEFORE_ARCHIVES address=10.25.0.1\n");

        await using var collector = new SupportLogCollector(
            fixture.Paths,
            SupportLogSanitizer.CreateDefault(fixture.Paths),
            () => instance,
            queueCapacity: 1);
        await collector.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await ReadUntilAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.SnapshotCompleted,
            timeout.Token);

        var archiveLine = new string('r', 32 * 1024 - 1) + "\n";
        foreach (var archiveName in new[] { "a.log.gz", "b.log.gz", "c.log.gz" })
        {
            await File.WriteAllBytesAsync(
                Path.Combine(logs, archiveName),
                CompressUtf8(string.Concat(Enumerable.Repeat(archiveLine, 24))),
                timeout.Token);
        }

        var openedArchives = 0;
        await ReadUntilAsync(
            collector,
            item =>
            {
                if (item.Kind != SupportLogCollectorItemKind.SourceOpened ||
                    !item.LogicalName.EndsWith(
                        ".log.gz",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                openedArchives++;
                return openedArchives == 3;
            },
            timeout.Token);

        Assert.Single(Directory.GetFiles(
            fixture.Paths.SupportSpool,
            ".compressed-*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task MissingCompressedReplay_IsDisposedAfterGrace()
    {
        using var fixture = new TemporaryPortableRoot();
        var instance = fixture.Paths.CombineUnderInstances("MissingReplayPack");
        var logs = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logs);
        var archiveLine = new string('s', 32 * 1024 - 1) + "\n";
        await File.WriteAllBytesAsync(
            Path.Combine(logs, "missing.log.gz"),
            CompressUtf8(string.Concat(Enumerable.Repeat(archiveLine, 24))));

        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero));
        var discoveryCalls = 0;
        await using var collector = new SupportLogCollector(
            fixture.Paths,
            SupportLogSanitizer.CreateDefault(fixture.Paths),
            () => Interlocked.Increment(ref discoveryCalls) == 1
                ? instance
                : null,
            time,
            queueCapacity: 1);
        await collector.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await ReadUntilAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.Content &&
                    item.LogicalName.EndsWith(
                        ".log.gz",
                        StringComparison.OrdinalIgnoreCase),
            timeout.Token);

        var snapshotTask = ReadThroughSnapshotAsync(collector, timeout.Token);
        await WaitUntilAsync(
            () => Volatile.Read(ref discoveryCalls) >= 3,
            timeout.Token);
        Assert.Single(Directory.GetFiles(
            fixture.Paths.SupportSpool,
            ".compressed-*.tmp",
            SearchOption.TopDirectoryOnly));

        time.Advance(TimeSpan.FromSeconds(6));
        _ = await snapshotTask;

        Assert.Empty(Directory.GetFiles(
            fixture.Paths.SupportSpool,
            ".compressed-*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    private static int IndexOf(
        IReadOnlyList<SupportLogCollectorItem> items,
        string marker)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index].Text.Contains(marker, StringComparison.Ordinal))
            {
                return index;
            }
        }
        throw new Xunit.Sdk.XunitException($"Marker was not collected: {marker}");
    }

    private static async Task<IReadOnlyList<SupportLogCollectorItem>>
        ReadThroughSnapshotAsync(
            SupportLogCollector collector,
            CancellationToken token) =>
        await ReadThroughAsync(
            collector,
            item => item.Kind == SupportLogCollectorItemKind.SnapshotCompleted,
            token);

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

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        CancellationToken token)
    {
        while (!predicate())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), token);
        }
    }

    private static byte[] CompressUtf8(string value)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(
                   output,
                   CompressionLevel.Fastest,
                   leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(value));
        }
        return output.ToArray();
    }

    private sealed class TemporaryPortableRoot : IDisposable
    {
        public TemporaryPortableRoot()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "MinecraftSupportPriorityTests",
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

    private sealed class MutableTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }

        public void Advance(TimeSpan duration)
        {
            lock (_gate)
            {
                _utcNow += duration;
            }
        }
    }
}
