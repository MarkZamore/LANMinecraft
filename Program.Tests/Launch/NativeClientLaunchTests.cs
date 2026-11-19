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
}
