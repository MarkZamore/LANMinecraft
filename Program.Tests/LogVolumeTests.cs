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
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
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
        var instance = paths.CombineUnderInstances("Infinity");
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

    [Fact]
    public void InstanceDiagnostics_StayInsideTheirBudget()
    {
        var paths = CreatePaths();
        var instance = paths.CombineUnderInstances("Infinity");
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
        var instance = paths.CombineUnderInstances("Infinity");
        var pack = paths.CombineUnderPacks("Infinity");
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
        var instance = paths.CombineUnderInstances("Infinity");
        var pack = paths.CombineUnderPacks("Infinity");
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
