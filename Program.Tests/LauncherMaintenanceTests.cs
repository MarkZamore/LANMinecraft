using System.IO;

namespace Minecraft.Tests;

public sealed class LauncherMaintenanceTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "launcher-maintenance-" + Guid.NewGuid().ToString("N")));
    private readonly TestClock _clock = new();

    public LauncherMaintenanceTests() => _paths.Ensure();

    [Fact]
    public async Task FullPassWaitsForStartupAndContinuousIdle()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenance = Create((full, _, _, _) => started.TrySetResult(full));
        try
        {
            maintenance.ObserveIdle(false);
            _clock.Advance(TimeSpan.FromHours(1));
            maintenance.ObserveIdle(true);
            _clock.Advance(LauncherMaintenance.QuietPeriod - TimeSpan.FromSeconds(1));
            maintenance.ObserveIdle(true);
            Assert.False(started.Task.IsCompleted);
            Assert.False(File.Exists(CompletionPath));

            maintenance.Interrupt();
            maintenance.ObserveIdle(true);
            _clock.Advance(LauncherMaintenance.QuietPeriod);
            maintenance.ObserveIdle(true);
            Assert.True(await started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await WaitUntilAsync(() => File.Exists(CompletionPath));
        }
        finally { maintenance.Stop(); }
    }

    [Fact]
    public async Task ForegroundAndShutdownDoNotWaitForAnInFlightFilesystemOperation()
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        var maintenance = Create((_, _, checkpoint, token) =>
        {
            started.TrySetResult(token);
            try
            {
                if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                checkpoint(token);
                finished.TrySetResult(false);
            }
            catch (OperationCanceledException)
            {
                finished.TrySetResult(true);
                throw;
            }
        });
        try
        {
            StartIdle(maintenance);
            var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            maintenance.Interrupt(); // The Play handler requests cancellation before using the packs.
            Assert.True(token.IsCancellationRequested);
            Assert.False(finished.Task.IsCompleted);
            maintenance.Stop(); // Closing does not join the worker either.
            Assert.False(finished.Task.IsCompleted);
            resume.Set();
            Assert.True(await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(File.Exists(CompletionPath));
        }
        finally
        {
            resume.Set();
            maintenance.Stop();
        }
    }

    [Fact]
    public async Task ARecordedFullPassAllowsOnlyQueuedPacksUntilTheNextDay()
    {
        var timestamp = _clock.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(CompletionPath, timestamp);
        var ran = new TaskCompletionSource<(bool Full, string[] Packs)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenance = Create((full, pending, _, _) => ran.TrySetResult((full, pending.ToArray())));
        try
        {
            maintenance.RequestPack("Updated pack");
            maintenance.RequestPack("updated pack");
            StartIdle(maintenance);
            var work = await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(work.Full);
            Assert.Equal("Updated pack", Assert.Single(work.Packs));
            Assert.Equal(timestamp, File.ReadAllText(CompletionPath));
        }
        finally { maintenance.Stop(); }
    }

    [Fact]
    public void AutomaticDeletionLeavesProtectedAssetsAndDoesNotFollowJunctions()
    {
        var outside = Path.Combine(_paths.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside");
        var linked = Path.Combine(_paths.Personal, "link");
        SavesFolderService.CreateJunction(linked, outside);

        var assets = Path.Combine(_paths.Personal, "Nested", "SHADERPACKS");
        Directory.CreateDirectory(assets);
        var protectedFile = Path.Combine(assets, "mine.zip");
        File.WriteAllText(protectedFile, "mine");
        var unneeded = Path.Combine(_paths.Personal, "unknown.txt");
        File.WriteAllText(unneeded, "remove");
        var empty = Path.Combine(_paths.Personal, "empty");
        Directory.CreateDirectory(empty);

        Assert.False(MaintenancePath.TryDelete(_paths, Path.Combine(linked, "keep.txt"), directory: false));
        Assert.False(MaintenancePath.TryDelete(_paths, linked, directory: true));
        Assert.False(MaintenancePath.TryDelete(_paths, protectedFile, directory: false));
        Assert.False(MaintenancePath.TryDelete(_paths, Path.Combine(_paths.Personal, "Nested"), directory: true));
        Assert.False(MaintenancePath.TryDelete(_paths, unneeded, directory: false, stillAllowed: () => false));
        Assert.True(File.Exists(unneeded));
        Assert.True(MaintenancePath.TryDelete(_paths, unneeded, directory: false));
        Assert.True(MaintenancePath.TryDelete(_paths, empty, directory: true));
        Assert.True(File.Exists(protectedFile));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        Assert.False(File.Exists(unneeded));
        Assert.False(Directory.Exists(empty));
    }

    private string CompletionPath => Path.Combine(_paths.Launcher, LauncherMaintenance.CompletionFileName);

    private LauncherMaintenance Create(Action<bool, IReadOnlyList<string>, Action<CancellationToken>, CancellationToken> sweep) =>
        new(_paths, new Logger(_paths.LogFile), () => false, sweep, _clock);

    private void StartIdle(LauncherMaintenance maintenance)
    {
        maintenance.ObserveIdle(true);
        _clock.Advance(LauncherMaintenance.QuietPeriod);
        maintenance.ObserveIdle(true);
    }

    private static async Task WaitUntilAsync(Func<bool> test)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!test()) await Task.Delay(20, timeout.Token);
    }

    public void Dispose() => TempTree.Delete(_paths.Root);

    private sealed class TestClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public override long GetTimestamp() => Volatile.Read(ref _ticks);
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }
}
