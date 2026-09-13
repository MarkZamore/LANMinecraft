using System.Text;

namespace Minecraft.Tests;

/// <summary>
/// Logs are the one thing a launcher writes forever. A modded session produced
/// a 35 MB debug.log here, a week of them fills a gigabyte, and the diagnostics
/// feature copies all of it to a friend. These cases pin the budgets: what the
/// game is allowed to write, what survives a cleanup, and what is worth
/// another player's bandwidth.
/// </summary>
public sealed class LogVolumeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-log-volume-{Guid.NewGuid():N}");

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
    /// The game's DEBUG copy of a session is the single largest file a player
    /// accumulates, and nothing reads it that latest.log does not cover.
    /// </summary>
    [Fact]
    public void TheGamesDebugCopy_IsNotKeptAndNotStreamed()
    {
        var paths = CreatePaths();
        var instance = paths.CombineUnderInstances("Some Build");
        var logs = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logs);
        var debug = Path.Combine(logs, "debug.log");
        var rolled = Path.Combine(logs, "debug-1.log.gz");
        var latest = Path.Combine(logs, "latest.log");
        File.WriteAllText(debug, new string('d', 4096));
        File.WriteAllText(rolled, "compressed");
        File.WriteAllText(latest, "the session");

        LogCleanupService.RetainRecentSessionDiagnostics(instance);

        Assert.False(File.Exists(debug));
        Assert.False(File.Exists(rolled));
        Assert.True(File.Exists(latest));
    }

    /// <summary>
    /// e4steam writes when it accepts a guest, closes a bridge or loses a
    /// session at DEBUG, and only the debug copy keeps DEBUG. Throwing that copy
    /// away used to throw away the only account of why a guest fell out of a world.
    /// </summary>
    [Fact]
    public void TheE4steamLinesOfTheDebugCopy_OutliveIt()
    {
        var paths = CreatePaths();
        var instance = paths.CombineUnderInstances("Some Build");
        var logs = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logs);
        var debug = Path.Combine(logs, "debug.log");
        File.WriteAllLines(debug,
        [
            "[13Sep2026 20:35:01.000] [Render thread/DEBUG] [net.minecraft.client/]: noise",
            "[13Sep2026 20:35:02.000] [e4steam-steam-runtime/DEBUG] [e4steam/]: Accepted Steam session for known lobby peer",
            "[13Sep2026 20:35:03.000] [e4steam-steam-runtime/WARN] [e4steam/]: Steam Networking Messages session failed",
            "java.io.IOException: bridge closed",
            "\tat link.e4steam.SteamRuntime.run(SteamRuntime.java:120)",
            "[13Sep2026 20:35:04.000] [Render thread/DEBUG] [net.minecraft.client/]: more noise",
        ]);

        LogCleanupService.RetainRecentSessionDiagnostics(instance);

        Assert.False(File.Exists(debug));
        var kept = File.ReadAllLines(Path.Combine(logs, LogCleanupService.E4steamPreviousLogName))
            .Where(line => !line.StartsWith("[launcher]", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(4, kept.Length);
        Assert.Contains(kept, line => line.Contains("Accepted Steam session", StringComparison.Ordinal));
        Assert.Contains(kept, line => line.StartsWith("\tat link.e4steam", StringComparison.Ordinal));
        Assert.DoesNotContain(kept, line => line.Contains("noise", StringComparison.Ordinal));
    }

    /// <summary>
    /// The loader names e4steam in lines that are not e4steam's - every pack it
    /// sorts, every mixin config it applies - and those lines run to megabytes.
    /// Only what e4steam itself wrote is worth keeping.
    /// </summary>
    [Fact]
    public void LinesThatOnlyMentionE4steam_AreNotKept()
    {
        var logs = CreateLogs(out var instance);
        File.WriteAllLines(Path.Combine(logs, "debug.log"),
        [
            "[13Sep2026 20:35:01.000] [main/DEBUG] [mixin/]: Mixing config e4steam.mixins.json",
            "[13Sep2026 20:35:01.000] [Worker-Main-2/INFO] [villagerapi/]: Created new filesystem for mod pack: e4steam",
            "[13Sep2026 20:35:01.000] [Render thread/DEBUG] [net.fabricmc/]: Final sorting result: vanilla, mod/e4steam, mod/emi",
        ]);

        LogCleanupService.RetainRecentSessionDiagnostics(instance);

        Assert.False(File.Exists(Path.Combine(logs, LogCleanupService.E4steamPreviousLogName)));
    }

    /// <summary>
    /// The loader rolls debug.log into debug-1.log.gz on every start, so the
    /// session a guest fell out of is often already an archive by the time the
    /// launcher sweeps.
    /// </summary>
    [Fact]
    public void TheRolledDebugArchives_AreReadToo()
    {
        var logs = CreateLogs(out var instance);
        WriteGzip(Path.Combine(logs, "debug-2.log.gz"),
            "[12Sep2026 21:00:00.000] [e4steam-steam-runtime/DEBUG] [e4steam/]: OLDEST_MARKER");
        WriteGzip(Path.Combine(logs, "debug-1.log.gz"),
            "[13Sep2026 19:00:00.000] [e4steam-steam-runtime/WARN] [e4steam/]: ARCHIVED_MARKER");
        File.WriteAllLines(Path.Combine(logs, "debug.log"),
            ["[13Sep2026 20:35:01.000] [Render thread/DEBUG] [net.minecraft.client/]: noise"]);

        LogCleanupService.RetainRecentSessionDiagnostics(instance);

        var kept = File.ReadAllText(Path.Combine(logs, LogCleanupService.E4steamPreviousLogName));
        Assert.Contains("ARCHIVED_MARKER", kept, StringComparison.Ordinal);
        Assert.True(
            kept.IndexOf("OLDEST_MARKER", StringComparison.Ordinal) < kept.IndexOf("ARCHIVED_MARKER", StringComparison.Ordinal),
            "The oldest archive has to come first.");
        Assert.False(File.Exists(Path.Combine(logs, "debug-1.log.gz")));
    }

    /// <summary>
    /// Players restart the game once or twice to try again before anybody sends
    /// a report; the session that went wrong must still be there when they do.
    /// </summary>
    [Fact]
    public void Sessions_AreAddedToTheRecord_NotReplaced()
    {
        var logs = CreateLogs(out var instance);
        File.WriteAllLines(Path.Combine(logs, "debug.log"),
            ["[13Sep2026 20:00:00.000] [e4steam-steam-runtime/WARN] [e4steam/]: FIRST_SESSION_MARKER"]);
        LogCleanupService.RetainRecentSessionDiagnostics(instance);
        File.WriteAllLines(Path.Combine(logs, "debug.log"),
            ["[13Sep2026 21:00:00.000] [e4steam-steam-runtime/DEBUG] [e4steam/]: SECOND_SESSION_MARKER"]);
        LogCleanupService.RetainRecentSessionDiagnostics(instance);

        var kept = File.ReadAllText(Path.Combine(logs, LogCleanupService.E4steamPreviousLogName));
        Assert.Contains("FIRST_SESSION_MARKER", kept, StringComparison.Ordinal);
        Assert.Contains("SECOND_SESSION_MARKER", kept, StringComparison.Ordinal);
    }

    /// <summary>
    /// A launcher restarted over a game still playing sweeps that instance too,
    /// and the debug log it would read is a session not finished yet.
    /// </summary>
    [Fact]
    public void ADebugLogTheGameStillWrites_IsLeftAlone()
    {
        var logs = CreateLogs(out var instance);
        var debug = Path.Combine(logs, "debug.log");
        File.WriteAllLines(debug,
            ["[13Sep2026 20:00:00.000] [e4steam-steam-runtime/WARN] [e4steam/]: LIVE_SESSION_MARKER"]);

        using (new FileStream(debug, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            LogCleanupService.RetainRecentSessionDiagnostics(instance);
        }

        Assert.True(File.Exists(debug));
        Assert.False(File.Exists(Path.Combine(logs, LogCleanupService.E4steamPreviousLogName)));
    }

    private string CreateLogs(out string instance)
    {
        var paths = CreatePaths();
        instance = paths.CombineUnderInstances("Some Build");
        var logs = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logs);
        return logs;
    }

    private static void WriteGzip(string path, params string[] lines)
    {
        using var file = File.Create(path);
        using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest);
        using var writer = new StreamWriter(gzip);
        foreach (var line in lines) writer.WriteLine(line);
    }

    [Fact]
    public void InstanceDiagnostics_StayInsideTheirBudget()
    {
        var paths = CreatePaths();
        var instance = paths.CombineUnderInstances("Some Build");
        var logs = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logs);

        // Ten recent 16 MB archives: inside the retention window, far outside
        // the size budget.
        var written = new List<string>();
        for (var index = 0; index < 10; index++)
        {
            var path = Path.Combine(logs, $"2026-08-{10 + index:D2}-1.log.gz");
            File.WriteAllBytes(path, new byte[16 * 1024 * 1024]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-index));
            written.Add(path);
        }

        LogCleanupService.RetainRecentSessionDiagnostics(instance);

        var remaining = Directory.GetFiles(logs).Sum(path => new FileInfo(path).Length);
        Assert.True(remaining <= 64L * 1024 * 1024, $"{remaining} bytes survived the budget.");
        // The newest sessions are the ones kept.
        Assert.True(File.Exists(written[0]));
        Assert.False(File.Exists(written[^1]));
    }

    [Fact]
    public void LauncherLogArchives_StayInsideTheirBudget()
    {
        var paths = CreatePaths();
        File.WriteAllText(paths.LogFile, "current session");
        for (var index = 0; index < 12; index++)
        {
            var path = Path.Combine(paths.Personal, $"logs-2026081{index % 10}-0000{index:D2}.log");
            File.WriteAllBytes(path, new byte[2 * 1024 * 1024]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-index));
        }

        LogCleanupService.RunCleanup(paths);

        var archives = Directory.GetFiles(paths.Personal, "logs-*.log");
        var total = archives.Sum(path => new FileInfo(path).Length);
        Assert.True(total <= 16L * 1024 * 1024, $"{total} bytes of launcher logs survived.");
    }

    /// <summary>
    /// A received report is diagnostics, not a backup: the store is bounded so
    /// a friend who sends one after every crash cannot fill the disk.
    /// </summary>
    [Fact]
    public void TheReportStore_IsBoundedToSomethingASessionCanJustify()
    {
        Assert.True(BugReportService.MaxStoredBytes <= 256L * 1024 * 1024);
        // Two ten-mebibyte log tails compress to a fraction of this; the cap is
        // the point where a report stops being diagnostics and becomes a dump.
        Assert.True(BugReportService.MaxArchiveBytes <= 64L * 1024 * 1024);
        Assert.Equal(TimeSpan.FromDays(30), BugReportService.Retention);
    }

    /// <summary>
    /// The configuration handed to the game keeps the file players and crash
    /// reports read, drops the DEBUG copy, and bounds its own rollovers.
    /// </summary>
    [Fact]
    public void TheGameLoggingConfiguration_KeepsLatestLogAndDropsTheRest()
    {
        var paths = CreatePaths();
        var instance = paths.CombineUnderInstances("Some Build");
        var pack = paths.CombineUnderPacks("Some Build");
        Directory.CreateDirectory(instance);
        Directory.CreateDirectory(pack);

        var argument = new GameLogConfigurationService().PrepareArgument(instance, pack);

        Assert.Equal("-Dlog4j.configurationFile=config/log4j2.xml", argument);
        var written = File.ReadAllText(Path.Combine(instance, "config", "log4j2.xml"));
        Assert.Contains("logs/latest.log", written, StringComparison.Ordinal);
        // No appender writes the DEBUG copy - the file name appears only in
        // the comment explaining why it is gone.
        Assert.DoesNotContain("fileName=\"logs/debug.log\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("<Root level=\"DEBUG\"", written, StringComparison.Ordinal);
        Assert.Contains("SizeBasedTriggeringPolicy", written, StringComparison.Ordinal);
        Assert.Contains("IfLastModified age=\"7d\"", written, StringComparison.Ordinal);
    }

    /// <summary>A pack that configured its own logging keeps it.</summary>
    [Fact]
    public void APackWithItsOwnLoggingConfiguration_IsLeftAlone()
    {
        var paths = CreatePaths();
        var instance = paths.CombineUnderInstances("Some Build");
        var pack = paths.CombineUnderPacks("Some Build");
        Directory.CreateDirectory(Path.Combine(pack, "config"));
        File.WriteAllText(Path.Combine(pack, "config", "log4j2.xml"), "<Configuration/>");

        var argument = new GameLogConfigurationService().PrepareArgument(instance, pack);

        Assert.Null(argument);
        Assert.False(File.Exists(Path.Combine(instance, "config", "log4j2.xml")));
    }

    private AppPaths CreatePaths()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        return paths;
    }
}
