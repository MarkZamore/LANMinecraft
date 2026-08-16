using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

/// <summary>
/// PeerIdentityId is a SteamID64 in decimal form since the Steam migration; it
/// used to be the peer's Minecraft UUID. Folders written by older builds keep
/// their GUID names and simply age out under retention.
/// </summary>
public sealed record SupportLogSessionDescriptor(
    Guid SessionId,
    string PeerIdentityId,
    string PeerPlayerName,
    DateTimeOffset StartedAtUtc,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record SupportLogStreamDescriptor(
    string SourceId,
    SupportLogSourceKind Kind,
    string DisplayName,
    long? SourceLength = null,
    DateTimeOffset? LastWriteUtc = null);

public sealed record SupportLogReceiveStatus(
    Guid SessionId,
    string PeerIdentityId,
    string PeerPlayerName,
    string SessionDirectory,
    bool IsActive,
    long BytesReceived,
    DateTimeOffset LastActivityUtc,
    string StopReason);

public sealed class SupportLogStorageLimitException : IOException
{
    public SupportLogStorageLimitException(string reason)
        : base(reason)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

/// <summary>
/// Coordinates the single diagnostics quota shared by received logs and every outgoing spool
/// under one portable root. The cached byte count is updated by reservations, so growing spools
/// do not cause a recursive directory scan for every frame.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001",
    Justification = "Coordinators live for the process lifetime; SemaphoreSlim is used without its wait handle.")]
internal sealed class SupportLogCombinedQuota
{
    private static readonly ConcurrentDictionary<string, SupportLogCombinedQuota> Coordinators =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly string _supportLogsRoot;
    private readonly string _supportSpoolRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _activeReceivedSessions =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;
    private long _knownBytes;
    private long _reservedQuotaBytes;
    private long _reservedDiskBytes;

    public static long GetControlFileHeadroom(long maxTotalBytes) =>
        Math.Min(1024L * 1024, Math.Max(1, maxTotalBytes / 100));

    private SupportLogCombinedQuota(AppPaths paths)
    {
        _supportLogsRoot = Path.GetFullPath(paths.SupportLogs);
        _supportSpoolRoot = Path.GetFullPath(paths.SupportSpool);
    }

    public static SupportLogCombinedQuota For(AppPaths paths)
    {
        var logs = Path.GetFullPath(paths.SupportLogs);
        var spool = Path.GetFullPath(paths.SupportSpool);
        var key = logs.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                  "\0" +
                  spool.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Coordinators.GetOrAdd(key, _ => new SupportLogCombinedQuota(paths));
    }

    public async Task InitializeAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            InitializeUnderGate();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RegisterActiveReceivedSessionAsync(
        string sessionDirectory,
        CancellationToken token)
    {
        var normalized = NormalizeUnderRoot(sessionDirectory, _supportLogsRoot);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            InitializeUnderGate();
            _activeReceivedSessions.Add(normalized);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnregisterActiveReceivedSessionAsync(
        string sessionDirectory,
        CancellationToken token)
    {
        var normalized = NormalizeUnderRoot(sessionDirectory, _supportLogsRoot);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _activeReceivedSessions.Remove(normalized);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Reservation> ReserveAsync(
        long quotaBytes,
        long diskBytes,
        long maxTotalBytes,
        long minimumFreeBytes,
        Func<long, bool>? freeSpaceProbe,
        TimeProvider timeProvider,
        CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quotaBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(diskBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTotalBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumFreeBytes);
        ArgumentNullException.ThrowIfNull(timeProvider);

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            InitializeUnderGate();
            if (!HasCapacityUnderGate(
                    quotaBytes,
                    diskBytes,
                    maxTotalBytes,
                    minimumFreeBytes,
                    freeSpaceProbe))
            {
                // Reconcile only when no writes are in flight. Atomic-file temporary files
                // otherwise make a filesystem scan double-count an active reservation.
                if (_reservedQuotaBytes == 0 && _reservedDiskBytes == 0)
                {
                    ReconcileUnderGate();
                }
                await PruneCompletedUnderGateAsync(
                    onlyExpired: false,
                    quotaBytes,
                    diskBytes,
                    maxTotalBytes,
                    minimumFreeBytes,
                    freeSpaceProbe,
                    timeProvider,
                    SupportLogStorage.Retention,
                    token).ConfigureAwait(false);
            }

            if (quotaBytes > 0 &&
                checked(_knownBytes + _reservedQuotaBytes + quotaBytes) > maxTotalBytes)
            {
                throw new SupportLogStorageLimitException(
                    "Total diagnostics storage quota exhausted.");
            }
            if (!HasRequiredFreeSpaceUnderGate(
                    checked(_reservedDiskBytes + diskBytes),
                    minimumFreeBytes,
                    freeSpaceProbe))
            {
                throw new SupportLogStorageLimitException(
                    "Disk reserve reached (minimum 2 GiB or 5% must remain free).");
            }

            _reservedQuotaBytes = checked(_reservedQuotaBytes + quotaBytes);
            _reservedDiskBytes = checked(_reservedDiskBytes + diskBytes);
            return new Reservation(this, quotaBytes, diskBytes);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseCommittedBytesAsync(
        long byteCount,
        CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        if (byteCount == 0) return;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            InitializeUnderGate();
            _knownBytes = Math.Max(0, _knownBytes - byteCount);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PruneCompletedReceivedSessionsAsync(
        bool onlyExpired,
        long requiredQuotaBytes,
        long requiredDiskBytes,
        long maxTotalBytes,
        long minimumFreeBytes,
        Func<long, bool>? freeSpaceProbe,
        TimeProvider timeProvider,
        TimeSpan retention,
        CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            InitializeUnderGate();
            await PruneCompletedUnderGateAsync(
                onlyExpired,
                requiredQuotaBytes,
                requiredDiskBytes,
                maxTotalBytes,
                minimumFreeBytes,
                freeSpaceProbe,
                timeProvider,
                retention,
                token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask CompleteReservationAsync(
        long reservedQuotaBytes,
        long reservedDiskBytes,
        long committedBytes)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _reservedQuotaBytes = Math.Max(
                0,
                _reservedQuotaBytes - reservedQuotaBytes);
            _reservedDiskBytes = Math.Max(
                0,
                _reservedDiskBytes - reservedDiskBytes);
            _knownBytes = checked(_knownBytes + committedBytes);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void InitializeUnderGate()
    {
        if (_initialized) return;
        Directory.CreateDirectory(_supportLogsRoot);
        Directory.CreateDirectory(_supportSpoolRoot);
        ReconcileUnderGate();
        _initialized = true;
    }

    private void ReconcileUnderGate()
    {
        _knownBytes = checked(
            CalculateDirectorySize(_supportLogsRoot) +
            CalculateDirectorySize(_supportSpoolRoot));
    }

    private bool HasCapacityUnderGate(
        long quotaBytes,
        long diskBytes,
        long maxTotalBytes,
        long minimumFreeBytes,
        Func<long, bool>? freeSpaceProbe) =>
        (quotaBytes == 0 ||
         checked(_knownBytes + _reservedQuotaBytes + quotaBytes) <= maxTotalBytes) &&
        HasRequiredFreeSpaceUnderGate(
            checked(_reservedDiskBytes + diskBytes),
            minimumFreeBytes,
            freeSpaceProbe);

    private bool HasRequiredFreeSpaceUnderGate(
        long incomingBytes,
        long minimumFreeBytes,
        Func<long, bool>? freeSpaceProbe)
    {
        try
        {
            if (freeSpaceProbe is not null) return freeSpaceProbe(incomingBytes);
            var root = Path.GetPathRoot(_supportLogsRoot);
            if (string.IsNullOrWhiteSpace(root)) return false;
            var drive = new DriveInfo(root);
            var reserve = Math.Max(minimumFreeBytes, drive.TotalSize / 20);
            return drive.AvailableFreeSpace - incomingBytes >= reserve;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task PruneCompletedUnderGateAsync(
        bool onlyExpired,
        long requiredQuotaBytes,
        long requiredDiskBytes,
        long maxTotalBytes,
        long minimumFreeBytes,
        Func<long, bool>? freeSpaceProbe,
        TimeProvider timeProvider,
        TimeSpan retention,
        CancellationToken token)
    {
        var cutoff = timeProvider.GetUtcNow() - retention;
        foreach (var candidate in FindCompletedReceivedSessionsUnderGate()
                     .OrderBy(item => item.CompletedAtUtc))
        {
            token.ThrowIfCancellationRequested();
            var expired = candidate.CompletedAtUtc < cutoff;
            if (!expired && onlyExpired) continue;
            if (!expired &&
                HasCapacityUnderGate(
                    requiredQuotaBytes,
                    requiredDiskBytes,
                    maxTotalBytes,
                    minimumFreeBytes,
                    freeSpaceProbe))
            {
                break;
            }

            try
            {
                var size = CalculateDirectorySize(candidate.Directory);
                Directory.Delete(candidate.Directory, recursive: true);
                _knownBytes = Math.Max(0, _knownBytes - size);
                TryDeleteEmptyParent(Path.GetDirectoryName(candidate.Directory));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            await Task.Yield();
        }
    }

    private List<CompletedReceivedSession> FindCompletedReceivedSessionsUnderGate()
    {
        var result = new List<CompletedReceivedSession>();
        foreach (var peerDirectory in EnumerateDirectoriesSafe(_supportLogsRoot))
        {
            foreach (var sessionDirectory in EnumerateDirectoriesSafe(peerDirectory))
            {
                var full = Path.GetFullPath(sessionDirectory);
                if (_activeReceivedSessions.Contains(full)) continue;
                var manifestPath = Path.Combine(full, "manifest.json");
                var directoryTimestamp = GetDirectoryTimestamp(full);
                if (!File.Exists(manifestPath))
                {
                    result.Add(new CompletedReceivedSession(
                        full,
                        directoryTimestamp));
                    continue;
                }
                try
                {
                    var manifest = JsonSerializer.Deserialize<
                        SupportLogStorage.StoredSessionManifest>(
                        File.ReadAllText(manifestPath),
                        JsonOptions);
                    if (manifest is null)
                    {
                        result.Add(new CompletedReceivedSession(
                            full,
                            directoryTimestamp));
                        continue;
                    }
                    var completedAt = manifest.CompletedAtUtc ??
                                      (manifest.UpdatedAtUtc == default
                                          ? directoryTimestamp
                                          : manifest.UpdatedAtUtc);
                    result.Add(new CompletedReceivedSession(full, completedAt));
                }
                catch (Exception ex) when (ex is IOException or
                                           JsonException or
                                           UnauthorizedAccessException)
                {
                    result.Add(new CompletedReceivedSession(
                        full,
                        directoryTimestamp));
                }
            }
        }
        return result;
    }

    private static string NormalizeUnderRoot(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var prefix = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Diagnostic session path escapes SupportLogs.");
        }
        return full;
    }

    private static DateTimeOffset GetDirectoryTimestamp(string directory)
    {
        try
        {
            return new DateTimeOffset(
                Directory.GetLastWriteTimeUtc(directory),
                TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or
                                   UnauthorizedAccessException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static long CalculateDirectorySize(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(
                         directory,
                         "*",
                         SearchOption.AllDirectories))
            {
                try
                {
                    total = checked(total + new FileInfo(file).Length);
                }
                catch (Exception ex) when (ex is IOException or
                                           UnauthorizedAccessException or
                                           OverflowException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return total;
    }

    private static string[] EnumerateDirectoriesSafe(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateDirectories(
                    directory,
                    "*",
                    SearchOption.TopDirectoryOnly).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void TryDeleteEmptyParent(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        try
        {
            if (Directory.Exists(directory) &&
                !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch
        {
        }
    }

    internal sealed class Reservation : IAsyncDisposable
    {
        private readonly SupportLogCombinedQuota _owner;
        private readonly long _reservedQuotaBytes;
        private readonly long _reservedDiskBytes;
        private long _committedBytes;
        private int _disposed;

        internal Reservation(
            SupportLogCombinedQuota owner,
            long reservedQuotaBytes,
            long reservedDiskBytes)
        {
            _owner = owner;
            _reservedQuotaBytes = reservedQuotaBytes;
            _reservedDiskBytes = reservedDiskBytes;
        }

        public void Commit(long committedBytes)
        {
            if (committedBytes < 0 || committedBytes > _reservedQuotaBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(committedBytes));
            }
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _committedBytes = committedBytes;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await _owner.CompleteReservationAsync(
                _reservedQuotaBytes,
                _reservedDiskBytes,
                _committedBytes).ConfigureAwait(false);
        }
    }

    private sealed record CompletedReceivedSession(
        string Directory,
        DateTimeOffset CompletedAtUtc);
}

/// <summary>
/// Owns received diagnostic data. All receiver paths are generated locally from validated GUIDs
/// and stream identifiers; a remote peer can never supply a filesystem path.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001",
    Justification = "The storage lifetime is owned by the diagnostics service; SemaphoreSlim is used without its wait handle.")]
public sealed class SupportLogStorage
{
    /// <summary>1 = peer ids were Minecraft UUIDs; 2 = SteamID64.</summary>
    internal const int SchemaVersion = 2;

    public const long MaxSessionBytes = 2L * 1024 * 1024 * 1024;
    public const long MaxTotalBytes = 8L * 1024 * 1024 * 1024;
    public const long MinimumFreeBytes = 2L * 1024 * 1024 * 1024;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SupportLogSanitizer _sanitizer;
    private readonly TimeProvider _timeProvider;
    private readonly long _maxSessionBytes;
    private readonly long _maxTotalBytes;
    private readonly long _minimumFreeBytes;
    private readonly Func<long, bool>? _freeSpaceProbe;
    private readonly SupportLogCombinedQuota _combinedQuota;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, SupportLogReceiveSession> _active =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;
    private readonly string _activeSessionsPath;
    private bool _initialized;

    public SupportLogStorage(
        AppPaths paths,
        SupportLogSanitizer? sanitizer = null,
        TimeProvider? timeProvider = null)
        : this(
            paths,
            sanitizer,
            timeProvider,
            MaxSessionBytes,
            MaxTotalBytes,
            MinimumFreeBytes,
            freeSpaceProbe: null)
    {
    }

    internal SupportLogStorage(
        AppPaths paths,
        SupportLogSanitizer? sanitizer,
        TimeProvider? timeProvider,
        long maxSessionBytes,
        long maxTotalBytes,
        long minimumFreeBytes,
        Func<long, bool>? freeSpaceProbe)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSessionBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTotalBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumFreeBytes);
        if (maxSessionBytes > maxTotalBytes)
        {
            throw new ArgumentException(
                "The diagnostic session quota cannot exceed the total quota.");
        }
        _sanitizer = sanitizer ?? SupportLogSanitizer.CreateDefault(paths);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxSessionBytes = maxSessionBytes;
        _maxTotalBytes = maxTotalBytes;
        _minimumFreeBytes = minimumFreeBytes;
        _freeSpaceProbe = freeSpaceProbe;
        _root = Path.GetFullPath(paths.SupportLogs);
        _activeSessionsPath = Path.Combine(_root, "active-sessions.json");
        _combinedQuota = SupportLogCombinedQuota.For(paths);
        Directory.CreateDirectory(_root);
        Task.Run(InitializeEagerlyAsync).GetAwaiter().GetResult();
    }

    public string RootDirectory => _root;

    internal SupportLogCombinedQuota CombinedQuota => _combinedQuota;
    internal long ConfiguredMaxTotalBytes => _maxTotalBytes;
    internal long ConfiguredMinimumFreeBytes => _minimumFreeBytes;
    internal Func<long, bool>? ConfiguredFreeSpaceProbe => _freeSpaceProbe;
    internal TimeProvider ConfiguredTimeProvider => _timeProvider;

    public bool HasReceivedLogs =>
        Directory.Exists(_root) &&
        Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Any();

    public IReadOnlyList<SupportLogReceiveStatus> ActiveSessions
    {
        get
        {
            lock (_active)
            {
                return _active.Values.Select(session => session.Status).ToArray();
            }
        }
    }

    public async Task PruneExpiredAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await InitializeUnderGateAsync(token).ConfigureAwait(false);
            await PruneUnderGateAsync(
                onlyExpired: true,
                requiredBytes: 0,
                token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupportLogReceiveSession> CreateSessionAsync(
        SupportLogSessionDescriptor descriptor,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.SessionId == Guid.Empty ||
            !IsUsableDirectorySegment(descriptor.PeerIdentityId))
        {
            throw new ArgumentException(
                "The diagnostics session id must be a non-empty GUID and the peer id a plain identifier.");
        }

        var key = GetSessionKey(descriptor.PeerIdentityId, descriptor.SessionId);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await InitializeUnderGateAsync(token).ConfigureAwait(false);
            lock (_active)
            {
                if (_active.TryGetValue(key, out var existing)) return existing;
            }

            await PruneUnderGateAsync(onlyExpired: true, requiredBytes: 0, token).ConfigureAwait(false);

            var peerDirectory = Path.Combine(_root, descriptor.PeerIdentityId);
            var started = descriptor.StartedAtUtc == default
                ? _timeProvider.GetUtcNow()
                : descriptor.StartedAtUtc.ToUniversalTime();
            var directoryName =
                $"{started:yyyyMMddTHHmmssfffZ}-{descriptor.SessionId:D}";
            var sessionDirectory = Path.Combine(peerDirectory, directoryName);
            EnsureUnderRoot(sessionDirectory);
            var normalizedDescriptor = descriptor with
            {
                PeerPlayerName = NormalizePlayerName(descriptor.PeerPlayerName),
                Metadata = SanitizeMetadata(descriptor.Metadata),
                StartedAtUtc = started
            };
            var exists = Directory.Exists(sessionDirectory);
            await _combinedQuota.RegisterActiveReceivedSessionAsync(
                sessionDirectory,
                token).ConfigureAwait(false);
            var registered = true;
            var addedToActive = false;
            try
            {
                if (!exists) Directory.CreateDirectory(sessionDirectory);
                var session = new SupportLogReceiveSession(
                    this,
                    normalizedDescriptor,
                    sessionDirectory,
                    _sanitizer,
                    _timeProvider);
                if (exists)
                {
                    await session.ResumeAsync(token).ConfigureAwait(false);
                }
                else
                {
                    await session.InitializeAsync(token).ConfigureAwait(false);
                }
                lock (_active)
                {
                    _active.Add(key, session);
                    addedToActive = true;
                }
                await WriteActiveSessionsUnderGateAsync().ConfigureAwait(false);
                registered = false;
                return session;
            }
            finally
            {
                if (registered)
                {
                    if (addedToActive)
                    {
                        lock (_active)
                        {
                            _active.Remove(key);
                        }
                    }
                    await _combinedQuota.UnregisterActiveReceivedSessionAsync(
                        sessionDirectory,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<WriteReservation> ReserveWriteAsync(
        SupportLogReceiveSession session,
        int byteCount,
        CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await InitializeUnderGateAsync(token).ConfigureAwait(false);
            if (session.BytesReceived + byteCount > _maxSessionBytes)
            {
                throw new SupportLogStorageLimitException(
                    $"Session quota exceeded ({_maxSessionBytes} bytes).");
            }

            var reservation = await _combinedQuota.ReserveAsync(
                checked(
                    byteCount +
                    SupportLogCombinedQuota.GetControlFileHeadroom(_maxTotalBytes)),
                byteCount,
                _maxTotalBytes,
                _minimumFreeBytes,
                _freeSpaceProbe,
                _timeProvider,
                token).ConfigureAwait(false);
            return new WriteReservation(reservation, byteCount);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task SessionChangedAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await WriteActiveSessionsUnderGateAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task CompleteSessionAsync(
        SupportLogReceiveSession session,
        CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_active)
            {
                _active.Remove(GetSessionKey(session.PeerIdentityId, session.SessionId));
            }
            try
            {
                await WriteActiveSessionsUnderGateAsync().ConfigureAwait(false);
            }
            finally
            {
                await _combinedQuota.UnregisterActiveReceivedSessionAsync(
                    session.SessionDirectory,
                    CancellationToken.None).ConfigureAwait(false);
            }
            await PruneUnderGateAsync(onlyExpired: true, requiredBytes: 0, token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task InitializeUnderGateAsync(CancellationToken token)
    {
        if (_initialized) return;
        Directory.CreateDirectory(_root);
        await _combinedQuota.InitializeAsync(token).ConfigureAwait(false);
        await CloseStaleSessionsUnderGateAsync(token).ConfigureAwait(false);
        await PruneUnderGateAsync(onlyExpired: true, requiredBytes: 0, token).ConfigureAwait(false);
        await WriteTrackedTextAsync(
            _activeSessionsPath,
            JsonSerializer.Serialize(
                new ActiveSessionIndex(1, _timeProvider.GetUtcNow(), []),
                JsonOptions),
            token).ConfigureAwait(false);
        _initialized = true;
    }

    private async Task InitializeEagerlyAsync()
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await InitializeUnderGateAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CloseStaleSessionsUnderGateAsync(CancellationToken token)
    {
        ActiveSessionIndex? index = null;
        if (File.Exists(_activeSessionsPath))
        {
            try
            {
                index = JsonSerializer.Deserialize<ActiveSessionIndex>(
                    await File.ReadAllTextAsync(_activeSessionsPath, token).ConfigureAwait(false),
                    JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        foreach (var entry in index?.Sessions ?? [])
        {
            string directory;
            try
            {
                directory = Path.GetFullPath(Path.Combine(_root, entry.RelativeDirectory));
                EnsureUnderRoot(directory);
            }
            catch
            {
                continue;
            }
            await CloseManifestIfActiveAsync(directory, token).ConfigureAwait(false);
        }

        // The index itself can be missing or damaged after an abrupt power loss.
        // At construction time there are no process-local active sessions, so every
        // on-disk active manifest is stale and must become eligible for retention.
        foreach (var peerDirectory in EnumerateDirectoriesSafe(_root))
        {
            foreach (var sessionDirectory in EnumerateDirectoriesSafe(peerDirectory))
            {
                await CloseManifestIfActiveAsync(sessionDirectory, token).ConfigureAwait(false);
            }
        }
    }

    private async Task CloseManifestIfActiveAsync(
        string directory,
        CancellationToken token)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath)) return;
        try
        {
            var manifest = JsonSerializer.Deserialize<StoredSessionManifest>(
                await File.ReadAllTextAsync(manifestPath, token).ConfigureAwait(false),
                JsonOptions);
            if (manifest is null || !manifest.IsActive) return;
            var now = _timeProvider.GetUtcNow();
            var lastActivity = manifest.UpdatedAtUtc == default
                ? new DateTimeOffset(
                    Directory.GetLastWriteTimeUtc(directory),
                    TimeSpan.Zero)
                : manifest.UpdatedAtUtc;
            if (lastActivity > now)
            {
                lastActivity = now;
            }
            await WriteTrackedTextAsync(
                manifestPath,
                JsonSerializer.Serialize(
                    manifest with
                    {
                        IsActive = false,
                        CompletedAtUtc = lastActivity,
                        StopReason = "launcher_restarted"
                    },
                    JsonOptions),
                token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
    }

    private async Task PruneUnderGateAsync(
        bool onlyExpired,
        long requiredBytes,
        CancellationToken token)
    {
        await _combinedQuota.PruneCompletedReceivedSessionsAsync(
            onlyExpired,
            requiredBytes,
            requiredBytes,
            _maxTotalBytes,
            _minimumFreeBytes,
            _freeSpaceProbe,
            _timeProvider,
            Retention,
            token).ConfigureAwait(false);
    }

    private async Task WriteActiveSessionsUnderGateAsync()
    {
        SupportLogReceiveStatus[] statuses;
        lock (_active)
        {
            statuses = _active.Values.Select(session => session.Status).ToArray();
        }
        var entries = statuses.Select(status => new ActiveSessionEntry(
            status.SessionId,
            status.PeerIdentityId,
            status.PeerPlayerName,
            Path.GetRelativePath(_root, status.SessionDirectory).Replace('\\', '/'),
            status.LastActivityUtc,
            status.BytesReceived)).ToArray();
        await WriteTrackedTextAsync(
            _activeSessionsPath,
            JsonSerializer.Serialize(
                new ActiveSessionIndex(SchemaVersion, _timeProvider.GetUtcNow(), entries),
                JsonOptions),
            CancellationToken.None).ConfigureAwait(false);
    }

    internal async Task WriteTrackedTextAsync(
        string path,
        string contents,
        CancellationToken token = default)
    {
        EnsureUnderRoot(path);
        token.ThrowIfCancellationRequested();
        var bytes = new UTF8Encoding(false).GetBytes(contents);
        var previousLength = GetFileLength(path);
        var growth = Math.Max(0, bytes.LongLength - previousLength);
        await using var reservation = await _combinedQuota.ReserveAsync(
            growth,
            bytes.LongLength,
            _maxTotalBytes,
            _minimumFreeBytes,
            _freeSpaceProbe,
            _timeProvider,
            token).ConfigureAwait(false);
        AtomicFile.WriteAllBytes(path, bytes);
        reservation.Commit(growth);
        if (bytes.LongLength < previousLength)
        {
            await _combinedQuota.ReleaseCommittedBytesAsync(
                previousLength - bytes.LongLength,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static long GetFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private Dictionary<string, string> SanitizeMetadata(
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null) return new Dictionary<string, string>();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in metadata.Take(256))
        {
            var key = NormalizeMetadataKey(pair.Key);
            result[key] = IsSensitiveMetadataKey(key)
                ? "<REDACTED>"
                : _sanitizer.SanitizeMetadataValue(pair.Value);
        }
        return result;
    }

    private static string NormalizeMetadataKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var normalized = new string(value
            .Take(64)
            .Select(character => char.IsLetterOrDigit(character) ||
                                 character is '_' or '-' or '.'
                ? character
                : '_')
            .ToArray());
        return normalized.Length == 0 ? "unknown" : normalized;
    }

    private static string NormalizePlayerName(string? value)
    {
        var normalized = new string((value ?? string.Empty)
            .Where(character => !char.IsControl(character))
            .Take(64)
            .ToArray());
        return normalized.Length == 0 ? "Unknown player" : normalized;
    }

    private static bool IsSensitiveMetadataKey(string key)
    {
        var normalized = key.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return normalized.Contains("token", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("passwd", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("sessionid", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("oauthcode", StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureUnderRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var prefix = _root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Diagnostic path escapes SupportLogs.");
        }
    }

    private static string GetSessionKey(string peerIdentityId, Guid sessionId) =>
        $"{peerIdentityId}/{sessionId:D}";

    /// <summary>
    /// A peer id becomes a directory name, so it may only contain characters
    /// that cannot climb out of SupportLogs (SteamID64 digits, or a legacy GUID).
    /// </summary>
    private static bool IsUsableDirectorySegment(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 64 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string[] EnumerateDirectoriesSafe(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal sealed class WriteReservation : IAsyncDisposable
    {
        private readonly SupportLogCombinedQuota.Reservation _reservation;
        private readonly long _maximumCommittedBytes;

        internal WriteReservation(
            SupportLogCombinedQuota.Reservation reservation,
            long maximumCommittedBytes)
        {
            _reservation = reservation;
            _maximumCommittedBytes = maximumCommittedBytes;
        }

        public void Commit(long committedBytes)
        {
            if (committedBytes < 0 || committedBytes > _maximumCommittedBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(committedBytes));
            }
            _reservation.Commit(committedBytes);
        }

        public ValueTask DisposeAsync() => _reservation.DisposeAsync();
    }

    private sealed record ActiveSessionIndex(
        int SchemaVersion,
        DateTimeOffset UpdatedAtUtc,
        IReadOnlyList<ActiveSessionEntry> Sessions);

    private sealed record ActiveSessionEntry(
        Guid SessionId,
        string PeerIdentityId,
        string PeerPlayerName,
        string RelativeDirectory,
        DateTimeOffset LastActivityUtc,
        long BytesReceived);

    internal sealed record StoredSessionManifest(
        int SchemaVersion,
        Guid SessionId,
        string PeerIdentityId,
        string PeerPlayerName,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        bool IsActive,
        long BytesReceived,
        string StopReason,
        IReadOnlyDictionary<string, string> Metadata,
        IReadOnlyList<StoredStreamManifest> Streams,
        ulong HighestAcceptedSequence = 0,
        string HighestAcceptedHash = "");

    internal sealed record StoredStreamManifest(
        string SourceId,
        SupportLogSourceKind Kind,
        string DisplayName,
        string ReceiverFileName,
        long? SourceLength,
        DateTimeOffset? LastWriteUtc);
}

[SuppressMessage(
    "Design",
    "CA1001",
    Justification = "The receive session is storage-owned and its SemaphoreSlim is process-local synchronization only.")]
public sealed class SupportLogReceiveSession
{
    private static readonly Regex ValidSourceId = new(
        "^[A-Za-z0-9_-]{1,64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions IndentedJsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SupportLogStorage _owner;
    private readonly SupportLogSessionDescriptor _descriptor;
    private readonly SupportLogSanitizer _sanitizer;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _acceptedSequenceGate = new(1, 1);
    private readonly object _acceptedStateGate = new();
    private readonly Dictionary<string, RegisteredStream> _streams =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _metadata =
        new(StringComparer.Ordinal);
    private readonly string _manifestPath;
    private readonly string _eventsPath;
    private readonly string _networkPath;
    private DateTimeOffset _lastIndexWriteUtc;
    private DateTimeOffset _lastManifestWriteUtc;
    private DateTimeOffset _lastActivityUtc;
    private long _bytesReceived;
    private bool _isActive = true;
    private string _stopReason = string.Empty;
    private ulong _highestAcceptedSequence;
    private string _highestAcceptedHash = string.Empty;
    private bool _acceptedStateDirty;

    internal SupportLogReceiveSession(
        SupportLogStorage owner,
        SupportLogSessionDescriptor descriptor,
        string sessionDirectory,
        SupportLogSanitizer sanitizer,
        TimeProvider timeProvider)
    {
        _owner = owner;
        _descriptor = descriptor;
        SessionDirectory = sessionDirectory;
        _sanitizer = sanitizer;
        _timeProvider = timeProvider;
        _lastActivityUtc = timeProvider.GetUtcNow();
        _manifestPath = Path.Combine(sessionDirectory, "manifest.json");
        _eventsPath = Path.Combine(sessionDirectory, "events.ndjson");
        _networkPath = Path.Combine(sessionDirectory, "network.ndjson");
        foreach (var pair in descriptor.Metadata ?? new Dictionary<string, string>())
        {
            _metadata[pair.Key] = pair.Value;
        }
    }

    public event Action<SupportLogReceiveStatus>? StatusChanged;

    public Guid SessionId => _descriptor.SessionId;
    public string PeerIdentityId => _descriptor.PeerIdentityId;
    public string PeerPlayerName => _descriptor.PeerPlayerName;
    public string SessionDirectory { get; }
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);
    public bool IsActive => _isActive;
    public ulong HighestAcceptedSequence
    {
        get
        {
            lock (_acceptedStateGate) return _highestAcceptedSequence;
        }
    }

    public string HighestAcceptedHash
    {
        get
        {
            lock (_acceptedStateGate) return _highestAcceptedHash;
        }
    }

    internal async Task<IReadOnlyDictionary<uint, SupportLogSourceKind>>
        GetPersistedProtocolStreamsAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (HighestAcceptedSequence == 0)
            {
                return new Dictionary<uint, SupportLogSourceKind>();
            }

            var result = new Dictionary<uint, SupportLogSourceKind>
            {
                [2] = SupportLogSourceKind.Events,
                [3] = SupportLogSourceKind.Network
            };
            foreach (var stream in _streams.Values)
            {
                const string prefix = "stream_";
                if (!stream.Descriptor.SourceId.StartsWith(
                        prefix,
                        StringComparison.Ordinal) ||
                    !uint.TryParse(
                        stream.Descriptor.SourceId.AsSpan(prefix.Length),
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var logicalStreamId) ||
                    logicalStreamId == 0 ||
                    logicalStreamId > PeerSupportProtocol.MaxLogicalStreamId ||
                    !result.TryAdd(logicalStreamId, stream.Descriptor.Kind))
                {
                    throw new InvalidDataException(
                        "The persisted diagnostic protocol stream mapping is invalid.");
                }
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public SupportLogReceiveStatus Status => new(
        SessionId,
        PeerIdentityId,
        PeerPlayerName,
        SessionDirectory,
        _isActive,
        BytesReceived,
        _lastActivityUtc,
        _stopReason);

    internal async Task InitializeAsync(CancellationToken token)
    {
        Directory.CreateDirectory(SessionDirectory);
        await _owner.WriteTrackedTextAsync(_eventsPath, string.Empty, token)
            .ConfigureAwait(false);
        await _owner.WriteTrackedTextAsync(_networkPath, string.Empty, token)
            .ConfigureAwait(false);
        await WriteManifestAsync().ConfigureAwait(false);
        _lastManifestWriteUtc = _timeProvider.GetUtcNow();
    }

    internal async Task ResumeAsync(CancellationToken token)
    {
        if (!File.Exists(_manifestPath))
        {
            throw new InvalidDataException(
                "An existing diagnostic session has no manifest.");
        }

        SupportLogStorage.StoredSessionManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SupportLogStorage.StoredSessionManifest>(
                           await File.ReadAllTextAsync(_manifestPath, token).ConfigureAwait(false),
                           JsonOptions) ??
                       throw new InvalidDataException(
                           "The existing diagnostic session manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "The existing diagnostic session manifest is invalid.",
                ex);
        }
        if (manifest.SessionId != SessionId ||
            !string.Equals(manifest.PeerIdentityId, PeerIdentityId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The existing diagnostic session identity does not match.");
        }

        _streams.Clear();
        foreach (var stored in manifest.Streams)
        {
            if (!ValidSourceId.IsMatch(stored.SourceId) ||
                stored.Kind is SupportLogSourceKind.Events or SupportLogSourceKind.Network)
            {
                throw new InvalidDataException(
                    "The existing diagnostic stream mapping is invalid.");
            }
            var expectedName = BuildReceiverFileName(stored.Kind, _streams.Count + 1);
            if (!string.Equals(
                    expectedName,
                    stored.ReceiverFileName,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The existing diagnostic receiver filename is invalid.");
            }
            _streams.Add(
                stored.SourceId,
                new RegisteredStream(
                    new SupportLogStreamDescriptor(
                        stored.SourceId,
                        stored.Kind,
                        stored.DisplayName,
                        stored.SourceLength,
                        stored.LastWriteUtc),
                    expectedName));
        }

        _metadata.Clear();
        foreach (var pair in manifest.Metadata)
        {
            _metadata[pair.Key] = pair.Value;
        }
        foreach (var pair in _descriptor.Metadata ?? new Dictionary<string, string>())
        {
            _metadata[pair.Key] = pair.Value;
        }

        var acceptedHash = string.Empty;
        if (manifest.HighestAcceptedSequence > 0 &&
            !TryNormalizeSha256(manifest.HighestAcceptedHash, out acceptedHash))
        {
            throw new InvalidDataException(
                "The existing diagnostic accepted-frame hash is invalid.");
        }
        lock (_acceptedStateGate)
        {
            _highestAcceptedSequence = manifest.HighestAcceptedSequence;
            _highestAcceptedHash = manifest.HighestAcceptedSequence == 0
                ? string.Empty
                : acceptedHash;
            _acceptedStateDirty = false;
        }
        Interlocked.Exchange(ref _bytesReceived, CalculateReceivedPayloadBytes());
        _isActive = true;
        _stopReason = string.Empty;
        _lastActivityUtc = _timeProvider.GetUtcNow();
        if (!File.Exists(_eventsPath))
        {
            await _owner.WriteTrackedTextAsync(_eventsPath, string.Empty, token)
                .ConfigureAwait(false);
        }
        if (!File.Exists(_networkPath))
        {
            await _owner.WriteTrackedTextAsync(_networkPath, string.Empty, token)
                .ConfigureAwait(false);
        }
        await WriteManifestAsync().ConfigureAwait(false);
        _lastManifestWriteUtc = _timeProvider.GetUtcNow();
    }

    public async Task<string> RegisterSourceAsync(
        SupportLogStreamDescriptor descriptor,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!ValidSourceId.IsMatch(descriptor.SourceId))
        {
            throw new InvalidDataException("The diagnostic source ID is invalid.");
        }
        if (descriptor.Kind is SupportLogSourceKind.Events or SupportLogSourceKind.Network)
        {
            throw new InvalidDataException("Structured streams use fixed receiver files.");
        }

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfInactive();
            if (_streams.TryGetValue(descriptor.SourceId, out var existing))
            {
                if (existing.Descriptor.Kind != descriptor.Kind)
                {
                    throw new InvalidDataException("A diagnostic source changed kind.");
                }
                return existing.ReceiverFileName;
            }

            var receiverFileName = BuildReceiverFileName(descriptor.Kind, _streams.Count + 1);
            var normalized = descriptor with
            {
                DisplayName = _sanitizer.SanitizeMetadataValue(descriptor.DisplayName)
            };
            _streams.Add(
                descriptor.SourceId,
                new RegisteredStream(normalized, receiverFileName));
            await WriteManifestAsync().ConfigureAwait(false);
            _lastManifestWriteUtc = _timeProvider.GetUtcNow();
            return receiverFileName;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateMetadataAsync(
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfInactive();
            foreach (var pair in metadata.Take(64))
            {
                var key = new string((pair.Key ?? string.Empty)
                    .Take(64)
                    .Select(character => char.IsLetterOrDigit(character) ||
                                         character is '_' or '-' or '.'
                        ? character
                        : '_')
                    .ToArray());
                if (key.Length == 0) continue;
                _metadata[key] = IsSensitiveMetadataKey(key)
                    ? "<REDACTED>"
                    : _sanitizer.SanitizeMetadataValue(pair.Value);
            }
            await WriteManifestAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Serializes frame application and persists the last accepted sequence/hash.
    /// A repeated latest frame is acknowledged without invoking <paramref name="appendAction"/>.
    /// </summary>
    public async Task<bool> CommitAcceptedFrameAsync(
        ulong sequence,
        string sha256,
        Func<CancellationToken, Task> appendAction,
        CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sequence);
        ArgumentNullException.ThrowIfNull(appendAction);
        if (!TryNormalizeSha256(sha256, out var normalizedHash))
        {
            throw new InvalidDataException(
                "The diagnostic accepted-frame hash is invalid.");
        }

        await _acceptedSequenceGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ulong highest;
            string highestHash;
            lock (_acceptedStateGate)
            {
                highest = _highestAcceptedSequence;
                highestHash = _highestAcceptedHash;
            }
            if (sequence <= highest)
            {
                if (sequence == highest &&
                    !string.Equals(
                        highestHash,
                        normalizedHash,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The latest diagnostic sequence was replayed with different content.");
                }
                if (IsAcceptedStateDirty())
                {
                    await PersistAcceptedStateAsync().ConfigureAwait(false);
                }
                return false;
            }
            if (sequence != highest + 1)
            {
                throw new InvalidDataException(
                    "The diagnostic accepted-frame sequence contains a gap.");
            }

            await appendAction(token).ConfigureAwait(false);
            lock (_acceptedStateGate)
            {
                _highestAcceptedSequence = sequence;
                _highestAcceptedHash = normalizedHash;
                _acceptedStateDirty = true;
            }
            await PersistAcceptedStateAsync().ConfigureAwait(false);
            return true;
        }
        finally
        {
            _acceptedSequenceGate.Release();
        }
    }

    private bool IsAcceptedStateDirty()
    {
        lock (_acceptedStateGate) return _acceptedStateDirty;
    }

    private async Task PersistAcceptedStateAsync()
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ThrowIfInactive();
            await WriteManifestAsync().ConfigureAwait(false);
            _lastManifestWriteUtc = _timeProvider.GetUtcNow();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task AppendLogAsync(
        string sourceId,
        ReadOnlyMemory<byte> utf8Text,
        CancellationToken token = default)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(utf8Text.Span);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("The diagnostic log is not valid UTF-8.", ex);
        }
        return AppendLogAsync(sourceId, text, token);
    }

    public async Task AppendLogAsync(
        string sourceId,
        string text,
        CancellationToken token = default)
    {
        RegisteredStream stream;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfInactive();
            if (!_streams.TryGetValue(sourceId, out stream!))
            {
                throw new InvalidDataException("The diagnostic source is not registered.");
            }
        }
        finally
        {
            _gate.Release();
        }

        var sanitized = _sanitizer.SanitizeText(text);
        await AppendTextAsync(
            Path.Combine(SessionDirectory, stream.ReceiverFileName),
            sanitized,
            token).ConfigureAwait(false);
    }

    public Task AppendEventAsync<T>(T value, CancellationToken token = default) =>
        AppendStructuredAsync(_eventsPath, value, token);

    public Task AppendNetworkAsync<T>(T value, CancellationToken token = default) =>
        AppendStructuredAsync(_networkPath, value, token);

    public async Task CompleteAsync(
        string? reason = null,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        var transitioned = false;
        Exception? manifestError = null;
        try
        {
            if (!_isActive) return;
            _isActive = false;
            transitioned = true;
            _stopReason = _sanitizer.SanitizeMetadataValue(
                string.IsNullOrWhiteSpace(reason) ? "completed" : reason);
            _lastActivityUtc = _timeProvider.GetUtcNow();
            try
            {
                await WriteManifestAsync(completedAtUtc: _lastActivityUtc)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or
                                       UnauthorizedAccessException or
                                       InvalidOperationException)
            {
                manifestError = ex;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (transitioned)
        {
            try
            {
                await _owner.CompleteSessionAsync(
                    this,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch when (manifestError is not null)
            {
                // Preserve the first failure while still making a best-effort attempt
                // to remove the dead session from the active index and quota.
            }
            finally
            {
                RaiseStatusChanged();
            }
        }
        if (manifestError is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(manifestError)
                .Throw();
        }
    }

    private async Task AppendStructuredAsync<T>(
        string path,
        T value,
        CancellationToken token)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var sanitized = _sanitizer.SanitizeLine(json);
        if (sanitized is null) return;
        await AppendTextAsync(path, sanitized + Environment.NewLine, token).ConfigureAwait(false);
    }

    private async Task AppendTextAsync(
        string path,
        string text,
        CancellationToken token)
    {
        if (string.IsNullOrEmpty(text)) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        SupportLogStorageLimitException? limit = null;

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfInactive();
            try
            {
                await using var reservation = await _owner.ReserveWriteAsync(
                    this,
                    bytes.Length,
                    token).ConfigureAwait(false);
                await using var output = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                reservation.Commit(bytes.Length);
                Interlocked.Add(ref _bytesReceived, bytes.Length);
                _lastActivityUtc = _timeProvider.GetUtcNow();
                if (_lastActivityUtc - _lastManifestWriteUtc >= TimeSpan.FromSeconds(2))
                {
                    try
                    {
                        await WriteManifestAsync().ConfigureAwait(false);
                        _lastManifestWriteUtc = _lastActivityUtc;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (SupportLogStorageLimitException ex)
            {
                limit = ex;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (limit is not null)
        {
            await CompleteAsync("stopped: " + limit.Reason, CancellationToken.None)
                .ConfigureAwait(false);
            throw limit;
        }

        if (_lastActivityUtc - _lastIndexWriteUtc >= TimeSpan.FromSeconds(2))
        {
            _lastIndexWriteUtc = _lastActivityUtc;
            try
            {
                await _owner.SessionChangedAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        RaiseStatusChanged();
    }

    private async Task WriteManifestAsync(DateTimeOffset? completedAtUtc = null)
    {
        var streams = _streams.Values.Select(stream =>
            new SupportLogStorage.StoredStreamManifest(
                stream.Descriptor.SourceId,
                stream.Descriptor.Kind,
                stream.Descriptor.DisplayName,
                stream.ReceiverFileName,
                stream.Descriptor.SourceLength,
                stream.Descriptor.LastWriteUtc)).ToArray();
        ulong acceptedSequence;
        string acceptedHash;
        lock (_acceptedStateGate)
        {
            acceptedSequence = _highestAcceptedSequence;
            acceptedHash = _highestAcceptedHash;
        }
        var manifest = new SupportLogStorage.StoredSessionManifest(
            SupportLogStorage.SchemaVersion,
            SessionId,
            PeerIdentityId,
            PeerPlayerName,
            _descriptor.StartedAtUtc,
            _timeProvider.GetUtcNow(),
            completedAtUtc,
            _isActive,
            BytesReceived,
            _stopReason,
            new Dictionary<string, string>(_metadata, StringComparer.Ordinal),
            streams,
            acceptedSequence,
            acceptedHash);
        await _owner.WriteTrackedTextAsync(
            _manifestPath,
            JsonSerializer.Serialize(
                manifest,
                IndentedJsonOptions),
            CancellationToken.None).ConfigureAwait(false);
        lock (_acceptedStateGate)
        {
            if (_highestAcceptedSequence == acceptedSequence &&
                string.Equals(
                    _highestAcceptedHash,
                    acceptedHash,
                    StringComparison.Ordinal))
            {
                _acceptedStateDirty = false;
            }
        }
    }

    private void ThrowIfInactive()
    {
        if (!_isActive)
        {
            throw new InvalidOperationException("The diagnostic receive session is no longer active.");
        }
    }

    private void RaiseStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(Status);
        }
        catch
        {
        }
    }

    private long CalculateReceivedPayloadBytes()
    {
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(
                     SessionDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                total = checked(total + new FileInfo(path).Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
            {
            }
        }
        return total;
    }

    private static bool TryNormalizeSha256(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
        {
            return false;
        }
        try
        {
            var bytes = Convert.FromHexString(value);
            if (bytes.Length != 32) return false;
            normalized = Convert.ToHexString(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsSensitiveMetadataKey(string key)
    {
        var normalized = key.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return normalized.Contains("token", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("passwd", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("sessionid", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("oauthcode", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildReceiverFileName(SupportLogSourceKind kind, int ordinal)
    {
        var prefix = kind switch
        {
            SupportLogSourceKind.Launcher => "launcher",
            SupportLogSourceKind.Game => "game",
            SupportLogSourceKind.CrashReport => "crash-report",
            SupportLogSourceKind.Environment => "environment",
            _ => throw new InvalidDataException("The diagnostic source kind is invalid.")
        };
        return $"{prefix}-{ordinal:D4}.log";
    }

    private sealed record RegisteredStream(
        SupportLogStreamDescriptor Descriptor,
        string ReceiverFileName);
}

/// <summary>
/// Persistent outgoing sequence spool. It lives outside SupportLogs, is bounded by a per-session
/// quota and deliberately survives reconnects until acknowledged.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001",
    Justification = "The spool lifetime matches an outgoing session; SemaphoreSlim is used without its wait handle.")]
public sealed class SupportLogSpool
{
    public const long DefaultQuotaBytes = SupportLogStorage.MaxSessionBytes;
    public const int MaxRecordBytes = 256 * 1024;

    private readonly string _directory;
    private readonly long _quotaBytes;
    private readonly long _maxTotalBytes;
    private readonly long _minimumFreeBytes;
    private readonly Func<long, bool>? _freeSpaceProbe;
    private readonly TimeProvider _timeProvider;
    private readonly SupportLogCombinedQuota _combinedQuota;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _bytes;
    private ulong _highestSequence;

    public SupportLogSpool(
        AppPaths paths,
        Guid sessionId,
        long quotaBytes = DefaultQuotaBytes)
        : this(
            paths,
            sessionId,
            quotaBytes,
            SupportLogStorage.MaxTotalBytes,
            SupportLogStorage.MinimumFreeBytes,
            freeSpaceProbe: null,
            TimeProvider.System,
            SupportLogCombinedQuota.For(paths))
    {
    }

    internal SupportLogSpool(
        AppPaths paths,
        Guid sessionId,
        SupportLogStorage storage,
        long quotaBytes = DefaultQuotaBytes)
        : this(
            paths,
            sessionId,
            quotaBytes,
            storage.ConfiguredMaxTotalBytes,
            storage.ConfiguredMinimumFreeBytes,
            storage.ConfiguredFreeSpaceProbe,
            storage.ConfiguredTimeProvider,
            storage.CombinedQuota)
    {
    }

    private SupportLogSpool(
        AppPaths paths,
        Guid sessionId,
        long quotaBytes,
        long maxTotalBytes,
        long minimumFreeBytes,
        Func<long, bool>? freeSpaceProbe,
        TimeProvider timeProvider,
        SupportLogCombinedQuota combinedQuota)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(combinedQuota);
        if (sessionId == Guid.Empty) throw new ArgumentException("Session ID is empty.", nameof(sessionId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quotaBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTotalBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumFreeBytes);
        _directory = Path.GetFullPath(Path.Combine(paths.SupportSpool, sessionId.ToString("N")));
        var root = Path.GetFullPath(paths.SupportSpool).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!_directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Spool path escapes SupportSpool.");
        }
        _quotaBytes = quotaBytes;
        _maxTotalBytes = maxTotalBytes;
        _minimumFreeBytes = minimumFreeBytes;
        _freeSpaceProbe = freeSpaceProbe;
        _timeProvider = timeProvider;
        _combinedQuota = combinedQuota;
        Directory.CreateDirectory(_directory);
        _combinedQuota.InitializeAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        LoadExisting();
    }

    public string DirectoryPath => _directory;
    public long Bytes => Interlocked.Read(ref _bytes);
    public ulong HighestSequence => _highestSequence;

    public async Task EnqueueAsync(
        ulong sequence,
        ReadOnlyMemory<byte> payload,
        CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sequence);
        if (payload.Length > MaxRecordBytes)
        {
            throw new InvalidDataException("A diagnostics spool record exceeds 256 KiB.");
        }

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var path = GetRecordPath(sequence);
            if (File.Exists(path))
            {
                var existing = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
                if (!existing.AsSpan().SequenceEqual(payload.Span))
                {
                    throw new InvalidDataException(
                        "A diagnostics spool sequence was reused with different content.");
                }
                return;
            }
            if (_bytes + payload.Length > _quotaBytes)
            {
                throw new SupportLogStorageLimitException("Outgoing diagnostics spool quota exhausted.");
            }
            await using var reservation = await _combinedQuota.ReserveAsync(
                checked(
                    payload.Length +
                    SupportLogCombinedQuota.GetControlFileHeadroom(_maxTotalBytes)),
                payload.Length,
                _maxTotalBytes,
                _minimumFreeBytes,
                _freeSpaceProbe,
                _timeProvider,
                token).ConfigureAwait(false);
            AtomicFile.WriteAllBytes(path, payload.Span);
            reservation.Commit(payload.Length);
            _bytes += payload.Length;
            _highestSequence = Math.Max(_highestSequence, sequence);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async IAsyncEnumerable<SupportLogSpoolRecord> ReplayFromAsync(
        ulong sequenceExclusive,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        string[] files;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            files = Directory.EnumerateFiles(_directory, "*.frame", SearchOption.TopDirectoryOnly)
                .Select(path => new { Path = path, Sequence = TryParseSequence(path) })
                .Where(item => item.Sequence is not null && item.Sequence > sequenceExclusive)
                .OrderBy(item => item.Sequence)
                .Select(item => item.Path)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var sequence = TryParseSequence(file);
            if (sequence is null) continue;
            byte[] payload;
            try
            {
                payload = await File.ReadAllBytesAsync(file, token).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            yield return new SupportLogSpoolRecord(sequence.Value, payload);
        }
    }

    public async Task AckThroughAsync(ulong sequence, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*.frame", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                var recordSequence = TryParseSequence(file);
                if (recordSequence is null || recordSequence > sequence) continue;
                try
                {
                    var length = new FileInfo(file).Length;
                    File.Delete(file);
                    _bytes = Math.Max(0, _bytes - length);
                    await _combinedQuota.ReleaseCommittedBytesAsync(
                        length,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (IOException)
                {
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DiscardAfterAsync(
        ulong sequence,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var file in Directory.EnumerateFiles(
                         _directory,
                         "*.frame",
                         SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                var recordSequence = TryParseSequence(file);
                if (recordSequence is null || recordSequence <= sequence) continue;
                try
                {
                    var length = new FileInfo(file).Length;
                    File.Delete(file);
                    _bytes = Math.Max(0, _bytes - length);
                    await _combinedQuota.ReleaseCommittedBytesAsync(
                        length,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (IOException)
                {
                }
            }
            _highestSequence = Math.Min(_highestSequence, sequence);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteIfEmptyAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(_directory) &&
                !Directory.EnumerateFileSystemEntries(_directory).Any())
            {
                Directory.Delete(_directory);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void LoadExisting()
    {
        foreach (var file in Directory.EnumerateFiles(_directory, "*.frame", SearchOption.TopDirectoryOnly))
        {
            var sequence = TryParseSequence(file);
            if (sequence is null) continue;
            try
            {
                _bytes = checked(_bytes + new FileInfo(file).Length);
                _highestSequence = Math.Max(_highestSequence, sequence.Value);
            }
            catch (Exception ex) when (ex is IOException or OverflowException)
            {
            }
        }
    }

    private string GetRecordPath(ulong sequence) =>
        Path.Combine(_directory, $"{sequence:D20}.frame");

    private static ulong? TryParseSequence(string path) =>
        ulong.TryParse(Path.GetFileNameWithoutExtension(path), out var sequence) && sequence > 0
            ? sequence
            : null;
}

public sealed record SupportLogSpoolRecord(ulong Sequence, ReadOnlyMemory<byte> Payload);

/// <summary>Token-bucket resource guard capped at eight MiB/s by default.</summary>
[SuppressMessage(
    "Design",
    "CA1001",
    Justification = "The short-lived limiter owns only a SemaphoreSlim used without its wait handle.")]
public sealed class SupportLogRateLimiter
{
    public const int DefaultBytesPerSecond = 8 * 1024 * 1024;

    private readonly int _bytesPerSecond;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastTimestamp;
    private double _tokens;

    public SupportLogRateLimiter(
        int bytesPerSecond = DefaultBytesPerSecond,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);
        _bytesPerSecond = bytesPerSecond;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastTimestamp = _timeProvider.GetTimestamp();
        _tokens = bytesPerSecond;
    }

    public async Task WaitAsync(int byteCount, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        if (byteCount == 0) return;

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                Refill();
                if (_tokens >= byteCount)
                {
                    _tokens -= byteCount;
                    return;
                }

                var missing = byteCount - _tokens;
                var delay = TimeSpan.FromSeconds(missing / _bytesPerSecond);
                await Task.Delay(delay, _timeProvider, token).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Refill()
    {
        var now = _timeProvider.GetTimestamp();
        var elapsed = _timeProvider.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
        _lastTimestamp = now;
        _tokens = Math.Min(_bytesPerSecond, _tokens + elapsed * _bytesPerSecond);
    }
}
