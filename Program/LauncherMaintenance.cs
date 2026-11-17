using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Minecraft;

/// <summary>Schedules storage maintenance without making foreground work wait for it.</summary>
internal sealed class LauncherMaintenance(
    AppPaths paths,
    Logger logger,
    Func<bool> foregroundIsActive,
    Action<bool, IReadOnlyList<string>, Action<CancellationToken>, CancellationToken> sweep,
    TimeProvider? timeProvider = null)
{
    internal const string CompletionFileName = ".launcher-maintenance.utc";
    internal static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FullPassInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan YieldBetweenSlices = TimeSpan.FromMilliseconds(75);
    private readonly object _gate = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly HashSet<string> _pendingPacks = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _nextFullPass;
    private long? _idleSince;
    private CancellationTokenSource? _current;
    private bool _stopped;

    internal void RequestPack(string relativePath)
    {
        lock (_gate)
            if (!_stopped) _pendingPacks.Add(relativePath);
    }

    internal void ObserveIdle(bool ready)
    {
        if (!ready)
        {
            Interrupt();
            return;
        }
        lock (_gate)
        {
            if (_stopped || _current is not null) return;
            _idleSince ??= _clock.GetTimestamp();
            if (_clock.GetElapsedTime(_idleSince.Value) < QuietPeriod) return;
            var mayRunFull = _nextFullPass is null || _clock.GetUtcNow() >= _nextFullPass;
            if (!mayRunFull && _pendingPacks.Count == 0) return;

            var pending = _pendingPacks.ToArray();
            _pendingPacks.Clear();
            var cancellation = new CancellationTokenSource();
            _current = cancellation;
            var worker = new Thread(() => Run(mayRunFull, pending, cancellation))
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
            _idleSince = null;
            if (_current is not null) _ = _current.CancelAsync();
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

    private void Run(bool mayRunFull, string[] pending, CancellationTokenSource cancellation)
    {
        var loweredIo = SetThreadPriority(GetCurrentThread(), 0x00010000); // THREAD_MODE_BACKGROUND_BEGIN
        var token = cancellation.Token;
        var sliceStart = Stopwatch.GetTimestamp();
        DateTimeOffset? due = null;
        var completed = false;
        try
        {
            Checkpoint(token);
            if (mayRunFull) due = ReadLastCompletedPass()?.Add(FullPassInterval);
            var runFull = mayRunFull && (due is null || due <= _clock.GetUtcNow());
            if (runFull || pending.Length != 0) sweep(runFull, pending, Checkpoint, token);
            Checkpoint(token);
            if (runFull)
            {
                var now = _clock.GetUtcNow();
                WriteCompletion(now);
                due = now.Add(FullPassInterval);
            }
            completed = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   System.Text.Json.JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            logger.Warn($"Launcher storage maintenance did not finish: {ex.Message}");
        }
        finally
        {
            if (loweredIo) _ = SetThreadPriority(GetCurrentThread(), 0x00020000); // THREAD_MODE_BACKGROUND_END
            lock (_gate)
            {
                if (completed && due is not null) _nextFullPass = due;
                if (!completed)
                {
                    _pendingPacks.UnionWith(pending);
                    _idleSince = null;
                }
                if (ReferenceEquals(_current, cancellation)) _current = null;
            }
            cancellation.Dispose();
        }

        void Checkpoint(CancellationToken caller)
        {
            caller.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            if (foregroundIsActive()) throw new OperationCanceledException(token);
            if (Stopwatch.GetElapsedTime(sliceStart) < Slice) return;
            token.WaitHandle.WaitOne(YieldBetweenSlices);
            token.ThrowIfCancellationRequested();
            if (foregroundIsActive()) throw new OperationCanceledException(token);
            sliceStart = Stopwatch.GetTimestamp();
        }
    }

    private DateTimeOffset? ReadLastCompletedPass()
    {
        var path = Path.Combine(paths.Launcher, CompletionFileName);
        if (!MaintenancePath.TryRead(paths, path, out var attributes) || attributes.HasFlag(FileAttributes.Directory) ||
            new FileInfo(path).Length > 64) return null;
        var text = File.ReadAllText(path);
        return DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            && value <= _clock.GetUtcNow() ? value : null;
    }

    private void WriteCompletion(DateTimeOffset timestamp)
    {
        if (!MaintenancePath.TryRead(paths, paths.Launcher, out var attributes) || !attributes.HasFlag(FileAttributes.Directory)) return;
        var path = Path.Combine(paths.Launcher, CompletionFileName);
        if (File.Exists(path) && !MaintenancePath.TryRead(paths, path, out _)) return;
        AtomicFile.WriteAllText(path, timestamp.ToString("O", CultureInfo.InvariantCulture));
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(nint thread, int priority);
}
