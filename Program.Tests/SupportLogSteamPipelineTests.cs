using System.Globalization;
using System.Text;

namespace Minecraft.Tests;

/// <summary>
/// The whole diagnostics path between two launchers over the peer transport:
/// one side collects a growing game log, the other stores it.
///
/// The case that matters is volume. The collector emits one item per log line
/// and a game writes hundreds of lines a second; when every line became its own
/// frame the receiver's cadence guard cut the connection, the sender reconnected
/// and was cut again, and each game run reached the bundle only a few hundred
/// lines deep - exactly what the collected bundles showed.
/// </summary>
public sealed class SupportLogSteamPipelineTests : IDisposable
{
    private const ulong SenderSteamId = 76561198000000001;
    private const ulong ReceiverSteamId = 76561198000000002;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-support-steam-{Guid.NewGuid():N}");

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
    public async Task ABusyGameLog_ArrivesWholeAndSurvivesARestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var network = new InMemoryPeerNetwork();
        var senderTransport = network.CreateTransport(SenderSteamId, "Sender");
        var receiverTransport = network.CreateTransport(ReceiverSteamId, "Receiver");
        network.MakeFriends(SenderSteamId, ReceiverSteamId);

        var senderPaths = CreatePaths("sender");
        var receiverPaths = CreatePaths("receiver");
        var instance = senderPaths.CombineUnderInstances("DiagnosticsPack");
        var logs = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logs);
        var latest = Path.Combine(logs, "latest.log");

        // Several times the per-frame budget and far past the old cadence
        // guard, which is where the stream used to stop.
        const int lineCount = 4000;
        await File.WriteAllTextAsync(latest, BuildRun("RUN1", lineCount), timeout.Token);

        await using var receiver = CreateService(receiverPaths, receiverTransport, ReceiverSteamId, () => null);
        await using var receiverRouter = new PeerConnectionRouter(receiverTransport);
        receiverRouter.Register(receiver);
        await receiverRouter.StartAsync(timeout.Token);

        await using var sender = CreateService(senderPaths, senderTransport, SenderSteamId, () => instance);
        Assert.True(SteamId64.TryFrom(ReceiverSteamId, out var receiverId));
        sender.ObservePeer(new SteamPeerPresence
        {
            SteamId = receiverId,
            PersonaName = "Receiver",
            ProtocolVersion = SteamPresenceCodec.ProtocolVersion,
            PlayerName = "Receiver",
            DiagnosticProtocolVersion = PeerSupportProtocol.ProtocolVersion
        });

        await sender.SetTargetAsync(new DiagnosticLogTargetOption(receiverId, "Receiver"), timeout.Token);
        await WaitForLineAsync(receiverPaths, $"RUN1-line-{lineCount - 1}", sender, timeout.Token);

        // The player restarts Minecraft mid-session: latest.log is replaced and
        // the new run has to keep streaming into the same bundle.
        File.Delete(latest);
        await File.WriteAllTextAsync(latest, BuildRun("RUN2", lineCount), timeout.Token);
        await WaitForLineAsync(receiverPaths, $"RUN2-line-{lineCount - 1}", sender, timeout.Token);

        var received = ReadReceivedGameLog(receiverPaths);
        Assert.Contains("RUN1-line-0", received, StringComparison.Ordinal);
        Assert.Contains("RUN2-line-0", received, StringComparison.Ordinal);
    }

    private AppPaths CreatePaths(string name)
    {
        var paths = new AppPaths(Path.Combine(_root, name));
        paths.Ensure();
        return paths;
    }

    private static PeerSupportLogService CreateService(
        AppPaths paths,
        IPeerTransport transport,
        ulong steamId64,
        Func<string?> instanceDirectory)
    {
        var now = DateTimeOffset.UtcNow;
        var identity = steamId64.ToString(CultureInfo.InvariantCulture);
        return new PeerSupportLogService(
            paths,
            transport,
            () => (identity, "Player"),
            instanceDirectory,
            _ => Task.FromResult(new SupportEnvironmentSnapshot(
                now, "22", "22", ".NET", "Windows", "X64", "Java", "Pack", "hash",
                [], SteamDiagnosticContext.Unavailable, new Dictionary<string, string>(), string.Empty)),
            () => new SupportNetworkMetrics(
                now, identity, "Ready", 1, 1, false, 0, 0, 0, 0, 0, 0,
                new Dictionary<string, string>()));
    }

    private static string BuildRun(string prefix, int lines)
    {
        var builder = new StringBuilder(lines * 96);
        for (var index = 0; index < lines; index++)
        {
            builder.Append(prefix).Append("-line-").Append(index)
                .Append(" filler ").Append(new string('x', 64))
                .Append(Environment.NewLine);
        }
        return builder.ToString();
    }

    private static async Task WaitForLineAsync(
        AppPaths receiverPaths,
        string marker,
        PeerSupportLogService sender,
        CancellationToken token)
    {
        while (true)
        {
            var received = ReadReceivedGameLog(receiverPaths);
            if (received.Contains(marker, StringComparison.Ordinal)) return;
            if (token.IsCancellationRequested)
            {
                throw new Xunit.Sdk.XunitException(
                    $"'{marker}' never arrived; {received.Length} bytes received. " +
                    $"Sender status: {sender.StatusText}");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None);
        }
    }

    private static string ReadReceivedGameLog(AppPaths receiverPaths)
    {
        var root = Path.Combine(receiverPaths.Personal, "SupportLogs");
        if (!Directory.Exists(root)) return string.Empty;
        var builder = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => Path.GetFileName(path).StartsWith("game", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var stream = new FileStream(
                    file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                builder.Append(reader.ReadToEnd());
            }
            catch (IOException)
            {
            }
        }
        return builder.ToString();
    }
}
