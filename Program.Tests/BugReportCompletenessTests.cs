

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
        InMemoryPeerNetwork network, out AppPaths paths, out string instanceDirectory)
    {
        paths = CreatePaths("sender");
        var transport = network.CreateTransport(SenderSteamId, "Sender");
        var instance = paths.CombineUnderInstances("Infinity");
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
                "release 170", "LL8 Extended", new string('a', 64), IsMinecraftRunning: false));
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
                "release 170", "LL8 Extended", new string('b', 64), IsMinecraftRunning: false));
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
