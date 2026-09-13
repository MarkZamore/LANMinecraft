

namespace Minecraft.Tests;

/// <summary>
/// Whether a report is enough to find the cause.
///
/// In one evening three sets of logs arrived and none of them answered the
/// question. A game that failed to load said "1 errors found" and put the name
/// of the failing mod on a screen nobody could photograph; a game that printed
/// its complaint before log4j existed left an exit code and nothing else; and a
/// long session pushed its own launch out of the window the report keeps. Each
/// of those is a file that now travels.
/// </summary>
public sealed class BugReportCompletenessTests : IDisposable
{
    private const ulong SenderSteamId = 76561198256236531;
    private const ulong ReceiverSteamId = 76561198050776152;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-report-completeness-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            TempTree.Delete(_root);
        }
        catch
        {
        }
    }

    /// <summary>
    /// The console the game wrote to before it had a log of its own, the debug
    /// log when the pack keeps one, and the file the Java runtime leaves when it
    /// dies at the native level.
    /// </summary>
    [Fact]
    public async Task WhatTheGameSaidOutsideItsOwnLog_Travels()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var network = new InMemoryPeerNetwork();
        network.MakeFriends(SenderSteamId, ReceiverSteamId);
        var sender = CreateSender(network, out _, out var instance);
        var (receiver, router) = CreateReceiver(network);
        await using var routerScope = router;
        await router.StartAsync(timeout.Token);

        await File.WriteAllTextAsync(
            Path.Combine(instance, "logs", "latest.log"), "[12:00:00] LATEST\n", timeout.Token);
        await File.WriteAllTextAsync(
            Path.Combine(instance, "logs", "launcher-console.log"),
            "Error occurred during initialization of VM CONSOLE_MARKER\n",
            timeout.Token);
        await File.WriteAllTextAsync(
            Path.Combine(instance, "logs", "debug.log"), "DEBUG_MARKER\n", timeout.Token);
        await File.WriteAllTextAsync(
            Path.Combine(instance, "hs_err_pid4242.log"), "SIGSEGV HS_ERR_MARKER\n", timeout.Token);

        var report = await SendAndReceive(sender, receiver, timeout.Token);

        Assert.Contains("CONSOLE_MARKER",
            await File.ReadAllTextAsync(Path.Combine(report, "game", "launcher-console.log"), timeout.Token),
            StringComparison.Ordinal);
        Assert.Contains("DEBUG_MARKER",
            await File.ReadAllTextAsync(Path.Combine(report, "game", "debug.log"), timeout.Token),
            StringComparison.Ordinal);
        Assert.Contains("HS_ERR_MARKER",
            await File.ReadAllTextAsync(Path.Combine(report, "jvm", "hs_err_pid4242.log"), timeout.Token),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A guest who fell out of a world is explained by what e4steam said about
    /// its sessions and by what Steam said about its servers. The first sits at
    /// DEBUG in a log whose kept tail is usually something else, the second on
    /// the player's own machine only; both now travel.
    /// </summary>
    [Fact]
    public async Task WhatE4steamAndSteamSaidAboutConnections_Travels()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var network = new InMemoryPeerNetwork();
        network.MakeFriends(SenderSteamId, ReceiverSteamId);
        var steamLogs = Path.Combine(_root, "steam-logs");
        Directory.CreateDirectory(steamLogs);
        var sender = CreateSender(network, out _, out var instance, () => steamLogs);
        var (receiver, router) = CreateReceiver(network);
        await using var routerScope = router;
        await router.StartAsync(timeout.Token);

        var logs = Path.Combine(instance, "logs");
        using (var writer = File.CreateText(Path.Combine(logs, "debug.log")))
        {
            writer.WriteLine("[13Sep2026 20:00:00.000] [e4steam-steam-runtime/DEBUG] [e4steam/]: E4STEAM_EARLY_MARKER");
            var noise = "[13Sep2026 20:00:01.000] [Render thread/DEBUG] [net.minecraft.client/]: " + new string('n', 200);
            for (var index = 0; index < 20_000; index++) writer.WriteLine(noise);
            writer.WriteLine("[13Sep2026 21:00:00.000] [e4steam-steam-runtime/WARN] [e4steam/]: E4STEAM_LATE_MARKER");
        }
        await File.WriteAllTextAsync(
            Path.Combine(logs, LogCleanupService.E4steamPreviousLogName),
            "[12Sep2026 23:00:00.000] [e4steam-steam-runtime/DEBUG] [e4steam/]: E4STEAM_PREVIOUS_MARKER\n",
            timeout.Token);
        var now = DateTime.Now;
        await File.WriteAllLinesAsync(Path.Combine(steamLogs, SteamClientLogs.ConnectionLogName),
        [
            $"[{now.AddDays(-3):yyyy-MM-dd HH:mm:ss}] [Logged Off] OLD_STEAM_MARKER",
            $"[{now.AddHours(-5):yyyy-MM-dd HH:mm:ss}] [U:1:111111111] Log on with 'Using JWT 1234567890123456'",
            $"[{now.AddHours(-1):yyyy-MM-dd HH:mm:ss}] [U:1:295970803] [Logged Off] ConnectionDisconnected RECENT_STEAM_MARKER",
        ], timeout.Token);

        var report = await SendAndReceive(sender, receiver, timeout.Token);

        var e4steam = await File.ReadAllTextAsync(Path.Combine(report, "game", "e4steam.log"), timeout.Token);
        Assert.Contains("E4STEAM_EARLY_MARKER", e4steam, StringComparison.Ordinal);
        Assert.Contains("E4STEAM_LATE_MARKER", e4steam, StringComparison.Ordinal);
        Assert.Contains("E4STEAM_PREVIOUS_MARKER", e4steam, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('n', 200), e4steam, StringComparison.Ordinal);
        var steam = await File.ReadAllTextAsync(Path.Combine(report, "steam", "connection_log.txt"), timeout.Token);
        Assert.Contains("RECENT_STEAM_MARKER", steam, StringComparison.Ordinal);
        Assert.DoesNotContain("OLD_STEAM_MARKER", steam, StringComparison.Ordinal);
        // The sender's own account stays; someone else who used this Steam that
        // day, and the ids of sign-in tokens, do not travel.
        Assert.Contains("[U:1:295970803]", steam, StringComparison.Ordinal);
        Assert.DoesNotContain("111111111", steam, StringComparison.Ordinal);
        Assert.DoesNotContain("1234567890123456", steam, StringComparison.Ordinal);
    }

    /// <summary>
    /// A log too long to send whole keeps both ends. The launch is at the top of
    /// it and the failure is usually at the bottom; keeping only the bottom is
    /// how an evening of play arrived with the startup missing.
    /// </summary>
    [Fact]
    public async Task ALogTooLongToSend_KeepsItsBeginningAndItsEnd()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var network = new InMemoryPeerNetwork();
        network.MakeFriends(SenderSteamId, ReceiverSteamId);
        var sender = CreateSender(network, out _, out var instance);
        var (receiver, router) = CreateReceiver(network);
        await using var routerScope = router;
        await router.StartAsync(timeout.Token);

        // Bigger than the ten megabytes a live log is trimmed to.
        var filler = string.Concat(Enumerable.Repeat("[12:00:00] [main/INFO]: filler line\n", 400_000));
        await File.WriteAllTextAsync(
            Path.Combine(instance, "logs", "latest.log"),
            "[00:00:01] [main/INFO]: THE_LAUNCH_MARKER\n" + filler + "[23:59:59] [main/INFO]: THE_FAILURE_MARKER\n",
            timeout.Token);

        var report = await SendAndReceive(sender, receiver, timeout.Token);
        var text = await File.ReadAllTextAsync(Path.Combine(report, "game", "latest.log"), timeout.Token);

        Assert.Contains("THE_LAUNCH_MARKER", text, StringComparison.Ordinal);
        Assert.Contains("THE_FAILURE_MARKER", text, StringComparison.Ordinal);
        // And it says so, rather than leaving a reader to think the middle
        // simply never happened.
        Assert.Contains("bytes of this log are not in this report", text, StringComparison.Ordinal);
    }

    private static async Task<string> SendAndReceive(
        BugReportService sender, BugReportService receiver, CancellationToken token)
    {
        Assert.True(SteamId64.TryFrom(ReceiverSteamId, out var recipient));
        await sender.SendAsync(recipient, "не запускается", progress: null, token);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(receiver.ReportsDirectory))
            {
                var reports = Directory.GetDirectories(receiver.ReportsDirectory);
                if (reports.Length > 0 && File.Exists(Path.Combine(reports[0], "README.md")))
                {
                    return reports[0];
                }
            }

            await Task.Delay(25, token);
        }
    }

    private BugReportService CreateSender(
        InMemoryPeerNetwork network, out AppPaths paths, out string instanceDirectory,
        Func<string?>? steamLogsDirectory = null)
    {
        paths = CreatePaths("sender");
        var transport = network.CreateTransport(SenderSteamId, "Sender");
        var instance = paths.CombineUnderInstances("Some Build");
        Directory.CreateDirectory(Path.Combine(instance, "logs"));
        Directory.CreateDirectory(Path.Combine(instance, "crash-reports"));
        instanceDirectory = instance;
        var directory = instance;
        Assert.True(SteamId64.TryFrom(SenderSteamId, out var steamId));
        return new BugReportService(
            paths,
            new Logger(paths.LogFile),
            transport,
            () => directory,
            () => new BugReportContext(
                steamId, "MarkZamore", "MarkZamore", Guid.NewGuid().ToString("D"),
                "release 170", "Build A", new string('a', 64), IsMinecraftRunning: false),
            // The machine running the tests may have a Steam of its own.
            steamLogsDirectoryProvider: steamLogsDirectory ?? (() => null));
    }

    private (BugReportService Service, PeerConnectionRouter Router) CreateReceiver(
        InMemoryPeerNetwork network)
    {
        var paths = CreatePaths("receiver");
        var transport = network.CreateTransport(ReceiverSteamId, "Receiver");
        Assert.True(SteamId64.TryFrom(ReceiverSteamId, out var steamId));
        var service = new BugReportService(
            paths,
            new Logger(paths.LogFile),
            transport,
            () => null,
            () => new BugReportContext(
                steamId, "anuvenn", "anuvenn", Guid.NewGuid().ToString("D"),
                "release 170", "Build A", new string('b', 64), IsMinecraftRunning: false));
        var router = new PeerConnectionRouter(transport);
        router.Register(service);
        return (service, router);
    }

    private AppPaths CreatePaths(string who)
    {
        var paths = new AppPaths(Path.Combine(_root, who));
        paths.Ensure();
        return paths;
    }
}
