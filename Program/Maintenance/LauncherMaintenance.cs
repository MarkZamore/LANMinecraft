using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Minecraft;

/// <summary>Runs one resumable background pass per launcher session.</summary>
internal sealed class LauncherMaintenance(
    AppPaths paths,
    Logger logger,
    Action<LauncherMaintenanceRun> sweep,
    Func<bool>? gameIsRunning = null,
    Func<string, bool>? isPackInUse = null,
    Action<int>? reportProgress = null,
    Action<bool>? reportActivity = null)
{
    // Keep the existing service filename so upgrades have only one durable record.
    internal const string CompletionFileName = ".launcher-maintenance.utc";
    private const int ProgressFormat = 1;
    private const int MaximumProgressBytes = 512 * 1024;
    private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan YieldBetweenSlices = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan GameSlice = TimeSpan.FromMilliseconds(8);
    private static readonly TimeSpan GameYield = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan CursorSaveInterval = TimeSpan.FromSeconds(1);
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _protectedPacks = new(StringComparer.OrdinalIgnoreCase);
    private bool _started;
    private LauncherMaintenanceProgress? _progress;
    private bool _reportedCheckpointError;
    private int? _lastReportedProgress;
    private CancellationTokenSource? _current;
    private bool _stopped;

    private string ProgressPath => Path.Combine(paths.Launcher, CompletionFileName);

    internal void Start()
    {
        lock (_gate)
        {
            if (_stopped || _started) return;
            _started = true;
            var cancellation = new CancellationTokenSource();
            _current = cancellation;
            new Thread(() => Run(cancellation))
            {
                IsBackground = true,
                Name = "Launcher storage maintenance",
                Priority = ThreadPriority.BelowNormal
            }.Start();
        }
    }

    internal IDisposable ProtectPack(string path)
    {
        lock (_gate) _protectedPacks[path] = _protectedPacks.GetValueOrDefault(path) + 1;
        return new PackUse(this, path);
    }

    private sealed class PackUse(LauncherMaintenance owner, string path) : IDisposable
    {
        private LauncherMaintenance? _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } maintenance) return;
            lock (maintenance._gate)
            {
                var remaining = maintenance._protectedPacks[path] - 1;
                if (remaining == 0) maintenance._protectedPacks.Remove(path);
                else maintenance._protectedPacks[path] = remaining;
            }
        }
    }

    private bool IsPackProtected(string path)
    {
        lock (_gate)
        {
            if (_protectedPacks.ContainsKey(path)) return true;
        }
        return isPackInUse?.Invoke(path) ?? (gameIsRunning?.Invoke() == true);
    }

    internal void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            if (_current is not null) _ = _current.CancelAsync();
        }
    }

    private void Run(CancellationTokenSource cancellation)
    {
        var loweredIo = SetThreadPriority(GetCurrentThread(), 0x00010000); // THREAD_MODE_BACKGROUND_BEGIN
        var token = cancellation.Token;
        var sliceStart = Stopwatch.GetTimestamp();
        var hasGame = gameIsRunning?.Invoke() == true;
        var lastSave = Stopwatch.GetTimestamp();
        var checkpointDirty = false;
        try
        {
            if (LauncherStorageLayout.ReadSchema(paths) == LauncherStorageSchema.Unsupported) return;
            reportActivity?.Invoke(true);
            Checkpoint(token);
            PublishProgress(0);
            RemoveInterruptedWrites(Checkpoint, token);
            var progress = _progress = ReadProgress();
            var pass = new LauncherMaintenanceRun(true, () => gameIsRunning?.Invoke() == true, [], IsPackProtected, progress,
                Persist, Checkpoint, token, PublishProgress);
            sweep(pass);
            Checkpoint(token);
            if (!pass.HasDeferredWork)
            {
                MaintenancePath.TryDelete(paths, ProgressPath, directory: false);
                _progress = null;
                PublishProgress(100);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            TryLogWarning($"Launcher storage maintenance did not finish: {ex.Message}");
        }
        finally
        {
            SaveIfDirty();
            reportActivity?.Invoke(false);
            if (loweredIo) _ = SetThreadPriority(GetCurrentThread(), 0x00020000); // THREAD_MODE_BACKGROUND_END
            lock (_gate)
            {
                _current = null;
            }
            cancellation.Dispose();
        }

        void Persist(bool force)
        {
            checkpointDirty = true;
            if (force || Stopwatch.GetElapsedTime(lastSave) >= CursorSaveInterval)
                SaveIfDirty();
        }

        void SaveIfDirty()
        {
            if (!checkpointDirty || _progress is null) return;
            lastSave = Stopwatch.GetTimestamp();
            checkpointDirty = !WriteProgress(_progress);
        }

        void Checkpoint(CancellationToken caller)
        {
            caller.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(sliceStart) < (hasGame ? GameSlice : Slice)) return;
            hasGame = gameIsRunning?.Invoke() == true;
            token.WaitHandle.WaitOne(hasGame ? GameYield : YieldBetweenSlices);
            caller.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            sliceStart = Stopwatch.GetTimestamp();
        }
    }

    private void PublishProgress(int value)
    {
        if (reportProgress is null || _lastReportedProgress == value) return;
        _lastReportedProgress = value;
        reportProgress(value);
    }

    private LauncherMaintenanceProgress ReadProgress()
    {
        var currentRelease = UpdateService.CurrentReleaseNumber;
        var path = ProgressPath;
        var fresh = new LauncherMaintenanceProgress(ProgressFormat, currentRelease,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        try
        {
            if (MaintenancePath.TryRead(paths, path, out var attributes) && !attributes.HasFlag(FileAttributes.Directory) &&
                new FileInfo(path).Length <= MaximumProgressBytes)
            {
                using var stream = File.OpenRead(path);
                var saved = JsonSerializer.Deserialize<LauncherMaintenanceProgress>(stream);
                if (saved is { Format: ProgressFormat, CompletedSteps: not null, DeferredPacks: not null, Cursors: not null } &&
                    saved.DeferredPacks.All(PackCatalogService.ValidFolder) &&
                    saved.CompletedSteps.All(IsStepName) &&
                    saved.Cursors.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == saved.Cursors.Count &&
                    saved.Cursors.All(item => IsStepName(item.Key) && item.Value is { Length: > 0 and <= 16384 }))
                {
                    fresh.DeferredPacks.UnionWith(saved.DeferredPacks);
                    if (saved.Release != currentRelease) return fresh;
                    return new LauncherMaintenanceProgress(ProgressFormat, currentRelease,
                        // A pack may have updated after its previous cleanup. Recheck packs
                        // once at startup while retaining service steps and partial cursors.
                        new HashSet<string>(saved.CompletedSteps.Where(step =>
                            !step.StartsWith("pack:", StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase),
                        fresh.DeferredPacks,
                        new Dictionary<string, string>(saved.Cursors, StringComparer.OrdinalIgnoreCase));
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A malformed checkpoint or the old timestamp safely starts a fresh pass.
        }
        return fresh;

        static bool IsStepName(string? name) => name is { Length: > 0 and <= 256 } && !name.Any(char.IsControl);
    }

    private bool WriteProgress(LauncherMaintenanceProgress progress)
    {
        if (!MaintenancePath.TryRead(paths, paths.Launcher, out var folderAttributes) ||
            !folderAttributes.HasFlag(FileAttributes.Directory)) return false;
        var path = ProgressPath;
        var temporaryPath = Path.Combine(paths.Launcher, $".{CompletionFileName}.{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(progress);
            if (bytes.Length > MaximumProgressBytes) return false;
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(bytes);
            }
            try
            {
                _ = File.GetAttributes(path);
                if (!MaintenancePath.TryRead(paths, path, out var attributes) || attributes.HasFlag(FileAttributes.Directory)) return false;
            }
            catch (FileNotFoundException) { }
            // An unsuccessful rename leaves the previous checkpoint readable.
            File.Move(temporaryPath, path, overwrite: true);
            created = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_reportedCheckpointError)
            {
                _reportedCheckpointError = true;
                TryLogWarning($"Could not save storage maintenance progress: {ex.Message}");
            }
            return false;
        }
        finally
        {
            if (created) MaintenancePath.TryDelete(paths, temporaryPath, directory: false);
        }
    }

    private void TryLogWarning(string message)
    {
        try { logger.Warn(message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void RemoveInterruptedWrites(Action<CancellationToken> checkpoint, CancellationToken token)
    {
        if (!MaintenancePath.TryRead(paths, paths.Launcher, out var attributes) || !attributes.HasFlag(FileAttributes.Directory)) return;
        var prefix = "." + CompletionFileName + ".";
        const string suffix = ".tmp";
        foreach (var path in Directory.EnumerateFiles(paths.Launcher, prefix + "*" + suffix))
        {
            checkpoint(token);
            var name = Path.GetFileName(path);
            if (name.Length == prefix.Length + 32 + suffix.Length &&
                name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(name.AsSpan(prefix.Length, 32), "N", out _))
                MaintenancePath.TryDelete(paths, path, directory: false);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(nint thread, int priority);
}

internal sealed record LauncherMaintenanceProgress(
    int Format,
    int Release,
    HashSet<string> CompletedSteps,
    HashSet<string> DeferredPacks,
    Dictionary<string, string> Cursors);

/// <summary>Records validated units of work without making the UI save a checkpoint.</summary>
internal sealed class LauncherMaintenanceRun(
    bool fullPass,
    Func<bool> gameIsRunning,
    IReadOnlyList<string> requestedPacks,
    Func<string, bool> isPackInUse,
    LauncherMaintenanceProgress progress,
    Action<bool> persist,
    Action<CancellationToken> checkpoint,
    CancellationToken token,
    Action<int> reportProgress)
{
    private bool _idleStepsPending;
    private HashSet<string>? _plannedSteps;
    private int _completedPlannedSteps;
    internal bool FullPass => fullPass;
    internal bool GameIsRunning => gameIsRunning();
    internal IReadOnlyList<string> RequestedPacks => requestedPacks;
    internal IReadOnlyList<string> PriorityPacks => progress.DeferredPacks.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    internal CancellationToken Token => token;
    internal bool HasDeferredWork => _idleStepsPending || progress.DeferredPacks.Count != 0;

    internal bool IsPackInUse(string relativePath) => isPackInUse(relativePath);
    internal void Checkpoint(CancellationToken caller) => checkpoint(caller);

    internal void PlanSteps(IEnumerable<string> keys)
    {
        checkpoint(token);
        _plannedSteps = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _completedPlannedSteps = _plannedSteps.Count(progress.CompletedSteps.Contains);
        ReportCompletedSteps();
    }

    internal bool RunStep(string key, Action work)
    {
        Checkpoint(token);
        if (progress.CompletedSteps.Contains(key)) return false;

        work();
        Checkpoint(token);
        progress.CompletedSteps.Add(key);
        progress.Cursors.Remove(key);
        persist(true);
        if (_plannedSteps?.Contains(key) == true)
        {
            _completedPlannedSteps++;
            ReportCompletedSteps();
        }
        return true;
    }

    private void ReportCompletedSteps()
    {
        if (_plannedSteps is { Count: > 0 })
            reportProgress(Math.Min(99, (int)(100L * _completedPlannedSteps / _plannedSteps.Count)));
    }

    internal void DeferStep(string key)
    {
        if (!progress.CompletedSteps.Contains(key)) DeferPass();
    }

    internal void DeferPass() => _idleStepsPending = true;

    internal void DeferPack(string relativePath)
    {
        if (PackCatalogService.ValidFolder(relativePath) && progress.DeferredPacks.Add(relativePath))
            persist(true);
    }

    internal void CompletePack(string relativePath)
    {
        if (progress.DeferredPacks.Remove(relativePath)) persist(true);
    }

    internal string? Cursor(string step) => progress.Cursors.GetValueOrDefault(step);

    internal void SaveCursor(string step, string position)
    {
        Checkpoint(token);
        if (position is not { Length: > 0 and <= 16384 } || Cursor(step) == position) return;
        progress.Cursors[step] = position;
        persist(false);
    }
}
