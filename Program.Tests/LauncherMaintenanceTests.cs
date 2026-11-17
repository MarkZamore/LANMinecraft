using System.IO;
using System.Text.Json;

namespace Minecraft.Tests;

public sealed class LauncherMaintenanceTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "launcher-maintenance-" + Guid.NewGuid().ToString("N")));

    public LauncherMaintenanceTests() => _paths.Ensure();

    [Fact]
    public async Task FullPassStartsAsSoonAsStartupIsReady()
    {
        var interruptedWrite = Path.Combine(_paths.Launcher, $".{LauncherMaintenance.CompletionFileName}.{Guid.NewGuid():N}.tmp");
        var unrelatedTemporaryFile = Path.Combine(_paths.Launcher, $".unrelated.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(ProgressPath, "2026-01-01T12:00:00.0000000+00:00");
        File.WriteAllText(interruptedWrite, "unfinished checkpoint");
        File.WriteAllText(unrelatedTemporaryFile, "leave this alone");
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        var maintenance = Create(run =>
        {
            Interlocked.Increment(ref invocations);
            run.RunStep("service", () => started.TrySetResult(run.FullPass));
        });
        try
        {
            Assert.False(started.Task.IsCompleted);
            Assert.True(File.Exists(interruptedWrite));

            maintenance.Start();
            Assert.True(await started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await WaitUntilAsync(() => !File.Exists(ProgressPath));
            Assert.False(File.Exists(interruptedWrite));
            Assert.True(File.Exists(unrelatedTemporaryFile));
            maintenance.Start();
            Assert.Equal(1, Volatile.Read(ref invocations));
        }
        finally { maintenance.Stop(); }
    }

    [Fact]
    public async Task GameTransitionsDoNotCancelAnInFlightOperationAndShutdownDoesNotWait()
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        var playing = 0;
        var maintenance = Create(run =>
        {
            started.TrySetResult(run.Token);
            try
            {
                if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                run.Checkpoint(run.Token);
                finished.TrySetResult(false);
            }
            catch (OperationCanceledException)
            {
                finished.TrySetResult(true);
                throw;
            }
        }, gameIsRunning: () => Volatile.Read(ref playing) != 0);
        try
        {
            maintenance.Start();
            var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref playing, 1);
            maintenance.ObserveGameState();
            Assert.False(token.IsCancellationRequested);
            Volatile.Write(ref playing, 0);
            maintenance.ObserveGameState();
            Assert.False(token.IsCancellationRequested);
            Assert.False(finished.Task.IsCompleted);
            maintenance.Stop();
            Assert.True(token.IsCancellationRequested);
            Assert.False(finished.Task.IsCompleted);
            resume.Set();
            Assert.True(await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(File.Exists(ProgressPath));
        }
        finally
        {
            resume.Set();
            maintenance.Stop();
        }
    }

    [Fact]
    public async Task InterruptedPassKeepsOneCursorAndResumesWithoutRepeatingCompletedWork()
    {
        var marked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        var first = Create(run =>
        {
            run.RunStep("service", () => { });
            run.SaveCursor("pack:managed", "mods/partial.jar");
            run.DeferPack("Managed");
            marked.TrySetResult(true);
            try
            {
                resume.Wait(TimeSpan.FromSeconds(10));
                run.Checkpoint(run.Token);
            }
            finally { released.TrySetResult(true); }
        });

        try
        {
            first.Start();
            Assert.True(await marked.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Single(Directory.GetFiles(_paths.Launcher, "*launcher-maintenance*"));
            first.Stop();
            resume.Set();
            Assert.True(await released.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(File.Exists(ProgressPath));
        }
        finally
        {
            resume.Set();
            first.Stop();
        }

        var resumed = new TaskCompletionSource<(string? Cursor, string[] Deferred, bool Repeated)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restarted = Create(run =>
        {
            var repeated = run.RunStep("service", () => { });
            resumed.TrySetResult((run.Cursor("pack:managed"), run.PriorityPacks.ToArray(), repeated));
            run.RunStep("pack:managed", () => { });
            run.CompletePack("Managed");
        });
        try
        {
            restarted.Start();
            var work = await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("mods/partial.jar", work.Cursor);
            Assert.Equal("Managed", Assert.Single(work.Deferred));
            Assert.False(work.Repeated);
            await WaitUntilAsync(() => !File.Exists(ProgressPath));
        }
        finally { restarted.Stop(); }
    }

    [Fact]
    public async Task ACheckpointOpenWithoutDeleteSharingPreservesThePreviousCheckpoint()
    {
        var original = JsonSerializer.Serialize(new LauncherMaintenanceProgress(1, UpdateService.CurrentReleaseNumber,
            ["completed"], [], new Dictionary<string, string> { ["partial"] = "mods/old.jar" }));
        File.WriteAllText(ProgressPath, original);
        using var held = new FileStream(ProgressPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var writeAttempted = new TaskCompletionSource<(string Checkpoint, string[] TemporaryFiles)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenance = Create(run =>
        {
            run.RunStep("another-step", () => { });
            run.DeferStep("partial");
            writeAttempted.TrySetResult((File.ReadAllText(ProgressPath),
                Directory.GetFiles(_paths.Launcher, $".{LauncherMaintenance.CompletionFileName}.*.tmp")));
        });

        try
        {
            maintenance.Start();
            var attempt = await writeAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(original, attempt.Checkpoint);
            Assert.Empty(attempt.TemporaryFiles);
        }
        finally { maintenance.Stop(); }
    }

    [Fact]
    public async Task DeferredWorkSleepsUntilTheGameStateChanges()
    {
        Directory.CreateDirectory(_paths.Launcher);
        File.WriteAllText(ProgressPath, JsonSerializer.Serialize(new LauncherMaintenanceProgress(
            1, UpdateService.CurrentReleaseNumber, ["pack:In game"], [], [])));
        var playing = 1;
        var launches = 0;
        var deferred = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenance = Create(run =>
        {
            Interlocked.Increment(ref launches);
            if (run.GameIsRunning)
            {
                run.RunStep("independent", () => { });
                run.DeferStep("shared");
                run.DeferPack("In game");
                deferred.TrySetResult(run.IsPackInUse("In game") && !run.IsPackInUse("Other pack"));
                return;
            }
            run.RunStep("shared", () => { });
            var didWork = run.RunStep("pack:In game", () => { });
            run.CompletePack("In game");
            completed.TrySetResult(didWork);
        }, gameIsRunning: () => Volatile.Read(ref playing) != 0, isPackInUse: pack => pack == "In game" && Volatile.Read(ref playing) != 0);

        try
        {
            maintenance.RequestPack("In game");
            maintenance.Start();
            Assert.True(await deferred.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            for (var attempt = 0; attempt < 10; attempt++)
            {
                maintenance.ObserveGameState();
                await Task.Delay(15);
            }
            Assert.Equal(1, Volatile.Read(ref launches));
            Assert.True(File.Exists(ProgressPath));

            Volatile.Write(ref playing, 0);
            maintenance.ObserveGameState();
            Assert.True(await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await WaitUntilAsync(() => !File.Exists(ProgressPath));
            Assert.Equal(2, Volatile.Read(ref launches));
        }
        finally { maintenance.Stop(); }
    }

    [Fact]
    public async Task ProgressCountsDistinctCompletedStepsAndFinishesAfterRemovingCheckpoint()
    {
        var reports = new List<int>();
        var finished = new TaskCompletionSource<(int[] Values, bool CheckpointExists)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenance = Create(run =>
        {
            run.PlanSteps(["service", "SERVICE", "pack:Example"]);
            run.RunStep("unrelated", () => { });
            run.RunStep("service", () => { });
            run.RunStep("SERVICE", () => { });
            run.SaveCursor("pack:Example", "a.jar");
            run.RunStep("pack:Example", () => { });
        }, reportProgress: value =>
        {
            reports.Add(value);
            if (value == 100) finished.TrySetResult((reports.ToArray(), File.Exists(ProgressPath)));
        });

        try
        {
            maintenance.Start();
            var status = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal([0, 50, 99, 100], status.Values);
            Assert.False(status.CheckpointExists);
        }
        finally { maintenance.Stop(); }
    }

    [Fact]
    public async Task ProgressResumesFromSavedStepsAndWaitsForTheDeferredPack()
    {
        File.WriteAllText(ProgressPath, JsonSerializer.Serialize(new LauncherMaintenanceProgress(1,
            UpdateService.CurrentReleaseNumber, ["service"], ["Busy"], [])));
        var playing = 1;
        var reports = new List<int>();
        var deferred = new TaskCompletionSource<int[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<int[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenance = Create(run =>
        {
            run.PlanSteps(["service", "pack:Busy", "pack:Other"]);
            run.RunStep("service", () => { });
            run.RunStep("pack:Other", () => { });
            if (run.GameIsRunning)
            {
                run.DeferPack("Busy");
                deferred.TrySetResult(reports.ToArray());
                return;
            }
            run.RunStep("pack:Busy", () => { });
            run.CompletePack("Busy");
        }, gameIsRunning: () => Volatile.Read(ref playing) != 0, reportProgress: value =>
        {
            reports.Add(value);
            if (value == 100) finished.TrySetResult(reports.ToArray());
        });

        try
        {
            maintenance.Start();
            var partialReports = await deferred.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal([0, 33, 66], partialReports);
            Assert.True(File.Exists(ProgressPath));
            Assert.False(finished.Task.IsCompleted);

            Volatile.Write(ref playing, 0);
            maintenance.ObserveGameState();
            var finalReports = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal([0, 33, 66, 99, 100], finalReports);
            Assert.False(File.Exists(ProgressPath));
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

    private string ProgressPath => Path.Combine(_paths.Launcher, LauncherMaintenance.CompletionFileName);

    private LauncherMaintenance Create(Action<LauncherMaintenanceRun> sweep, Func<bool>? gameIsRunning = null,
        Func<string, bool>? isPackInUse = null, Action<int>? reportProgress = null) =>
        new(_paths, new Logger(_paths.LogFile), sweep, gameIsRunning, isPackInUse, reportProgress);

    private static async Task WaitUntilAsync(Func<bool> test)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!test()) await Task.Delay(20, timeout.Token);
    }

    public void Dispose() => TempTree.Delete(_paths.Root);
}
