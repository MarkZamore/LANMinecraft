using System.IO;
using System.Text.Json;

namespace Minecraft;

/// <summary>
/// A write-ahead journal for the handful of files a launch rewrites inside a
/// world (level.dat, playerdata, the player manifest). A crash between two of
/// those writes leaves a world half-converted, which is what recovery repairs.
/// </summary>
internal sealed class ProfileFileTransaction : IDisposable
{
    /// <summary>The launcher's one format version; see <see cref="PortableFormat"/>.</summary>
    private const int SchemaVersion = PortableFormat.SchemaVersion;
    private const string QuarantineDirectoryName = "Quarantine";
    private static readonly TimeSpan QuarantineRetention = TimeSpan.FromDays(30);

    /// <summary>
    /// A journal older than this is not replayed. Recovery restores files as
    /// they were when the transaction started, so an ancient journal would roll
    /// a world back over everything played since.
    /// </summary>
    private static readonly TimeSpan MaximumReplayAge = TimeSpan.FromDays(2);
    private static readonly JsonSerializerOptions ReadJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppPaths _paths;
    private readonly string _transactionRoot;
    private readonly string _journalPath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ProfileTransactionJournal _journal;
    private readonly Dictionary<string, ProfileTransactionEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _finished;

    private ProfileFileTransaction(AppPaths paths, string operation)
    {
        _paths = paths;
        var root = paths.ProfileTransactions;
        paths.EnsureUnderRoot(root);
        Directory.CreateDirectory(root);
        _transactionRoot = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_transactionRoot);
        _journalPath = Path.Combine(_transactionRoot, "transaction.json");
        _journal = new ProfileTransactionJournal
        {
            SchemaVersion = SchemaVersion,
            Operation = operation,
            State = "Active",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            OwnerProcessId = Environment.ProcessId,
            OwnerProcessStartedAtUtc = ProcessStartedAtUtc(Environment.ProcessId)
        };
        PersistJournal();
    }

    public static ProfileFileTransaction Begin(AppPaths paths, string operation)
    {
        return new ProfileFileTransaction(paths, operation);
    }

    /// <summary>
    /// Replays whatever a previous run left behind. A journal is only replayed
    /// when its owner is gone and it is recent; anything else is left alone
    /// (another launcher is still using it) or set aside for a human to look at.
    /// Nothing here throws: a damaged journal must never be the reason the
    /// launcher refuses to start.
    /// </summary>
    public static void RecoverPending(AppPaths paths, Logger? logger)
    {
        var root = paths.ProfileTransactions;
        if (!Directory.Exists(root)) return;

        foreach (var transactionRoot in Directory
                     .EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                     .Where(directory => !string.Equals(
                         Path.GetFileName(directory),
                         QuarantineDirectoryName,
                         StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            var name = Path.GetFileName(transactionRoot);
            try
            {
                var journalPath = Path.Combine(transactionRoot, "transaction.json");
                if (!File.Exists(journalPath))
                {
                    Directory.Delete(transactionRoot, recursive: true);
                    continue;
                }

                var journal = JsonSerializer.Deserialize<ProfileTransactionJournal>(
                                  File.ReadAllText(journalPath), ReadJsonOptions)
                    ?? throw new InvalidDataException("Profile transaction journal is empty.");

                if (!PortableFormat.CanRead(journal.SchemaVersion))
                {
                    // Written by a newer build; that build owns its own repair.
                    logger?.Warn(
                        $"Player profile transaction {name} uses format {journal.SchemaVersion}; leaving it untouched.");
                    continue;
                }

                if (IsOwnerAlive(journal))
                {
                    logger?.Info($"Player profile transaction {name} belongs to a running launcher; skipped.");
                    continue;
                }

                if (string.Equals(journal.State, "Committed", StringComparison.Ordinal))
                {
                    Directory.Delete(transactionRoot, recursive: true);
                    continue;
                }

                var age = DateTimeOffset.UtcNow - journal.CreatedAtUtc;
                if (journal.CreatedAtUtc == default || age > MaximumReplayAge || age < -MaximumReplayAge)
                {
                    logger?.Warn(
                        $"Player profile transaction {name} is {age.TotalDays:F1} days old; " +
                        "it is set aside instead of replayed.");
                    Quarantine(root, transactionRoot, logger);
                    continue;
                }

                RestoreEntries(paths, transactionRoot, journal.Entries);
                logger?.Warn($"Recovered interrupted player profile transaction {name}.");
                Directory.Delete(transactionRoot, recursive: true);
            }
            catch (Exception ex)
            {
                logger?.Warn($"Could not recover player profile transaction {name}: {ex.Message}");
                Quarantine(root, transactionRoot, logger);
            }
        }

        PruneQuarantine(root, logger);
    }

    /// <summary>
    /// Moves a transaction nobody can replay out of the way. The backups stay
    /// on disk - they may be the only copy of a player's data - but they no
    /// longer block startup or get retried on every launch.
    /// </summary>
    private static void Quarantine(string root, string transactionRoot, Logger? logger)
    {
        try
        {
            if (!Directory.Exists(transactionRoot)) return;
            var quarantine = Path.Combine(root, QuarantineDirectoryName);
            Directory.CreateDirectory(quarantine);
            var target = Path.Combine(
                quarantine,
                $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Path.GetFileName(transactionRoot)}");
            if (Directory.Exists(target)) return;
            Directory.Move(transactionRoot, target);
            logger?.Warn($"Player profile transaction moved to {target}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warn($"Player profile transaction could not be set aside: {ex.Message}");
        }
    }

    /// <summary>Backups are full copies of world files; they do not live forever.</summary>
    private static void PruneQuarantine(string root, Logger? logger)
    {
        var quarantine = Path.Combine(root, QuarantineDirectoryName);
        if (!Directory.Exists(quarantine)) return;
        var cutoff = DateTime.UtcNow - QuarantineRetention;
        foreach (var directory in Directory.EnumerateDirectories(quarantine).ToArray())
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) > cutoff) continue;
                Directory.Delete(directory, recursive: true);
                logger?.Info($"Removed an expired player profile transaction backup: {Path.GetFileName(directory)}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// True while the launcher that opened the journal is still running, which
    /// is what stops a second instance from rolling back a live transaction.
    /// </summary>
    private static bool IsOwnerAlive(ProfileTransactionJournal journal)
    {
        if (journal.OwnerProcessId <= 0) return false;
        if (journal.OwnerProcessId == Environment.ProcessId) return true;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(journal.OwnerProcessId);
            // The id may have been reused since; the start time settles it.
            return journal.OwnerProcessStartedAtUtc == default ||
                   Math.Abs((process.StartTime.ToUniversalTime() - journal.OwnerProcessStartedAtUtc).TotalSeconds) < 2;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static DateTime ProcessStartedAtUtc(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return default;
        }
    }

    public void Track(string path)
    {
        EnsureActive();
        var fullPath = Path.GetFullPath(path);
        _paths.EnsureUnderRoot(fullPath);
        if (_entries.ContainsKey(fullPath)) return;

        var entry = new ProfileTransactionEntry
        {
            Path = fullPath,
            Existed = File.Exists(fullPath),
            BackupFile = $"{_entries.Count:D6}.backup"
        };
        if (entry.Existed)
        {
            var backupPath = Path.Combine(_transactionRoot, entry.BackupFile);
            CopyWithRetry(fullPath, backupPath);
            File.SetAttributes(backupPath, FileAttributes.Normal);
            entry.LastWriteUtc = File.GetLastWriteTimeUtc(fullPath);
            entry.Attributes = File.GetAttributes(fullPath);
        }

        _entries.Add(fullPath, entry);
        _journal.Entries.Add(entry);
        PersistJournal();
    }

    public void Commit()
    {
        EnsureActive();
        _journal.State = "Committed";
        PersistJournal();
        _finished = true;
        DeleteTransactionDirectory();
    }

    public void Rollback()
    {
        if (_finished) return;
        RestoreEntries(_paths, _transactionRoot, _journal.Entries);
        _finished = true;
        DeleteTransactionDirectory();
    }

    public void Dispose()
    {
        if (!_finished) Rollback();
    }

    private void PersistJournal()
    {
        AtomicFile.WriteAllText(_journalPath, JsonSerializer.Serialize(_journal, _jsonOptions));
    }

    private static void RestoreEntries(
        AppPaths paths,
        string transactionRoot,
        IReadOnlyList<ProfileTransactionEntry> entries)
    {
        List<Exception>? failures = null;
        foreach (var entry in entries.Reverse())
        {
            try
            {
                var fullPath = Path.GetFullPath(entry.Path);
                paths.EnsureUnderRoot(fullPath);
                if (entry.Existed)
                {
                    var backupPath = Path.Combine(transactionRoot, entry.BackupFile);
                    if (!File.Exists(backupPath))
                    {
                        throw new FileNotFoundException("Profile transaction backup is missing.", backupPath);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    if (File.Exists(fullPath)) File.SetAttributes(fullPath, FileAttributes.Normal);
                    File.Copy(backupPath, fullPath, overwrite: true);
                    File.SetLastWriteTimeUtc(fullPath, entry.LastWriteUtc);
                    File.SetAttributes(fullPath, entry.Attributes);
                }
                else if (File.Exists(fullPath))
                {
                    File.SetAttributes(fullPath, FileAttributes.Normal);
                    File.Delete(fullPath);
                }
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        if (failures is { Count: > 0 })
        {
            throw new AggregateException("Player profile rollback was incomplete.", failures);
        }
    }

    // The root itself stays: it is a fixed launcher directory now, not a
    // temporary one, and deleting it would take the quarantine with it.
    private void DeleteTransactionDirectory()
    {
        if (Directory.Exists(_transactionRoot)) Directory.Delete(_transactionRoot, recursive: true);
    }

    private void EnsureActive()
    {
        if (_finished) throw new InvalidOperationException("Player profile transaction is already finished.");
    }

    private sealed class ProfileTransactionJournal
    {
        public int SchemaVersion { get; set; } = ProfileFileTransaction.SchemaVersion;
        public string Operation { get; set; } = string.Empty;
        public string State { get; set; } = "Active";
        public DateTimeOffset CreatedAtUtc { get; set; }

        /// <summary>Who is writing it; a live owner is never rolled back by anyone else.</summary>
        public int OwnerProcessId { get; set; }

        /// <summary>Process ids are reused, so the start time identifies the owner.</summary>
        public DateTime OwnerProcessStartedAtUtc { get; set; }

        public List<ProfileTransactionEntry> Entries { get; set; } = [];
    }

    private sealed class ProfileTransactionEntry
    {
        public string Path { get; set; } = string.Empty;
        public bool Existed { get; set; }
        public string BackupFile { get; set; } = string.Empty;
        public DateTime LastWriteUtc { get; set; }
        public FileAttributes Attributes { get; set; }
    }

    // A real-time scanner may hold the source for a few milliseconds right
    // after another write; retry briefly instead of failing the whole launch.
    private static void CopyWithRetry(string source, string destination)
    {
        const int attempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: false);
                return;
            }
            catch (IOException) when (attempt < attempts && !File.Exists(destination))
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }
}
