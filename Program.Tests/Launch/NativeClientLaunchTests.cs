using System.IO;
using Minecraft;
using Xunit;

namespace Minecraft.Tests;

public sealed class NativeClientLaunchTests
{
    [Fact]
    public async Task NativeStartupWaitsForTheWindowAndCanEndBeforeItOpens()
    {
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var visible = 0;
        var waiting = NativeClientLaunch.WaitForGameWindowAsync(exited.Task, () => Volatile.Read(ref visible) != 0, CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        Volatile.Write(ref visible, 1);
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));

        // A clean exit is still a failed startup if the window never opened.
        exited.SetResult(0);
        Assert.False(await NativeClientLaunch.WaitForGameWindowAsync(exited.Task, () => true, CancellationToken.None));

        using var cancellation = new CancellationTokenSource();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = NativeClientLaunch.WaitForGameWindowAsync(running.Task, () => false, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void NativeStartupReportsOnlyIncreasingPreparationFractions()
    {
        var progress = new InlineProgress();
        var tracker = new NativeStartupProgress(progress);
        Assert.Null(tracker.Fraction);

        tracker.Observe(null);
        tracker.Observe("Checking 5262 pinned Minecraft 26.3 input files");
        tracker.Observe("""{"message":"Preparing resources"}""");
        tracker.Observe("""{"fraction":""");
        tracker.Observe("""{"fraction":"0.8"}""");
        tracker.Observe("""{"fraction":-0.1}""");
        tracker.Observe("""{"fraction":1.1}""");
        tracker.Observe("""{"fraction":1e500}""");
        tracker.Observe(new string(' ', 256) + """{"fraction":0.8}""");
        tracker.Observe(""" {"fraction":0.000} """);
        tracker.Observe("""{"fraction":0.550}""");
        tracker.Observe("""{"fraction":0.500}""");
        tracker.Observe("""{"fraction":0.550}""");
        tracker.Observe("""{"fraction":1.000}""");

        Assert.Equal(new double?[] { 0, 0.55, null }, progress.Reports.Select(value => value.Fraction));
        Assert.Equal(1d, tracker.Fraction);
        Assert.All(progress.Reports, value => Assert.Equal(RuntimePreparationStage.PreparingNativeGame, value.Stage));
        Assert.Equal(new[]
        {
            NativeStartupProgress.PreparationMessage,
            NativeStartupProgress.PreparationMessage,
            NativeStartupProgress.OpeningMessage
        }, progress.Reports.Select(value => value.Message));
    }

    [Fact]
    public void NativeStartupStopsReportingWhenTheWaitEnds()
    {
        var progress = new InlineProgress();
        var tracker = new NativeStartupProgress(progress);
        tracker.Observe("""{"fraction":0.500}""");
        tracker.Complete();
        tracker.Observe("""{"fraction":1.000}""");

        Assert.Equal(0.5, tracker.Fraction);
        Assert.Equal(0.5, Assert.Single(progress.Reports).Fraction);
    }

    [Fact]
    public async Task NativeStartupTimesOutOnlyAfterResourcesAreReadyAndOpeningIsSilent()
    {
        var clock = new ManualTimeProvider();
        var progress = new InlineProgress();
        var tracker = new NativeStartupProgress(progress, clock);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        clock.Advance(TimeSpan.FromMinutes(12));
        Assert.False(tracker.PollForOpeningTimeout());
        Assert.Equal("Подготовка ресурсов · 12:00", Assert.Single(progress.Reports).Message);
        Assert.False(tracker.PollForOpeningTimeout());
        Assert.Single(progress.Reports);

        tracker.Observe("""{"fraction":1.000}""");
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(tracker.PollForOpeningTimeout());
        tracker.Observe("Horizon: loading the title artwork");
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.False(tracker.PollForOpeningTimeout());
        clock.Advance(TimeSpan.FromSeconds(1));

        // An existing game window wins over a clock boundary.
        Assert.True(await NativeClientLaunch.WaitForGameWindowAsync(exited.Task, () => true,
            CancellationToken.None, tracker));
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => NativeClientLaunch.WaitForGameWindowAsync(
            exited.Task, () => false, CancellationToken.None, tracker));
        var failure = MinecraftLaunchFailure.FromNativeStartupTimeout(timeout, " console", " latest");
        Assert.EndsWith(" console latest", failure.Message);
        Assert.Contains("окно так и не появилось", MinecraftLaunchFailure.ForUser(failure).Detail);
    }

    [Fact]
    public void NativeClientUsesThePortableInstanceAndOnlySteamIdentity()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "LANMinecraft fixture"));
        var executable = Path.Combine(paths.Packs, "Native fixture", "native", "launch.exe");
        var instance = Path.Combine(paths.Instances, "Native fixture");
        var identity = new LocalIdentityContext
        {
            IdentityName = "Player_Test",
            PlayerUuid = Guid.Parse("6b19861d-e126-4a23-a7a6-d946f0eeabdd"),
            SteamId64 = SteamId64.TryFrom(76561198000000000, out var steamId) ? steamId : throw new InvalidOperationException(),
            SessionAccessToken = "java-only-test-token"
        };

        var start = NativeClientLaunch.CreateStartInfo(paths, identity, "Native fixture", instance, executable,
            "nightly-test", paths.SkinRegistryFile, paths.IdentityRegistryFile);

        Assert.Equal(executable, start.FileName);
        Assert.Equal(new[] { "--game-dir", instance }, start.ArgumentList);
        Assert.False(start.UseShellExecute);
        Assert.False(start.RedirectStandardInput);
        Assert.Equal("Player_Test", start.Environment["LANMINECRAFT_PLAYER_NAME"]);
        Assert.Equal(identity.PlayerUuid.ToString("D"), start.Environment["LANMINECRAFT_PLAYER_UUID"]);
        Assert.Equal(identity.SteamId64.ToString(), start.Environment["LANMINECRAFT_STEAM_ID"]);
        Assert.Equal("nightly-test", start.Environment["LANMINECRAFT_NATIVE_VARIANT"]);
        Assert.Equal(paths.SharedRuntime, start.Environment["LANMINECRAFT_SHARED_MINECRAFT"]);
        Assert.DoesNotContain("java-only-test-token", start.Environment.Values);
    }

    private sealed class InlineProgress : IProgress<RuntimePreparationProgress>
    {
        public List<RuntimePreparationProgress> Reports { get; } = [];

        public void Report(RuntimePreparationProgress value) => Reports.Add(value);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
