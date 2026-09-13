using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Minecraft 26.2 on NeoForge finishes shutting down and then cannot end: its
/// watchdog either dies or never starts, and the JVM stays for a pool thread
/// nobody stops. The launcher ends such a process, and only such a process, so
/// what it recognises has to be exactly the loader saying main is over, as the
/// last thing logged, with no window left - and it has to wait as long as the
/// game itself would have.
/// </summary>
public sealed class ClientShutdownHangTests
{
    private const string LoaderClosed = "[20:35:24] [Render thread/INFO]: Closing FML Loader 4d770bcd";
    private const string ModLoaderCleared = "[20:35:24] [Render thread/INFO]: Clearing ModLoader";

    [Theory]
    [InlineData(LoaderClosed)]
    [InlineData("[20:35:24] [Render thread/INFO] [net.neoforged.fml.loading.FMLLoader]: Closing FML Loader 4d770bcd")]
    public void TheLoaderClosing_IsRecognised(string line)
    {
        // Mojang's log configuration and the launcher's, which names the logger.
        Assert.True(ClientShutdownHang.IsMainFinished(line));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[20:35:23] [Render thread/INFO]: Stopping!")]
    [InlineData(ModLoaderCleared)]
    [InlineData("Exception in thread \"Client shutdown watchdog #1\" java.lang.NoClassDefFoundError: net/minecraft/server/dedicated/ServerWatchdog")]
    [InlineData("Closing FML Loader 4d770bcd")]
    [InlineData("[20:40:02] [Render thread/INFO]: [CHAT] <Bob> [20:35:24] [Render thread/INFO]: Closing FML Loader 4d770bcd")]
    [InlineData("[20:40:02] [Server thread/INFO]: <Bob> [20:35:24] [Render thread/INFO]: Closing FML Loader 4d770bcd")]
    [InlineData("[20:40:02] [Render thread/INFO]: [Not Secure] [CHAT] <Bob> ]: Closing FML Loader 4d770bcd")]
    public void AnythingElse_IsNot(string? line)
    {
        // Stopping is said before the world is saved; the watchdog's death does
        // not happen at all when the window was closed with its cross; and the
        // same words pasted into chat - which the game writes into the same
        // file - must never end anybody's game.
        Assert.False(ClientShutdownHang.IsMainFinished(line));
    }

    [Fact]
    public void ALogEndingWithTheLoaderClosing_ShowsMainFinished()
    {
        var path = WriteLog("[20:35:23] [Render thread/INFO]: Stopping!", LoaderClosed, ModLoaderCleared,
            "Exception in thread \"Client shutdown watchdog #1\" java.lang.NoClassDefFoundError: x",
            "\tat java.base/java.lang.Thread.run(Thread.java:1474)");
        try
        {
            Assert.True(ClientShutdownHang.LatestLogShowsMainFinished(path, DateTime.UtcNow.AddMinutes(-5)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheLine_FollowedByMoreOfTheGame_IsNotTheEnd()
    {
        // A session that kept writing below an earlier session's last words.
        var path = WriteLog(LoaderClosed, ModLoaderCleared, "[21:02:11] [main/INFO]: Loading Minecraft 26.2");
        try
        {
            Assert.False(ClientShutdownHang.LatestLogShowsMainFinished(path, DateTime.UtcNow.AddMinutes(-5)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AChatLineWithTheWords_EndsNothing()
    {
        var path = WriteLog("[20:40:02] [Render thread/INFO]: [CHAT] <Bob> [20:35:24] [Render thread/INFO]: Closing FML Loader 4d770bcd");
        try
        {
            Assert.False(ClientShutdownHang.LatestLogShowsMainFinished(path, DateTime.UtcNow.AddMinutes(-5)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ALogLastWrittenBeforeTheProcessStarted_IsTheLastGamesLog()
    {
        // The seconds before log4j rotates latest.log: the file still ends with
        // the previous game's shutdown, and must not end the one just starting.
        var path = WriteLog(LoaderClosed, ModLoaderCleared);
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-2));
            Assert.False(ClientShutdownHang.LatestLogShowsMainFinished(path, DateTime.UtcNow.AddHours(-1)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheLine_AtTheEndOfALongLog_IsFound()
    {
        var filler = Enumerable.Repeat("[20:30:00] [Render thread/INFO]: " + new string('x', 200), 2000).ToArray();
        var path = WriteLog([.. filler, LoaderClosed]);
        try
        {
            Assert.True(new FileInfo(path).Length > ClientShutdownHang.TailBytes);
            Assert.True(ClientShutdownHang.LatestLogShowsMainFinished(path, DateTime.UtcNow.AddMinutes(-5)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NoLogAtAll_ShowsNothing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"no-latest-{Guid.NewGuid():N}.log");
        Assert.False(ClientShutdownHang.LatestLogShowsMainFinished(path, DateTime.UtcNow.AddMinutes(-5)));
    }

    [Fact]
    public async Task AGameStillThere_WithNoWindow_TheGamesOwnWaitAfterMainFinished_IsEnded()
    {
        var clock = new FakeClock();
        var ended = 0;
        var result = await ClientShutdownHang.EndWhenStuckAsync(
            mainFinished: () => { clock.Advance(TimeSpan.FromSeconds(5)); return true; },
            hasExited: () => false,
            hasWindow: () => false,
            end: () => ended++,
            utcNow: clock.Now,
            pollInterval: TimeSpan.Zero,
            token: CancellationToken.None);

        Assert.True(result);
        Assert.Equal(1, ended);
        Assert.True(clock.Elapsed >= ClientShutdownHang.EndAfter);
    }

    [Fact]
    public async Task AProcessWithAWindow_IsNeverEnded()
    {
        // A game still being played, or the dialog a fatal error opens after
        // the same line: someone is looking at it.
        var clock = new FakeClock();
        var polls = 0;
        var ended = 0;
        var result = await ClientShutdownHang.EndWhenStuckAsync(
            mainFinished: () => { clock.Advance(TimeSpan.FromSeconds(5)); return true; },
            hasExited: () => ++polls > 40,
            hasWindow: () => true,
            end: () => ended++,
            utcNow: clock.Now,
            pollInterval: TimeSpan.Zero,
            token: CancellationToken.None);

        Assert.False(result);
        Assert.Equal(0, ended);
    }

    [Fact]
    public async Task AGameThatEndsByItself_IsNeverEnded()
    {
        var clock = new FakeClock();
        var calls = 0;
        var ended = 0;
        var result = await ClientShutdownHang.EndWhenStuckAsync(
            mainFinished: () => { clock.Advance(TimeSpan.FromSeconds(5)); return true; },
            hasExited: () => ++calls > 3,
            hasWindow: () => false,
            end: () => ended++,
            utcNow: clock.Now,
            pollInterval: TimeSpan.Zero,
            token: CancellationToken.None);

        Assert.False(result);
        Assert.Equal(0, ended);
    }

    [Fact]
    public async Task TheWait_StartsOver_WhenTheLineIsGoneAgain()
    {
        // Seen for ten seconds, gone once, then seen again: twenty seconds have
        // passed in all, but not twenty since it was last seen to come back.
        var clock = new FakeClock();
        var answers = new Queue<bool>([true, true, false, true, true, true, true, true]);
        var ended = 0;
        await ClientShutdownHang.EndWhenStuckAsync(
            mainFinished: () => { clock.Advance(TimeSpan.FromSeconds(5)); return answers.Dequeue(); },
            hasExited: () => false,
            hasWindow: () => false,
            end: () => ended++,
            utcNow: clock.Now,
            pollInterval: TimeSpan.Zero,
            token: CancellationToken.None);

        Assert.Equal(1, ended);
        Assert.Empty(answers);
    }

    [Fact]
    public async Task Cancelling_EndsNothing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var ended = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClientShutdownHang.EndWhenStuckAsync(
            mainFinished: () => true,
            hasExited: () => false,
            hasWindow: () => false,
            end: () => ended++,
            utcNow: () => DateTime.UtcNow,
            pollInterval: TimeSpan.FromSeconds(1),
            token: cancellation.Token));
        Assert.Equal(0, ended);
    }

    private sealed class FakeClock
    {
        private static readonly DateTime Start = new(2026, 9, 13, 20, 35, 24, DateTimeKind.Utc);
        private DateTime _now = Start;
        public DateTime Now() => _now;
        public void Advance(TimeSpan by) => _now += by;
        public TimeSpan Elapsed => _now - Start;
    }

    private static string WriteLog(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"latest-{Guid.NewGuid():N}.log");
        File.WriteAllLines(path, lines);
        return path;
    }
}
