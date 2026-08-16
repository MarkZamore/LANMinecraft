using System.Text;

namespace Minecraft.Tests;

/// <summary>
/// Minecraft rewrites logs/latest.log from scratch on every launch, and players
/// launch it repeatedly during one diagnostics session. Bundles collected from
/// anuvenn showed each launch contributing only its first few hundred lines, so
/// what is pinned here is that the stream survives a restart and keeps growing.
/// </summary>
public sealed class SupportLogCollectorRestartTests
{
    [Fact]
    public async Task AGameRestart_KeepsStreamingTheNewRun()
    {
        using var fixture = new TemporaryPortableRoot();
        var instance = fixture.Paths.CombineUnderInstances("RestartPack");
        var logs = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logs);
        var latest = Path.Combine(logs, "latest.log");
        await File.WriteAllTextAsync(latest, BuildRun("FIRST", 400));

        await using var collector = new SupportLogCollector(
            fixture.Paths,
            SupportLogSanitizer.CreateDefault(fixture.Paths),
            () => instance);
        await collector.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await ReadUntilAsync(
            collector,
            item => Contains(item, "FIRST-line-399"),
            timeout.Token);

        // The game exits and starts again: the file is replaced, and the new
        // run is longer than the offset the collector stopped at.
        File.Delete(latest);
        await File.WriteAllTextAsync(latest, BuildRun("SECOND", 1200), timeout.Token);
        await ReadUntilAsync(
            collector,
            item => Contains(item, "SECOND-line-1199"),
            timeout.Token);

        // ...and it keeps writing after that, which is where the reported
        // bundles went silent.
        await File.AppendAllTextAsync(
            latest,
            "SECOND-tail-after-restart" + Environment.NewLine,
            timeout.Token);
        var tail = await ReadUntilAsync(
            collector,
            item => Contains(item, "SECOND-tail-after-restart"),
            timeout.Token);

        Assert.EndsWith("/latest.log", tail.LogicalName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same file, replaced by a run that is shorter than what was already
    /// read - the classic truncation the reset detector was written for.
    /// </summary>
    [Fact]
    public async Task AShorterRunAfterRestart_IsStreamedFromTheBeginning()
    {
        using var fixture = new TemporaryPortableRoot();
        var instance = fixture.Paths.CombineUnderInstances("ShortRestartPack");
        var logs = Path.Combine(instance, "logs");
        Directory.CreateDirectory(logs);
        var latest = Path.Combine(logs, "latest.log");
        await File.WriteAllTextAsync(latest, BuildRun("LONG", 800));

        await using var collector = new SupportLogCollector(
            fixture.Paths,
            SupportLogSanitizer.CreateDefault(fixture.Paths),
            () => instance);
        await collector.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await ReadUntilAsync(collector, item => Contains(item, "LONG-line-799"), timeout.Token);

        File.Delete(latest);
        await File.WriteAllTextAsync(latest, BuildRun("SHORT", 20), timeout.Token);

        var first = await ReadUntilAsync(
            collector,
            item => Contains(item, "SHORT-line-0"),
            timeout.Token);
        Assert.EndsWith("/latest.log", first.LogicalName, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildRun(string prefix, int lines)
    {
        var builder = new StringBuilder(lines * 64);
        for (var index = 0; index < lines; index++)
        {
            builder.Append(prefix).Append("-line-").Append(index).Append(Environment.NewLine);
        }
        return builder.ToString();
    }

    private static bool Contains(SupportLogCollectorItem item, string marker) =>
        item.Kind == SupportLogCollectorItemKind.Content &&
        item.Text.Contains(marker, StringComparison.Ordinal);

    private static async Task<SupportLogCollectorItem> ReadUntilAsync(
        SupportLogCollector collector,
        Func<SupportLogCollectorItem, bool> predicate,
        CancellationToken token)
    {
        while (await collector.Items.WaitToReadAsync(token))
        {
            while (collector.Items.TryRead(out var item))
            {
                if (predicate(item)) return item;
            }
        }
        throw new EndOfStreamException();
    }

    private sealed class TemporaryPortableRoot : IDisposable
    {
        public TemporaryPortableRoot()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "MinecraftCollectorRestartTests",
                Guid.NewGuid().ToString("N"));
            Paths = new AppPaths(Root);
            Paths.Ensure();
        }

        public string Root { get; }
        public AppPaths Paths { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
