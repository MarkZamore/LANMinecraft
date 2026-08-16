using System.IO.Compression;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Minecraft;

public enum SupportLogSourceKind
{
    Launcher,
    Game,
    CrashReport,
    Environment,
    Events,
    Network
}

public enum SupportLogCollectorItemKind
{
    SourceOpened,
    Content,
    SourceReset,
    SnapshotCompleted,
    Warning
}

/// <summary>
/// An immutable, already-sanitized unit produced by <see cref="SupportLogCollector"/>.
/// Physical source paths are deliberately not exposed.
/// </summary>
public sealed record SupportLogCollectorItem(
    long Sequence,
    DateTimeOffset CreatedAtUtc,
    SupportLogCollectorItemKind Kind,
    string SourceId,
    SupportLogSourceKind SourceKind,
    string LogicalName,
    string SuggestedFileName,
    string Text,
    bool IsInitial);

/// <summary>
/// Enumerates a deliberately narrow set of diagnostic files and follows active text logs.
/// The bounded output channel applies back-pressure so an unavailable transport cannot grow
/// launcher memory without a limit.
/// </summary>
public sealed class SupportLogCollector : IAsyncDisposable
{
    public const int DefaultQueueCapacity = 512;
    public const int MaxItemUtf8Bytes = 192 * 1024;

    private const int ReadBufferBytes = 64 * 1024;
    internal const int MaxBytesPerSourcePerPass = 256 * 1024;
    private const int MaximumLineChars = 1024 * 1024;
    private const long MaximumCompressedExpansionBytes = 256L * 1024 * 1024;
    private const int MaximumCompressedExpansionRatio = 200;
    private const long CompressedExpansionAllowanceBytes = 1024L * 1024;
    private const long CompressedHighPriorityServiceBytes = 4L * 1024 * 1024;

    internal static readonly TimeSpan HighPriorityBacklogPollInterval =
        TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan CompressedReplayMissingGrace =
        TimeSpan.FromSeconds(5);
    private static readonly string[] ProhibitedDirectoryNames =
    [
        "saves",
        "worlds",
        "screenshots",
        "audio",
        "voice-recordings",
        "supportlogs",
        "supportspool",
        "cache",
        "caches",
        "dynamic-data-pack-cache",
        "dynamic-resource-pack-cache"
    ];

    private readonly AppPaths _paths;
    private readonly SupportLogSanitizer _sanitizer;
    private readonly Func<string?> _currentInstanceDirectoryProvider;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<SupportLogCollectorItem> _items;
    private readonly Dictionary<string, SourceState> _sources =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ignoredInitialArchives =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _initializedInstanceDirectories =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lifecycleGate = new();

    private Task? _runTask;
    private long _sequence;
    private bool _snapshotCompleted;
    private string _lastLowPriorityPath = string.Empty;

    public SupportLogCollector(
        AppPaths paths,
        SupportLogSanitizer sanitizer,
        Func<string?> currentInstanceDirectoryProvider,
        TimeProvider? timeProvider = null,
        int queueCapacity = DefaultQueueCapacity)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(currentInstanceDirectoryProvider);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);

        _paths = paths;
        _sanitizer = sanitizer;
        _currentInstanceDirectoryProvider = currentInstanceDirectoryProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _items = Channel.CreateBounded<SupportLogCollectorItem>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
    }

    public ChannelReader<SupportLogCollectorItem> Items => _items.Reader;

    internal Func<long, CancellationToken, Task>?
        CompressedPreparationCheckpointForTesting { get; set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleGate)
        {
            if (_runTask is not null) return Task.CompletedTask;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
            _runTask = RunAsync(linked);
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<SupportLogCollectorItem> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var item in _items.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        Task? runTask;
        lock (_lifecycleGate)
        {
            runTask = _runTask;
        }

        if (runTask is not null)
        {
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        else
        {
            _items.Writer.TryComplete();
        }

        _stop.Dispose();
    }

    private async Task RunAsync(CancellationTokenSource linked)
    {
        using (linked)
        {
            var token = linked.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var discoveredCandidates = DiscoverSources();
                    var candidates = SelectCandidatesForPass(discoveredCandidates);
                    var discoveredPaths = new HashSet<string>(
                        discoveredCandidates.Select(candidate => candidate.Path),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var candidate in discoveredCandidates)
                    {
                        if (!_sources.ContainsKey(candidate.Path))
                        {
                            _sources.Add(
                                candidate.Path,
                                new SourceState(candidate, candidate.IsInitial));
                        }
                    }
                    foreach (var candidate in candidates)
                    {
                        token.ThrowIfCancellationRequested();
                        var state = _sources[candidate.Path];
                        if (!state.SourceOpenedPublished)
                        {
                            await PublishAsync(
                                SupportLogCollectorItemKind.SourceOpened,
                                state,
                                string.Empty,
                                state.IsInitial && !_snapshotCompleted,
                                token).ConfigureAwait(false);
                            state.SourceOpenedPublished = true;
                        }
                        if (candidate.Compressed)
                        {
                            state.MarkCompressedDiscovered();
                        }

                        await ReadAvailableAsync(state, token).ConfigureAwait(false);
                    }
                    foreach (var missing in _sources.Values.Where(source =>
                                 !discoveredPaths.Contains(source.Candidate.Path)))
                    {
                        if (missing.Candidate.Compressed && !missing.Completed)
                        {
                            if (!missing.ShouldCompleteMissingCompressed(
                                    _timeProvider.GetUtcNow(),
                                    CompressedReplayMissingGrace))
                            {
                                continue;
                            }
                            missing.CompleteMissingCompressed();
                        }
                        if (missing.PendingText.Length > 0 &&
                            !missing.DiscardUntilNewLine)
                        {
                            await FlushPartialLineAsync(
                                missing,
                                missing.IsInitial && !_snapshotCompleted,
                                token).ConfigureAwait(false);
                        }
                        if (missing.IsInitial &&
                            missing.Offset < missing.InitialBoundary)
                        {
                            missing.Offset = missing.InitialBoundary;
                        }
                    }

                    if (!_snapshotCompleted &&
                        InitialSnapshotHasBeenDrained(discoveredCandidates))
                    {
                        _snapshotCompleted = true;
                        await PublishAsync(
                            new SupportLogCollectorItem(
                                NextSequence(),
                                _timeProvider.GetUtcNow(),
                                SupportLogCollectorItemKind.SnapshotCompleted,
                                string.Empty,
                                SupportLogSourceKind.Launcher,
                                string.Empty,
                                string.Empty,
                                string.Empty,
                                IsInitial: true),
                            token).ConfigureAwait(false);
                    }

                    var delay = !_snapshotCompleted ||
                                HasUnreadHighPriorityBacklog(discoveredCandidates)
                        ? HighPriorityBacklogPollInterval
                        : PollInterval;
                    await Task.Delay(delay, _timeProvider, token)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                await TryPublishWarningAsync($"Collector stopped: {ex.GetType().Name}: {ex.Message}")
                    .ConfigureAwait(false);
            }
            finally
            {
                await FlushPendingLinesAsync(token).ConfigureAwait(false);
                foreach (var state in _sources.Values)
                {
                    state.DisposeCompressedReplay();
                }
                _items.Writer.TryComplete();
            }
        }
    }

    private SourceCandidate[] DiscoverSources()
    {
        var result = new Dictionary<string, SourceCandidate>(StringComparer.OrdinalIgnoreCase);
        AddCandidate(
            result,
            _paths.LogFile,
            _paths.Personal,
            SupportLogSourceKind.Launcher,
            "launcher/logs.log",
            compressed: false,
            isInitial: !_snapshotCompleted);

        foreach (var launcherArchive in EnumerateFilesSafe(_paths.Personal, "logs-*.log", SearchOption.TopDirectoryOnly))
        {
            AddCandidate(
                result,
                launcherArchive,
                _paths.Personal,
                SupportLogSourceKind.Launcher,
                $"launcher/{Path.GetFileName(launcherArchive)}",
                compressed: false,
                isInitial: !_snapshotCompleted);
        }

        var instanceDirectory = ResolveAllowedInstanceDirectory();
        if (instanceDirectory is null)
        {
            return OrderCandidates(result.Values);
        }

        var firstVisitToInstance = _initializedInstanceDirectories.Add(
            instanceDirectory);

        foreach (var logPath in EnumerateFilesSafe(instanceDirectory, "*.log", SearchOption.AllDirectories))
        {
            if (!IsAllowedInstanceDiagnostic(logPath, instanceDirectory)) continue;
            AddCandidate(
                result,
                logPath,
                instanceDirectory,
                SupportLogSourceKind.Game,
                BuildLogicalName(instanceDirectory, logPath),
                compressed: false,
                isInitial: !_snapshotCompleted);
        }

        var compressedLogs = EnumerateFilesSafe(instanceDirectory, "*.log.gz", SearchOption.AllDirectories)
            .Where(path => IsAllowedInstanceDiagnostic(path, instanceDirectory))
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .ToArray();
        if (firstVisitToInstance)
        {
            foreach (var ignored in compressedLogs.Skip(1))
            {
                _ignoredInitialArchives.Add(ignored.FullName);
            }
        }

        foreach (var archive in compressedLogs)
        {
            if (_ignoredInitialArchives.Contains(archive.FullName)) continue;
            AddCandidate(
                result,
                archive.FullName,
                instanceDirectory,
                SupportLogSourceKind.Game,
                BuildLogicalName(instanceDirectory, archive.FullName),
                compressed: true,
                isInitial: !_snapshotCompleted);
        }

        var crashReports = EnumerateFilesSafe(
                Path.Combine(instanceDirectory, "crash-reports"),
                "*",
                SearchOption.TopDirectoryOnly)
            .Where(path =>
                Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".log", StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .ToArray();
        if (firstVisitToInstance)
        {
            foreach (var ignored in crashReports.Skip(1))
            {
                _ignoredInitialArchives.Add(ignored.FullName);
            }
        }

        foreach (var report in crashReports)
        {
            if (_ignoredInitialArchives.Contains(report.FullName)) continue;
            AddCandidate(
                result,
                report.FullName,
                instanceDirectory,
                SupportLogSourceKind.CrashReport,
                BuildLogicalName(instanceDirectory, report.FullName),
                compressed: false,
                isInitial: !_snapshotCompleted);
        }

        return OrderCandidates(result.Values);
    }

    private static SourceCandidate[] OrderCandidates(
        IEnumerable<SourceCandidate> candidates) =>
        candidates
            .OrderBy(GetSourcePriority)
            .ThenBy(item => item.LogicalName, StringComparer.Ordinal)
            .ToArray();

    private static int GetSourcePriority(SourceCandidate candidate)
    {
        if (candidate.LogicalName.Equals(
                "launcher/logs.log",
                StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (candidate.LogicalName.EndsWith(
                "/latest.log",
                StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        if (candidate.LogicalName.EndsWith(
                "/debug.log",
                StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }
        if (candidate.Kind == SupportLogSourceKind.CrashReport)
        {
            return 3;
        }
        if (!candidate.Compressed &&
            candidate.Kind == SupportLogSourceKind.Game)
        {
            return 4;
        }
        if (!candidate.Compressed)
        {
            return 5;
        }
        return 6;
    }

    private SourceCandidate[] SelectCandidatesForPass(
        IReadOnlyList<SourceCandidate> candidates)
    {
        var selected = candidates
            .Where(IsHighPrioritySource)
            .ToList();
        var lowPriority = candidates
            .Where(candidate => !IsHighPrioritySource(candidate))
            .ToArray();
        if (lowPriority.Length == 0)
        {
            _lastLowPriorityPath = string.Empty;
            return selected.ToArray();
        }

        var lastIndex = Array.FindIndex(
            lowPriority,
            candidate => string.Equals(
                candidate.Path,
                _lastLowPriorityPath,
                StringComparison.OrdinalIgnoreCase));
        var next = lowPriority[(lastIndex + 1) % lowPriority.Length];
        _lastLowPriorityPath = next.Path;
        selected.Add(next);
        return selected.ToArray();
    }

    private bool HasUnreadHighPriorityBacklog(
        IEnumerable<SourceCandidate> candidates)
    {
        foreach (var candidate in candidates.Where(IsHighPrioritySource))
        {
            if (candidate.Compressed ||
                !_sources.TryGetValue(candidate.Path, out var state) ||
                !IsSafeExistingFile(candidate.AllowedRoot, candidate.Path))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(candidate.Path);
                info.Refresh();
                if (info.Exists && info.Length > state.Offset)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or
                                       UnauthorizedAccessException)
            {
            }
        }
        return false;
    }

    private static bool IsHighPrioritySource(SourceCandidate candidate) =>
        candidate.Kind == SupportLogSourceKind.CrashReport ||
        candidate.LogicalName.Equals(
            "launcher/logs.log",
            StringComparison.OrdinalIgnoreCase) ||
        candidate.LogicalName.EndsWith(
            "/latest.log",
            StringComparison.OrdinalIgnoreCase) ||
        candidate.LogicalName.EndsWith(
            "/debug.log",
            StringComparison.OrdinalIgnoreCase);

    private async Task ServiceHighPrioritySourcesDuringCompressedPreparationAsync(
        SourceState compressedState,
        CancellationToken token)
    {
        var candidates = DiscoverSources()
            .Where(candidate =>
                !candidate.Compressed &&
                IsHighPrioritySource(candidate) &&
                !string.Equals(
                    candidate.Path,
                    compressedState.Candidate.Path,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (!_sources.TryGetValue(candidate.Path, out var state))
            {
                state = new SourceState(candidate, candidate.IsInitial);
                _sources.Add(candidate.Path, state);
            }
            if (!state.SourceOpenedPublished)
            {
                await PublishAsync(
                    SupportLogCollectorItemKind.SourceOpened,
                    state,
                    string.Empty,
                    state.IsInitial && !_snapshotCompleted,
                    token).ConfigureAwait(false);
                state.SourceOpenedPublished = true;
            }
            await ReadAvailableAsync(state, token).ConfigureAwait(false);
        }
    }

    private void AddCandidate(
        IDictionary<string, SourceCandidate> result,
        string path,
        string allowedRoot,
        SupportLogSourceKind kind,
        string logicalName,
        bool compressed,
        bool isInitial)
    {
        string fullPath;
        string fullAllowedRoot;
        try
        {
            fullPath = Path.GetFullPath(path);
            fullAllowedRoot = Path.GetFullPath(allowedRoot);
        }
        catch
        {
            return;
        }

        if (!IsSafeExistingFile(fullAllowedRoot, fullPath) ||
            IsProhibitedPath(fullPath))
        {
            return;
        }
        var sourceId = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant())))
            .ToLowerInvariant()[..24];
        var prefix = kind switch
        {
            SupportLogSourceKind.Launcher => "launcher",
            SupportLogSourceKind.CrashReport => "crash-report",
            _ => "game"
        };
        result[fullPath] = new SourceCandidate(
            fullPath,
            fullAllowedRoot,
            sourceId,
            kind,
            NormalizeLogicalName(logicalName),
            $"{prefix}-{sourceId[..8]}.log",
            compressed,
            isInitial);
    }

    private async Task ReadAvailableAsync(SourceState state, CancellationToken token)
    {
        if (!IsSafeExistingFile(
                state.Candidate.AllowedRoot,
                state.Candidate.Path))
        {
            return;
        }

        if (state.Candidate.Compressed)
        {
            FileInfo compressedInfo;
            try
            {
                compressedInfo = new FileInfo(state.Candidate.Path);
                compressedInfo.Refresh();
                if (!compressedInfo.Exists) return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
            if (state.HasCompressedSourceVersion &&
                (state.CompressedLength != compressedInfo.Length ||
                 state.CompressedCreationTimeUtcTicks != compressedInfo.CreationTimeUtc.Ticks ||
                 state.CompressedLastWriteTimeUtcTicks != compressedInfo.LastWriteTimeUtc.Ticks))
            {
                state.ResetCompressed();
                await PublishAsync(
                    SupportLogCollectorItemKind.SourceReset,
                    state,
                    "[diagnostic compressed source replaced]" + Environment.NewLine,
                    initial: false,
                    token).ConfigureAwait(false);
            }
            if (!state.Completed)
            {
                await ReadCompressedAsync(state, compressedInfo, token).ConfigureAwait(false);
            }
            return;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(state.Candidate.Path);
            info.Refresh();
            if (!info.Exists) return;
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        var resetReason = await GetResetReasonAsync(state, info, token).ConfigureAwait(false);
        if (resetReason is not null)
        {
            if (state.PendingText.Length > 0 && !state.DiscardUntilNewLine)
            {
                await FlushPartialLineAsync(
                    state,
                    state.IsInitial && !_snapshotCompleted,
                    token).ConfigureAwait(false);
            }
            state.Reset(
                info.CreationTimeUtc.Ticks,
                !_snapshotCompleted && state.IsInitial ? info.Length : 0);
            await PublishAsync(
                SupportLogCollectorItemKind.SourceReset,
                state,
                $"[diagnostic source restarted: {resetReason}]{Environment.NewLine}",
                IsInitialChunk(state),
                token).ConfigureAwait(false);
        }
        else if (state.CreationTimeUtcTicks == 0)
        {
            state.CreationTimeUtcTicks = info.CreationTimeUtc.Ticks;
            if (state.IsInitial) state.InitialBoundary = info.Length;
        }

        var available = info.Length - state.Offset;
        if (available <= 0)
        {
            state.LastObservedLength = info.Length;
            return;
        }

        var remaining = Math.Min(available, MaxBytesPerSourcePerPass);
        try
        {
            await using var stream = new FileStream(
                state.Candidate.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                ReadBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (!IsSafeExistingFile(
                    state.Candidate.AllowedRoot,
                    state.Candidate.Path))
            {
                return;
            }
            if (stream.Length < state.Offset) return;
            stream.Position = state.Offset;
            var buffer = new byte[ReadBufferBytes];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(ReadBufferBytes)];

            while (remaining > 0)
            {
                var requested = (int)Math.Min(buffer.Length, remaining);
                var read = await stream.ReadAsync(buffer.AsMemory(0, requested), token).ConfigureAwait(false);
                if (read == 0) break;

                var initial = IsInitialChunk(state);
                state.Offset += read;
                remaining -= read;
                state.Decoder.Convert(
                    buffer,
                    0,
                    read,
                    chars,
                    0,
                    chars.Length,
                    flush: false,
                    out _,
                    out var charsUsed,
                    out _);
                if (charsUsed > 0)
                {
                    state.PendingText.Append(chars, 0, charsUsed);
                    state.PendingLastChangedUtc = _timeProvider.GetUtcNow();
                    await EmitCompletedLinesAsync(state, initial, token).ConfigureAwait(false);
                }
            }

            state.LastObservedLength = stream.Length;
            await CapturePrefixAsync(state, stream, token).ConfigureAwait(false);
            await CaptureCheckpointAsync(state, stream, token).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task<string?> GetResetReasonAsync(
        SourceState state,
        FileInfo info,
        CancellationToken token)
    {
        if (state.Offset == 0) return null;
        if (info.Length < state.Offset) return "truncated";
        if (state.CreationTimeUtcTicks != 0 &&
            state.CreationTimeUtcTicks != info.CreationTimeUtc.Ticks)
        {
            return "replaced";
        }
        if (state.PrefixLength == 0 || state.PrefixHash is null) return null;

        try
        {
            await using var stream = new FileStream(
                state.Candidate.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                state.PrefixLength,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length < state.PrefixLength) return "truncated";
            var bytes = new byte[state.PrefixLength];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            var currentHash = SHA256.HashData(bytes);
            if (!CryptographicOperations.FixedTimeEquals(currentHash, state.PrefixHash))
            {
                return "replaced";
            }
            if (state.CheckpointLength <= 0 || state.CheckpointHash is null)
            {
                return null;
            }

            stream.Position = state.CheckpointOffset;
            var checkpoint = new byte[state.CheckpointLength];
            await stream.ReadExactlyAsync(checkpoint, token).ConfigureAwait(false);
            var checkpointHash = SHA256.HashData(checkpoint);
            return CryptographicOperations.FixedTimeEquals(
                checkpointHash,
                state.CheckpointHash)
                ? null
                : "rewritten";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task CapturePrefixAsync(
        SourceState state,
        FileStream openStream,
        CancellationToken token)
    {
        if (state.PrefixLength > 0 || state.Offset <= 0) return;
        state.PrefixLength = checked((int)Math.Min(512, state.Offset));
        var previousPosition = openStream.Position;
        openStream.Position = 0;
        var prefix = new byte[state.PrefixLength];
        await openStream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        state.PrefixHash = SHA256.HashData(prefix);
        openStream.Position = previousPosition;
    }

    private static async Task CaptureCheckpointAsync(
        SourceState state,
        FileStream openStream,
        CancellationToken token)
    {
        if (state.Offset <= 0) return;
        const int checkpointBytes = 4096;
        var length = checked((int)Math.Min(checkpointBytes, state.Offset));
        var offset = state.Offset - length;
        var previousPosition = openStream.Position;
        openStream.Position = offset;
        var checkpoint = new byte[length];
        await openStream.ReadExactlyAsync(checkpoint, token).ConfigureAwait(false);
        state.CheckpointOffset = offset;
        state.CheckpointLength = length;
        state.CheckpointHash = SHA256.HashData(checkpoint);
        openStream.Position = previousPosition;
    }

    private async Task ReadCompressedAsync(
        SourceState state,
        FileInfo info,
        CancellationToken token)
    {
        if (_timeProvider.GetUtcNow() < state.NextCompressedRetryUtc) return;
        var initial = state.IsInitial && !_snapshotCompleted;
        if (state.CompressedReplayStream is null)
        {
            if (_sources.Values.Any(source =>
                    !ReferenceEquals(source, state) &&
                    source.CompressedReplayStream is not null))
            {
                return;
            }

            FileStream? sanitizedOutput = null;
            try
            {
                if (!IsSafeExistingFile(
                        state.Candidate.AllowedRoot,
                        state.Candidate.Path))
                {
                    return;
                }

                Directory.CreateDirectory(_paths.SupportSpool);
                var spoolPath = Path.Combine(
                    _paths.SupportSpool,
                    $".compressed-{Guid.NewGuid():N}.tmp");
                sanitizedOutput = new FileStream(
                    spoolPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    ReadBufferBytes,
                    FileOptions.Asynchronous |
                    FileOptions.SequentialScan |
                    FileOptions.DeleteOnClose);
                await using var source = new FileStream(
                    state.Candidate.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    ReadBufferBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (!IsSafeExistingFile(
                        state.Candidate.AllowedRoot,
                        state.Candidate.Path))
                {
                    return;
                }

                await using var gzip = new GZipStream(
                    source,
                    CompressionMode.Decompress,
                    leaveOpen: false);
                var decompressedBuffer = new byte[ReadBufferBytes];
                var decodedCharacters =
                    new char[Encoding.UTF8.GetMaxCharCount(ReadBufferBytes)];
                var decoder = new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false,
                        throwOnInvalidBytes: false)
                    .GetDecoder();
                var expansionLimit = GetCompressedExpansionLimit(info.Length);
                long decompressedBytes = 0;
                long nextHighPriorityService =
                    CompressedHighPriorityServiceBytes;
                var expansionLimitReached = false;
                var firstDecodedCharacters = true;
                await using (var spoolWriter = new StreamWriter(
                                 sanitizedOutput,
                                 new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                                 ReadBufferBytes,
                                 leaveOpen: true))
                {
                    while (true)
                    {
                        var read = await gzip.ReadAsync(decompressedBuffer, token)
                            .ConfigureAwait(false);
                        if (read == 0) break;
                        if (decompressedBytes > expansionLimit - read)
                        {
                            expansionLimitReached = true;
                            break;
                        }
                        decompressedBytes += read;
                        if (decompressedBytes >= nextHighPriorityService)
                        {
                            var checkpoint =
                                CompressedPreparationCheckpointForTesting;
                            if (checkpoint is not null)
                            {
                                await checkpoint(decompressedBytes, token)
                                    .ConfigureAwait(false);
                            }
                            await ServiceHighPrioritySourcesDuringCompressedPreparationAsync(
                                    state,
                                    token)
                                .ConfigureAwait(false);
                            nextHighPriorityService = checked(
                                decompressedBytes +
                                CompressedHighPriorityServiceBytes);
                        }

                        var charsUsed = decoder.GetChars(
                            decompressedBuffer,
                            0,
                            read,
                            decodedCharacters,
                            0,
                            flush: false);
                        firstDecodedCharacters = await AppendCompressedCharactersAsync(
                                state,
                                decodedCharacters,
                                charsUsed,
                                firstDecodedCharacters,
                                initial,
                                spoolWriter,
                                token)
                            .ConfigureAwait(false);
                    }

                    if (!expansionLimitReached)
                    {
                        var charsUsed = decoder.GetChars(
                            [],
                            0,
                            0,
                            decodedCharacters,
                            0,
                            flush: true);
                        _ = await AppendCompressedCharactersAsync(
                                state,
                                decodedCharacters,
                                charsUsed,
                                firstDecodedCharacters,
                                initial,
                                spoolWriter,
                                token)
                            .ConfigureAwait(false);
                        if (state.PendingText.Length > 0 &&
                            !state.DiscardUntilNewLine)
                        {
                            await FlushPartialLineAsync(
                                    state,
                                    initial,
                                    token,
                                    spoolWriter)
                                .ConfigureAwait(false);
                        }
                        await spoolWriter.FlushAsync(token).ConfigureAwait(false);
                    }
                }

                if (expansionLimitReached)
                {
                    ClearPendingCompressedLine(state);
                    await PublishAsync(
                        SupportLogCollectorItemKind.Warning,
                        state,
                        "[compressed diagnostic log exceeded its bounded expansion limit]" +
                        Environment.NewLine,
                        initial,
                        token).ConfigureAwait(false);
                    state.CompleteCompressed(info);
                    return;
                }

                sanitizedOutput.Position = 0;
                state.AttachCompressedReplay(sanitizedOutput, info);
                sanitizedOutput = null;
                ClearPendingCompressedLine(state);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                ClearPendingCompressedLine(state);
                await PublishAsync(
                    SupportLogCollectorItemKind.Warning,
                    state,
                    $"[compressed diagnostic log could not be read: {ex.GetType().Name}]" +
                    Environment.NewLine,
                    state.IsInitial,
                    token).ConfigureAwait(false);
                // A corrupt or still-being-written archive must not block completion
                // of the initial snapshot. A changed file version triggers a fresh
                // validation attempt.
                state.CompleteCompressed(info);
                return;
            }
            finally
            {
                if (sanitizedOutput is not null)
                {
                    await sanitizedOutput.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        if (state.CompressedReplayStream is null) return;
        if (await ReplayCompressedOutputAsync(
                state.CompressedReplayStream,
                state,
                initial,
                token).ConfigureAwait(false))
        {
            state.CompleteCompressed(info);
        }
    }

    private static long GetCompressedExpansionLimit(long compressedBytes)
    {
        var ratioLimit = compressedBytes >=
                         MaximumCompressedExpansionBytes /
                         MaximumCompressedExpansionRatio
            ? MaximumCompressedExpansionBytes
            : compressedBytes * MaximumCompressedExpansionRatio;
        return Math.Min(
            MaximumCompressedExpansionBytes,
            Math.Max(CompressedExpansionAllowanceBytes, ratioLimit));
    }

    private async Task<bool> AppendCompressedCharactersAsync(
        SourceState state,
        char[] characters,
        int count,
        bool firstDecodedCharacters,
        bool initial,
        StreamWriter spool,
        CancellationToken token)
    {
        if (count == 0)
        {
            return firstDecodedCharacters;
        }

        var offset = 0;
        if (firstDecodedCharacters)
        {
            firstDecodedCharacters = false;
            if (characters[0] == '\uFEFF')
            {
                offset = 1;
            }
        }
        if (offset < count)
        {
            state.PendingText.Append(characters, offset, count - offset);
            state.PendingLastChangedUtc = _timeProvider.GetUtcNow();
            await EmitCompletedLinesAsync(
                    state,
                    initial,
                    token,
                    spool)
                .ConfigureAwait(false);
        }
        return firstDecodedCharacters;
    }

    private async Task<bool> ReplayCompressedOutputAsync(
        FileStream sanitizedOutput,
        SourceState state,
        bool initial,
        CancellationToken token)
    {
        var remaining = Math.Min(
            MaxBytesPerSourcePerPass,
            sanitizedOutput.Length - sanitizedOutput.Position);
        var bytes = new byte[ReadBufferBytes];
        var characters = new char[Encoding.UTF8.GetMaxCharCount(ReadBufferBytes)];
        while (remaining > 0)
        {
            var requested = checked((int)Math.Min(bytes.Length, remaining));
            var read = await sanitizedOutput.ReadAsync(
                    bytes.AsMemory(0, requested),
                    token)
                .ConfigureAwait(false);
            if (read == 0) break;
            remaining -= read;
            var charsUsed = state.CompressedReplayDecoder.GetChars(
                bytes,
                0,
                read,
                characters,
                0,
                flush: false);
            if (charsUsed == 0) continue;
            await PublishAsync(
                    SupportLogCollectorItemKind.Content,
                    state,
                    new string(characters, 0, charsUsed),
                    initial,
                    token)
                .ConfigureAwait(false);
        }

        if (sanitizedOutput.Position < sanitizedOutput.Length)
        {
            return false;
        }

        var finalCharacters = new char[4];
        var finalCount = state.CompressedReplayDecoder.GetChars(
            [],
            0,
            0,
            finalCharacters,
            0,
            flush: true);
        if (finalCount > 0)
        {
            await PublishAsync(
                    SupportLogCollectorItemKind.Content,
                    state,
                    new string(finalCharacters, 0, finalCount),
                    initial,
                    token)
                .ConfigureAwait(false);
        }
        return true;
    }

    private static void ClearPendingCompressedLine(SourceState state)
    {
        state.PendingText.Clear();
        state.PendingLastChangedUtc = default;
        state.DiscardUntilNewLine = false;
    }

    private async Task EmitCompletedLinesAsync(
        SourceState state,
        bool initial,
        CancellationToken token,
        StreamWriter? sanitizedSpool = null)
    {
        if (state.DiscardUntilNewLine)
        {
            var newline = IndexOf(state.PendingText, '\n');
            if (newline < 0)
            {
                state.PendingText.Clear();
                return;
            }
            state.PendingText.Remove(0, newline + 1);
            state.DiscardUntilNewLine = false;
        }

        while (true)
        {
            var newline = IndexOf(state.PendingText, '\n');
            if (newline < 0) break;
            if (newline > MaximumLineChars)
            {
                state.PendingText.Remove(0, newline + 1);
                await PublishAsync(
                    SupportLogCollectorItemKind.Warning,
                    state,
                    "[overlong diagnostic log line removed]" + Environment.NewLine,
                    initial,
                    token).ConfigureAwait(false);
                continue;
            }

            var line = state.PendingText.ToString(0, newline).TrimEnd('\r');
            state.PendingText.Remove(0, newline + 1);
            if (sanitizedSpool is null)
            {
                await EmitSanitizedLineAsync(state, line, initial, token)
                    .ConfigureAwait(false);
            }
            else
            {
                await SpoolSanitizedLineAsync(sanitizedSpool, line, token)
                    .ConfigureAwait(false);
            }
        }

        if (state.PendingText.Length == 0)
        {
            state.PendingLastChangedUtc = default;
        }

        if (state.PendingText.Length <= MaximumLineChars) return;
        state.PendingText.Clear();
        state.DiscardUntilNewLine = true;
        await PublishAsync(
            SupportLogCollectorItemKind.Warning,
            state,
            "[overlong unterminated log line removed]" + Environment.NewLine,
            initial,
            token).ConfigureAwait(false);
    }

    private async Task FlushPartialLineAsync(
        SourceState state,
        bool initial,
        CancellationToken token,
        StreamWriter? sanitizedSpool = null)
    {
        var line = state.PendingText.ToString().TrimEnd('\r');
        state.PendingText.Clear();
        state.PendingLastChangedUtc = default;
        if (sanitizedSpool is null)
        {
            await EmitSanitizedLineAsync(state, line, initial, token).ConfigureAwait(false);
        }
        else
        {
            await SpoolSanitizedLineAsync(sanitizedSpool, line, token)
                .ConfigureAwait(false);
        }
    }

    private async Task SpoolSanitizedLineAsync(
        StreamWriter spool,
        string line,
        CancellationToken token)
    {
        var sanitized = _sanitizer.SanitizeLine(line);
        if (sanitized is null) return;
        await spool.WriteLineAsync(sanitized.AsMemory(), token).ConfigureAwait(false);
    }

    private async Task EmitSanitizedLineAsync(
        SourceState state,
        string line,
        bool initial,
        CancellationToken token)
    {
        var sanitized = _sanitizer.SanitizeLine(line);
        if (sanitized is null) return;

        var remaining = sanitized.AsMemory();
        var maximumCharacters = MaxItemUtf8Bytes / 4;
        while (!remaining.IsEmpty)
        {
            var length = Math.Min(maximumCharacters, remaining.Length);
            var segment = remaining[..length].ToString();
            remaining = remaining[length..];
            if (remaining.IsEmpty) segment += Environment.NewLine;
            await PublishAsync(
                SupportLogCollectorItemKind.Content,
                state,
                segment,
                initial,
                token).ConfigureAwait(false);
        }

        if (sanitized.Length == 0)
        {
            await PublishAsync(
                SupportLogCollectorItemKind.Content,
                state,
                Environment.NewLine,
                initial,
                token).ConfigureAwait(false);
        }
    }

    private bool InitialSnapshotHasBeenDrained(
        IEnumerable<SourceCandidate> discoveredCandidates)
    {
        static bool IsDrained(SourceState source) =>
            source.Candidate.Compressed
                ? source.Completed
                : source.Offset >= source.InitialBoundary;

        if (!_sources.Values
                .Where(source => source.IsInitial)
                .All(IsDrained))
        {
            return false;
        }

        return discoveredCandidates
            .Where(candidate => candidate.IsInitial)
            .All(candidate =>
                _sources.TryGetValue(candidate.Path, out var source) &&
                source.SourceOpenedPublished &&
                IsDrained(source));
    }

    private async Task FlushPendingLinesAsync(CancellationToken token)
    {
        foreach (var state in _sources.Values)
        {
            if (state.PendingText.Length == 0 || state.DiscardUntilNewLine) continue;
            if (token.IsCancellationRequested)
            {
                var sanitized = _sanitizer.SanitizeLine(
                    state.PendingText.ToString().TrimEnd('\r'));
                if (sanitized is not null)
                {
                    _items.Writer.TryWrite(new SupportLogCollectorItem(
                        NextSequence(),
                        _timeProvider.GetUtcNow(),
                        SupportLogCollectorItemKind.Content,
                        state.Candidate.SourceId,
                        state.Candidate.Kind,
                        state.Candidate.LogicalName,
                        state.Candidate.SuggestedFileName,
                        sanitized + Environment.NewLine,
                        IsInitial: false));
                }
                state.PendingText.Clear();
                continue;
            }
            try
            {
                await EmitSanitizedLineAsync(
                    state,
                    state.PendingText.ToString().TrimEnd('\r'),
                    initial: false,
                    token).ConfigureAwait(false);
            }
            catch
            {
            }
            state.PendingText.Clear();
        }
    }

    private async Task PublishAsync(
        SupportLogCollectorItemKind kind,
        SourceState state,
        string text,
        bool initial,
        CancellationToken token)
    {
        await PublishAsync(
            new SupportLogCollectorItem(
                NextSequence(),
                _timeProvider.GetUtcNow(),
                kind,
                state.Candidate.SourceId,
                state.Candidate.Kind,
                state.Candidate.LogicalName,
                state.Candidate.SuggestedFileName,
                text,
                initial),
            token).ConfigureAwait(false);
    }

    private async Task PublishAsync(SupportLogCollectorItem item, CancellationToken token) =>
        await _items.Writer.WriteAsync(item, token).ConfigureAwait(false);

    private async Task TryPublishWarningAsync(string message)
    {
        try
        {
            var sanitized = _sanitizer.SanitizeMetadataValue(message);
            await PublishAsync(
                new SupportLogCollectorItem(
                    NextSequence(),
                    _timeProvider.GetUtcNow(),
                    SupportLogCollectorItemKind.Warning,
                    string.Empty,
                    SupportLogSourceKind.Launcher,
                    string.Empty,
                    string.Empty,
                    sanitized + Environment.NewLine,
                    IsInitial: false),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private long NextSequence() => Interlocked.Increment(ref _sequence);

    private string? ResolveAllowedInstanceDirectory()
    {
        string? value;
        try
        {
            value = _currentInstanceDirectoryProvider();
        }
        catch
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(value)) return null;

        try
        {
            var full = Path.GetFullPath(value);
            var instancesRoot = Path.GetFullPath(_paths.Instances);
            if (!IsSafeExistingDirectory(instancesRoot, full) ||
                PathsEqual(instancesRoot, full))
            {
                return null;
            }
            return full;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsAllowedInstanceDiagnostic(string path, string instanceDirectory)
    {
        string relative;
        try
        {
            var full = Path.GetFullPath(path);
            if (!IsSafeExistingFile(instanceDirectory, full))
            {
                return false;
            }
            relative = Path.GetRelativePath(instanceDirectory, full);
        }
        catch
        {
            return false;
        }
        if (EscapesRoot(relative) ||
            Path.IsPathRooted(relative) ||
            IsProhibitedPath(relative))
        {
            return false;
        }

        var name = Path.GetFileName(path);
        // The game's DEBUG copy of a session is tens of megabytes and says
        // nothing latest.log and the crash report do not.
        if (name.StartsWith("debug", StringComparison.OrdinalIgnoreCase)) return false;

        return Path.GetExtension(path).Equals(".log", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".log.gz", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeExistingFile(string allowedRoot, string path) =>
        IsSafeExistingPath(allowedRoot, path, requireDirectory: false);

    private static bool IsSafeExistingDirectory(string allowedRoot, string path) =>
        IsSafeExistingPath(allowedRoot, path, requireDirectory: true);

    private static bool IsSafeExistingPath(
        string allowedRoot,
        string path,
        bool requireDirectory)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(allowedRoot));
            var full = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(root, full);
            if (Path.IsPathRooted(relative) || EscapesRoot(relative))
            {
                return false;
            }

            var current = root;
            if (HasReparsePoint(current))
            {
                return false;
            }
            if (!PathsEqual(root, full))
            {
                foreach (var segment in relative.Split(
                             [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, segment);
                    if (HasReparsePoint(current))
                    {
                        return false;
                    }
                }
            }

            return requireDirectory
                ? Directory.Exists(full)
                : File.Exists(full);
        }
        catch (Exception ex) when (ex is IOException or
                                   UnauthorizedAccessException or
                                   ArgumentException or
                                   NotSupportedException)
        {
            return false;
        }
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or
                                   UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }

    private static bool EscapesRoot(string relativePath) =>
        relativePath.Equals("..", StringComparison.Ordinal) ||
        relativePath.StartsWith(
            ".." + Path.DirectorySeparatorChar,
            StringComparison.Ordinal) ||
        relativePath.StartsWith(
            ".." + Path.AltDirectorySeparatorChar,
            StringComparison.Ordinal);

    private static bool IsProhibitedPath(string path)
    {
        var segments = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Any(segment =>
            ProhibitedDirectoryNames.Contains(segment, StringComparer.OrdinalIgnoreCase) ||
            segment.Equals("settings.json", StringComparison.OrdinalIgnoreCase));
    }

    private static string[] EnumerateFilesSafe(
        string directory,
        string pattern,
        SearchOption searchOption)
    {
        if (!Directory.Exists(directory)) return [];
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = searchOption == SearchOption.AllDirectories,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            return Directory.EnumerateFiles(directory, pattern, options).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string BuildLogicalName(string instanceDirectory, string path)
    {
        var relative = Path.GetRelativePath(instanceDirectory, path);
        return "instance/" + relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string NormalizeLogicalName(string value)
    {
        var sanitized = value.Replace('\\', '/').TrimStart('/');
        while (sanitized.Contains("../", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("../", string.Empty, StringComparison.Ordinal);
        }
        return sanitized;
    }

    private static int IndexOf(StringBuilder builder, char value)
    {
        for (var index = 0; index < builder.Length; index++)
        {
            if (builder[index] == value) return index;
        }
        return -1;
    }

    private static bool IsInitialChunk(SourceState state) =>
        state.IsInitial && state.Offset < state.InitialBoundary;

    private sealed record SourceCandidate(
        string Path,
        string AllowedRoot,
        string SourceId,
        SupportLogSourceKind Kind,
        string LogicalName,
        string SuggestedFileName,
        bool Compressed,
        bool IsInitial);

    private sealed class SourceState
    {
        public SourceState(SourceCandidate candidate, bool isInitial)
        {
            Candidate = candidate;
            IsInitial = isInitial;
            if (isInitial)
            {
                try
                {
                    InitialBoundary = new FileInfo(candidate.Path).Length;
                }
                catch
                {
                }
            }
        }

        public SourceCandidate Candidate { get; }
        public bool IsInitial { get; }
        public bool SourceOpenedPublished { get; set; }
        public long InitialBoundary { get; set; }
        public long Offset { get; set; }
        public long LastObservedLength { get; set; }
        public long CreationTimeUtcTicks { get; set; }
        public int PrefixLength { get; set; }
        public byte[]? PrefixHash { get; set; }
        public long CheckpointOffset { get; set; }
        public int CheckpointLength { get; set; }
        public byte[]? CheckpointHash { get; set; }
        public Decoder Decoder { get; private set; } = Encoding.UTF8.GetDecoder();
        public StringBuilder PendingText { get; } = new();
        public DateTimeOffset PendingLastChangedUtc { get; set; }
        public bool DiscardUntilNewLine { get; set; }
        public bool Completed { get; set; }
        public DateTimeOffset NextCompressedRetryUtc { get; set; }
        public long CompressedLength { get; set; }
        public long CompressedCreationTimeUtcTicks { get; set; }
        public long CompressedLastWriteTimeUtcTicks { get; set; }
        public FileStream? CompressedReplayStream { get; private set; }
        private DateTimeOffset? CompressedReplayMissingSinceUtc { get; set; }
        public Decoder CompressedReplayDecoder { get; private set; } =
            new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true)
                .GetDecoder();
        public bool HasCompressedSourceVersion =>
            Completed || CompressedReplayStream is not null;

        public void MarkCompressedDiscovered()
        {
            CompressedReplayMissingSinceUtc = null;
        }

        public bool ShouldCompleteMissingCompressed(
            DateTimeOffset now,
            TimeSpan grace)
        {
            if (CompressedReplayStream is null)
            {
                return true;
            }

            if (CompressedReplayMissingSinceUtc is null)
            {
                CompressedReplayMissingSinceUtc = now;
                return false;
            }

            return now - CompressedReplayMissingSinceUtc.Value >= grace;
        }

        public void Reset(long creationTimeUtcTicks, long initialBoundary)
        {
            Offset = 0;
            LastObservedLength = 0;
            CreationTimeUtcTicks = creationTimeUtcTicks;
            PrefixLength = 0;
            PrefixHash = null;
            CheckpointOffset = 0;
            CheckpointLength = 0;
            CheckpointHash = null;
            Decoder = Encoding.UTF8.GetDecoder();
            PendingText.Clear();
            PendingLastChangedUtc = default;
            DiscardUntilNewLine = false;
            Completed = false;
            NextCompressedRetryUtc = default;
            InitialBoundary = initialBoundary;
        }

        public void ResetCompressed()
        {
            DisposeCompressedReplay();
            Completed = false;
            Offset = 0;
            NextCompressedRetryUtc = default;
            CompressedLength = 0;
            CompressedCreationTimeUtcTicks = 0;
            CompressedLastWriteTimeUtcTicks = 0;
            CompressedReplayDecoder = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true)
                .GetDecoder();
            PendingText.Clear();
            PendingLastChangedUtc = default;
            DiscardUntilNewLine = false;
        }

        public void AttachCompressedReplay(FileStream stream, FileInfo sourceInfo)
        {
            DisposeCompressedReplay();
            CompressedReplayStream = stream;
            CompressedReplayDecoder = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true)
                .GetDecoder();
            CompressedLength = sourceInfo.Length;
            CompressedCreationTimeUtcTicks = sourceInfo.CreationTimeUtc.Ticks;
            CompressedLastWriteTimeUtcTicks = sourceInfo.LastWriteTimeUtc.Ticks;
            Completed = false;
        }

        public void CompleteCompressed(FileInfo sourceInfo)
        {
            DisposeCompressedReplay();
            Completed = true;
            Offset = Math.Max(Offset, InitialBoundary);
            CompressedLength = sourceInfo.Length;
            CompressedCreationTimeUtcTicks = sourceInfo.CreationTimeUtc.Ticks;
            CompressedLastWriteTimeUtcTicks = sourceInfo.LastWriteTimeUtc.Ticks;
            NextCompressedRetryUtc = default;
        }

        public void CompleteMissingCompressed()
        {
            DisposeCompressedReplay();
            Completed = true;
            Offset = Math.Max(Offset, InitialBoundary);
            NextCompressedRetryUtc = default;
        }

        public void DisposeCompressedReplay()
        {
            try
            {
                CompressedReplayStream?.Dispose();
            }
            catch
            {
            }
            CompressedReplayStream = null;
            CompressedReplayMissingSinceUtc = null;
        }
    }
}
