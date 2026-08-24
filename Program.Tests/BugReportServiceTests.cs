using System.IO.Compression;

namespace Minecraft.Tests;

/// <summary>
/// A bug report is what replaced the live log stream: a player who was thrown
/// out of the game presses one button, and the last of their logs travels to a
/// friend as one archive that can still be read a week later.
///
/// These run two launchers over the in-memory peer network, the same way the
/// world transfer is tested.
/// </summary>
public sealed class BugReportServiceTests : IDisposable
{
    private const ulong SenderSteamId = 76561198256236531;
    private const ulong ReceiverSteamId = 76561198050776152;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-bug-report-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task AReportArrivesWholeWithTheMessageAndTheLogs()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var network = new InMemoryPeerNetwork();
        network.MakeFriends(SenderSteamId, ReceiverSteamId);
        var sender = CreateSender(network, out var senderPaths, out var instance);
        var (receiver, receiverPaths, router) = CreateReceiver(network);
        await using var routerScope = router;
        await router.StartAsync(timeout.Token);

        await File.WriteAllTextAsync(
            Path.Combine(instance, "logs", "latest.log"),
            "[12:00:00] [main/INFO]: LATEST_MARKER\n",
            timeout.Token);
        await File.WriteAllTextAsync(
            senderPaths.LogFile, "launcher LAUNCHER_MARKER\n", timeout.Token);
        await File.WriteAllTextAsync(
            Path.Combine(instance, "crash-reports", "crash-2026-08-16.txt"),
            "CRASH_MARKER\n",
            timeout.Token);

        Assert.True(SteamId64.TryFrom(ReceiverSteamId, out var recipient));
        var manifest = await sender.SendAsync(recipient, "Меня выкинуло из игры", progress: null, timeout.Token);

        Assert.Equal(SenderSteamId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            manifest.SenderSteamId64);
        Assert.True(manifest.ArchiveBytes > 0);

        var report = await WaitForReportAsync(receiver, timeout.Token);
        Assert.Contains("Меня выкинуло из игры", File.ReadAllText(Path.Combine(report, "README.md")),
            StringComparison.Ordinal);
        Assert.Contains("Меня выкинуло из игры", File.ReadAllText(Path.Combine(report, "report.txt")),
            StringComparison.Ordinal);
        Assert.Contains("LATEST_MARKER",
            File.ReadAllText(Path.Combine(report, "game", "latest.log")), StringComparison.Ordinal);
        Assert.Contains("LAUNCHER_MARKER",
            File.ReadAllText(Path.Combine(report, "launcher", "logs.log")), StringComparison.Ordinal);
        Assert.Contains("CRASH_MARKER",
            File.ReadAllText(Path.Combine(report, "crash-reports", "crash-2026-08-16.txt")),
            StringComparison.Ordinal);
        // The environment turns "it crashed" into something actionable.
        Assert.Contains("release 38",
            File.ReadAllText(Path.Combine(report, "environment.json")), StringComparison.Ordinal);
        // The archive itself is not kept once it is unpacked.
        Assert.False(File.Exists(Path.Combine(report, "report.zip")));
        _ = receiverPaths;
    }

    /// <summary>A report with nothing typed is still worth sending.</summary>
    [Fact]
    public async Task AReportWithoutAMessage_IsStillAccepted()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var network = new InMemoryPeerNetwork();
        network.MakeFriends(SenderSteamId, ReceiverSteamId);
        var sender = CreateSender(network, out _, out var instance);
        var (receiver, _, router) = CreateReceiver(network);
        await using var routerScope = router;
        await router.StartAsync(timeout.Token);
        await File.WriteAllTextAsync(Path.Combine(instance, "logs", "latest.log"), "session\n", timeout.Token);

        Assert.True(SteamId64.TryFrom(ReceiverSteamId, out var recipient));
        await sender.SendAsync(recipient, "   ", progress: null, timeout.Token);

        var report = await WaitForReportAsync(receiver, timeout.Token);
        Assert.Contains("(nothing)", File.ReadAllText(Path.Combine(report, "README.md")),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The sanitiser runs before anything leaves the machine: a friend gets the
    /// stack trace, not the player's chat or their user directory.
    /// </summary>
    [Fact]
    public async Task PrivateContent_NeverLeavesTheMachine()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var network = new InMemoryPeerNetwork();
        network.MakeFriends(SenderSteamId, ReceiverSteamId);
        var sender = CreateSender(network, out var senderPaths, out var instance);
        var (receiver, _, router) = CreateReceiver(network);
        await using var routerScope = router;
        await router.StartAsync(timeout.Token);
        await File.WriteAllTextAsync(
            Path.Combine(instance, "logs", "latest.log"),
            "[12:00:00] [Server thread/INFO]: [CHAT] <MarkZamore> SECRET_CHAT_LINE\n" +
            "[12:00:01] [main/ERROR]: java.lang.IllegalStateException: KEEP_THIS_LINE\n",
            timeout.Token);
        await File.WriteAllTextAsync(senderPaths.LogFile, "launcher started\n", timeout.Token);

        Assert.True(SteamId64.TryFrom(ReceiverSteamId, out var recipient));
        await sender.SendAsync(recipient, "crash", progress: null, timeout.Token);

        var report = await WaitForReportAsync(receiver, timeout.Token);
        var game = File.ReadAllText(Path.Combine(report, "game", "latest.log"));
        Assert.Contains("KEEP_THIS_LINE", game, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_CHAT_LINE", game, StringComparison.Ordinal);
    }

    /// <summary>
    /// The archive travels between machines, so an entry that climbs out of the
    /// report directory is refused rather than written.
    /// </summary>
    [Fact]
    public async Task AnArchiveThatEscapesItsDirectory_IsRefused()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var network = new InMemoryPeerNetwork();
        network.MakeFriends(SenderSteamId, ReceiverSteamId);
        var senderTransport = network.CreateTransport(SenderSteamId, "Sender");
        var (receiver, receiverPaths, router) = CreateReceiver(network);
        await using var routerScope = router;
        await router.StartAsync(timeout.Token);

        var archive = Path.Combine(_root, "evil.zip");
        Directory.CreateDirectory(_root);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("../../escaped.txt");
            using var stream = entry.Open();
            stream.Write("owned"u8);
        }

        var bytes = await File.ReadAllBytesAsync(archive, timeout.Token);
        var manifest = new BugReportManifest
        {
            ReportId = Guid.NewGuid().ToString("N"),
            SenderSteamId64 = SenderSteamId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SenderPlayerName = "Sender",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ArchiveBytes = bytes.Length,
            ArchiveSha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()
        };

        Assert.True(SteamId64.TryFrom(ReceiverSteamId, out var recipientId));
        await using var connection = await senderTransport.ConnectAsync(
            recipientId, BugReportManifest.ProtocolName, timeout.Token);
        await PortableProtocol.WriteJsonAsync(
            connection.Stream,
            manifest,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web),
            timeout.Token);
        await connection.Stream.WriteAsync(bytes, timeout.Token);
        await connection.Stream.FlushAsync(timeout.Token);

        // The receiver refuses it and nothing lands outside its own directory.
        await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
        Assert.False(File.Exists(Path.Combine(receiverPaths.Personal, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
        _ = receiver;
    }

    private BugReportService CreateSender(
        InMemoryPeerNetwork network,
        out AppPaths paths,
        out string instanceDirectory)
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
                "release 38", "Infinity", new string('a', 64), IsMinecraftRunning: false),
            _ => Task.FromResult(new SupportEnvironmentSnapshot(
                DateTimeOffset.UtcNow, "release 38", "38", ".NET 10", "Windows", "X64",
                "Java 25", "Infinity", new string('a', 64), [], [],
                SteamDiagnosticContext.Unavailable, new Dictionary<string, string>(), string.Empty)));
    }

    private (BugReportService Service, AppPaths Paths, PeerConnectionRouter Router) CreateReceiver(
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
                "release 38", "Infinity", new string('b', 64), IsMinecraftRunning: false));
        var router = new PeerConnectionRouter(transport);
        router.Register(service);
        return (service, paths, router);
    }

    private static async Task<string> WaitForReportAsync(BugReportService receiver, CancellationToken token)
    {
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
            await Task.Delay(TimeSpan.FromMilliseconds(50), token);
        }
    }

    private AppPaths CreatePaths(string name)
    {
        var paths = new AppPaths(Path.Combine(_root, name));
        paths.Ensure();
        return paths;
    }
}
