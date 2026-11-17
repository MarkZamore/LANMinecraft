using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Minecraft;

/// <summary>Runs one resumable background pass per launcher session.</summary>
internal sealed class LauncherMaintenance(
    AppPaths paths,
    Logger logger,
    Func<bool> foregroundIsActive,
    Action<LauncherMaintenanceRun> sweep,
    Func<bool>? gameIsRunning = null,
    Func<string, bool>? isPackInUse = null)
{
    // Keep the existing service filename so upgrades have only one durable record.
    internal const string CompletionFileName = ".launcher-maintenance.utc";
    private const int ProgressFormat = 1;
    private const int MaximumProgressBytes = 512 * 1024;
    private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan YieldBetweenSlices = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan GameSlice = TimeSpan.FromMilliseconds(8);
    private static readonly TimeSpan GameYield = TimeSpan.FromMilliseconds(150);
    private readonly object _gate = new();
    private readonly HashSet<string> _pendingPacks = new(StringComparer.OrdinalIgnoreCase);
    private bool _fullPassPending = true;
    private bool _deferredWork;
    private bool _mayStart;
    private bool _wasReady;
    private bool _observedGame;
    private bool _started;
    private bool _reportedCheckpointError;
    private CancellationTokenSource? _current;
    private bool _stopped;

    private string ProgressPath => Path.Combine(paths.Launcher, CompletionFileName);

    internal void RequestPack(string relativePath)
    {
        if (!PackCatalogService.ValidFolder(relativePath)) return;
        lock (_gate)
        {
            if (_stopped) return;
            _pendingPacks.Add(relativePath);
            _mayStart = true;
        }
    }

    internal void ObserveIdle(bool ready)
    {
        ready = ready && !foregroundIsActive();
        var hasGame = gameIsRunning?.Invoke() == true;
        lock (_gate)
        {
            if (_stopped) return;
            if (!ready)
            {
                _wasReady = false;
                if (_current is not null) _ = _current.CancelAsync();
                return;
            }
            if (!_wasReady || _observedGame != hasGame) _mayStart = true;
            _wasReady = true;
            _observedGame = hasGame;
            if (!_mayStart || _current is not null ||
                (!_fullPassPending && !_deferredWork && _pendingPacks.Count == 0))
                return;

            _mayStart = false;
            var cleanupInterruptedWrites = !_started;
            _started = true;
            var fullPass = _fullPassPending;
            var pending = _pendingPacks.ToArray();
            _pendingPacks.Clear();
            var cancellation = new CancellationTokenSource();
            _current = cancellation;
            var worker = new Thread(() => Run(fullPass, hasGame, cleanupInterruptedWrites, pending, cancellation))
            {
                IsBackground = true,
                Name = "Launcher storage maintenance",
                Priority = ThreadPriority.BelowNormal
            };
            worker.Start();
        }
    }

    internal void Interrupt()
    {
        lock (_gate)
        {
            if (_current is null) return;
            _mayStart = true;
            _ = _current.CancelAsync();
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            if (_current is not null) _ = _current.CancelAsync();
        }
    }

    private void Run(bool fullPass, bool hasGame, bool cleanupInterruptedWrites, string[] pending,
        CancellationTokenSource cancellation)
    {
        var loweredIo = SetThreadPriority(GetCurrentThread(), 0x00010000); // THREAD_MODE_BACKGROUND_BEGIN
        var token = cancellation.Token;
        var sliceStart = Stopwatch.GetTimestamp();
        LauncherMaintenanceRun? pass = null;
        var returned = false;
        try
        {
            Checkpoint(token);
            if (cleanupInterruptedWrites) RemoveInterruptedWrites(Checkpoint, token);
            var progress = ReadProgress();
            pass = new LauncherMaintenanceRun(fullPass, hasGame, pending, isPackInUse, progress,
                () => WriteProgress(progress), Checkpoint, token);
            foreach (var pack in pending)
                pass.RequeuePack(pack);
            sweep(pass);
            Checkpoint(token);
            if (!pass.HasDeferredWork)
                MaintenancePath.TryDelete(paths, ProgressPath, directory: false);
            returned = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            logger.Warn($"Launcher storage maintenance did not finish: {ex.Message}");
        }
        finally
        {
            if (loweredIo) _ = SetThreadPriority(GetCurrentThread(), 0x00020000); // THREAD_MODE_BACKGROUND_END
            lock (_gate)
            {
                if (returned && pass is not null)
                {
                    _deferredWork = pass.HasDeferredWork;
                    if (!_deferredWork) _fullPassPending = false;
                }
                else
                {
                    _pendingPacks.UnionWith(pending);
                    _deferredWork = true;
                }
                if (ReferenceEquals(_current, cancellation)) _current = null;
            }
            cancellation.Dispose();
        }

        void Checkpoint(CancellationToken caller)
        {
            caller.ThrowIfCancellationRequested();
            ThrowIfForegroundChanged();
            if (Stopwatch.GetElapsedTime(sliceStart) < (hasGame ? GameSlice : Slice)) return;
            token.WaitHandle.WaitOne(hasGame ? GameYield : YieldBetweenSlices);
            ThrowIfForegroundChanged();
            sliceStart = Stopwatch.GetTimestamp();
        }

        void ThrowIfForegroundChanged()
        {
            token.ThrowIfCancellationRequested();
            if (foregroundIsActive() || (gameIsRunning?.Invoke() == true) != hasGame)
                throw new OperationCanceledException(token);
        }
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
                        new HashSet<string>(saved.CompletedSteps, StringComparer.OrdinalIgnoreCase),
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

    private void WriteProgress(LauncherMaintenanceProgress progress)
    {
        if (!MaintenancePath.TryRead(paths, paths.Launcher, out var folderAttributes) ||
            !folderAttributes.HasFlag(FileAttributes.Directory)) return;
        var path = ProgressPath;
        try
        {
            _ = File.GetAttributes(path);
            if (!MaintenancePath.TryRead(paths, path, out var attributes) || attributes.HasFlag(FileAttributes.Directory)) return;
        }
        catch (FileNotFoundException) { }
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(progress);
            if (bytes.Length <= MaximumProgressBytes) AtomicFile.WriteAllBytes(path, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_reportedCheckpointError) return;
            logger.Warn($"Could not save storage maintenance progress: {ex.Message}");
            _reportedCheckpointError = true;
        }
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
    bool gameIsRunning,
    IReadOnlyList<string> requestedPacks,
    Func<string, bool>? isPackInUse,
    LauncherMaintenanceProgress progress,
    Action persist,
    Action<CancellationToken> checkpoint,
    CancellationToken token)
{
    private bool _idleStepsPending;
    internal bool FullPass => fullPass;
    internal bool GameIsRunning => gameIsRunning;
    internal IReadOnlyList<string> RequestedPacks => requestedPacks;
    internal IReadOnlyList<string> PriorityPacks => progress.DeferredPacks.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    internal CancellationToken Token => token;
    internal bool HasDeferredWork => _idleStepsPending || progress.DeferredPacks.Count != 0;

    internal bool IsPackInUse(string relativePath) => isPackInUse?.Invoke(relativePath) ?? GameIsRunning;
    internal void Checkpoint(CancellationToken caller) => checkpoint(caller);

    internal bool RunStep(string key, Action work)
    {
        Checkpoint(token);
        if (progress.CompletedSteps.Contains(key)) return false;

        work();
        Checkpoint(token);
        progress.CompletedSteps.Add(key);
        progress.Cursors.Remove(key);
        persist();
        return true;
    }

    internal void DeferStep(string key)
    {
        if (!progress.CompletedSteps.Contains(key)) DeferPass();
    }

    internal void DeferPass() => _idleStepsPending = true;

    internal void DeferPack(string relativePath)
    {
        if (PackCatalogService.ValidFolder(relativePath) && progress.DeferredPacks.Add(relativePath))
            persist();
    }

    internal void RequeuePack(string relativePath)
    {
        var reset = progress.CompletedSteps.Remove("pack:" + relativePath);
        if (progress.DeferredPacks.Add(relativePath) || reset) persist();
    }

    internal void CompletePack(string relativePath)
    {
        if (progress.DeferredPacks.Remove(relativePath)) persist();
    }

    internal string? Cursor(string step) => progress.Cursors.GetValueOrDefault(step);

    internal void SaveCursor(string step, string position)
    {
        Checkpoint(token);
        if (position is not { Length: > 0 and <= 16384 } || Cursor(step) == position) return;
        progress.Cursors[step] = position;
        persist();
    }
}
