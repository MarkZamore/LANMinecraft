namespace Minecraft.Tests;

public sealed class PackDownloadProgressTests
{
    [Fact]
    public void MultipleArtifacts_KeepOneBudgetAndCountAbsoluteRetainedBytes()
    {
        var output = new Recorder();
        var progress = new PackDownloadProgress(output, 100, new Dictionary<string, long>
            { ["model"] = 200, ["runtime"] = 50 });
        AssertSnapshot(output.Last, 0, 350);
        progress.PackProgress.Report(Bytes(60, 100));
        var model = progress.ArtifactProgress("model");
        model.Report(Bytes(100, 200));
        AssertSnapshot(output.Last, 160, 350);
        progress.ArtifactProgress("model").Report(Bytes(100, 200));
        AssertSnapshot(output.Last, 160, 350);
        model.Report(Bytes(20, 200)); // A failed hash discarded the old partial file.
        AssertSnapshot(output.Last, 80, 350);
        progress.ArtifactProgress("runtime").Report(Bytes(50, 50));
        AssertSnapshot(output.Last, 130, 350);
        progress.PackProgress.Report(new(RuntimePreparationStage.SyncingPack, "Сборка обновлена", 1));
        AssertSnapshot(output.Last, 130, 350); // A byte-less status cannot finish a scope.
        Assert.All(output.Reports, item => AssertSnapshot(item, item.DownloadedBytes, 350));
    }

    [Fact]
    public void FinalBytes_WaitForExplicitPackAndArtifactVerification()
    {
        var output = new Recorder();
        var progress = new PackDownloadProgress(output, 100, new Dictionary<string, long> { ["model"] = 900 });
        progress.MarkPackComplete();
        AssertSnapshot(output.Last, 100, 1000);
        progress.ArtifactProgress("model").Report(Bytes(900, 900));
        AssertSnapshot(output.Last, 999, 1000);
        progress.MarkArtifactComplete("model");
        AssertSnapshot(output.Last, 1000, 1000);
        progress.MarkArtifactComplete("model");
        progress.PackProgress.Report(Bytes(0, 100)); // Late reports cannot undo verified scopes.
        AssertSnapshot(output.Last, 1000, 1000);

        var second = new PackDownloadProgress(output, 100, new Dictionary<string, long> { ["model"] = 900 });
        second.MarkArtifactComplete("model");
        second.PackProgress.Report(Bytes(100, 100));
        AssertSnapshot(output.Last, 999, 1000);
        second.MarkPackComplete();
        AssertSnapshot(output.Last, 1000, 1000);
    }

    [Fact]
    public void CachedAndEmptyScopes_CompleteWithoutInventingTransferredBytes()
    {
        var output = new Recorder();
        var progress = new PackDownloadProgress(output, 0, new Dictionary<string, long>
            { ["cached"] = 500, ["empty"] = 0 });
        progress.MarkPackComplete();
        progress.MarkArtifactComplete("cached");
        AssertSnapshot(output.Last, 499, 500);
        progress.MarkArtifactComplete("empty");
        AssertSnapshot(output.Last, 500, 500);

        var emptyOutput = new Recorder();
        var empty = new PackDownloadProgress(emptyOutput, 0, new Dictionary<string, long> { ["empty"] = 0 });
        empty.PackProgress.Report(Bytes(long.MaxValue, 100));
        empty.MarkPackComplete();
        empty.MarkArtifactComplete("empty");
        Assert.All(emptyOutput.Reports, item =>
        {
            Assert.Equal(RuntimePreparationStage.Checking, item.Stage);
            Assert.Equal("Проверка", item.Message);
            Assert.Null(item.Fraction);
            Assert.Equal(0, item.DownloadedBytes);
            Assert.Equal(0, item.TotalBytes);
        });
    }

    [Fact]
    public void PlannedSizes_AreCopiedAndReportsStayWithinTheirOwnScope()
    {
        var output = new Recorder();
        var sizes = new Dictionary<string, long> { ["model"] = 200 };
        var progress = new PackDownloadProgress(output, 100, sizes);
        sizes["model"] = 900;
        progress.PackProgress.Report(Bytes(long.MaxValue, long.MaxValue));
        AssertSnapshot(output.Last, 100, 300);
        progress.ArtifactProgress("model").Report(Bytes(-1, 200));
        AssertSnapshot(output.Last, 100, 300);
        progress.ArtifactProgress("model").Report(Bytes(long.MaxValue, long.MaxValue));
        AssertSnapshot(output.Last, 299, 300);
        Assert.Throws<KeyNotFoundException>(() => progress.ArtifactProgress("unknown"));
        Assert.Throws<KeyNotFoundException>(() => progress.MarkArtifactComplete("unknown"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackDownloadProgress(output, -1, sizes));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackDownloadProgress(output, 0,
            new Dictionary<string, long> { ["negative"] = -1 }));
        Assert.Throws<OverflowException>(() => new PackDownloadProgress(output, long.MaxValue, sizes));

        var huge = new PackDownloadProgress(output, long.MaxValue, new Dictionary<string, long>());
        huge.PackProgress.Report(Bytes(long.MaxValue, long.MaxValue));
        Assert.Equal(long.MaxValue - 1, output.Last.DownloadedBytes);
        Assert.InRange(output.Last.Fraction!.Value, 0d, Math.BitDecrement(1d));
        huge.MarkPackComplete();
        Assert.Equal(1d, output.Last.Fraction);
    }

    [Fact]
    public void Adapters_ReportSynchronouslyWithoutPostingToTheCurrentContext()
    {
        var previous = SynchronizationContext.Current;
        var context = new RecordingContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var output = new Recorder();
            var progress = new PackDownloadProgress(output, 100, new Dictionary<string, long> { ["model"] = 100 });
            progress.PackProgress.Report(Bytes(25, 100));
            AssertSnapshot(output.Last, 25, 200);
            progress.ArtifactProgress("model").Report(Bytes(50, 100));
            AssertSnapshot(output.Last, 75, 200);
            Assert.Equal(0, context.Posts);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [Fact]
    public void ConcurrentArtifacts_PublishConsistentSnapshotsAndFinishTogether()
    {
        var output = new Recorder();
        var sizes = Enumerable.Range(0, 12).ToDictionary(index => index.ToString(), _ => 100L);
        var progress = new PackDownloadProgress(output, 100, sizes);
        Parallel.ForEach(sizes.Keys, id =>
        {
            var artifact = progress.ArtifactProgress(id);
            for (var value = 0; value <= 100; value++) artifact.Report(Bytes(value, 100));
            progress.MarkArtifactComplete(id);
        });
        AssertSnapshot(output.Last, 1200, 1300);
        progress.MarkPackComplete();
        AssertSnapshot(output.Last, 1300, 1300);
        Assert.All(output.Reports, item => AssertSnapshot(item, item.DownloadedBytes, 1300));
    }

    private static RuntimePreparationProgress Bytes(long current, long total) =>
        new(RuntimePreparationStage.Downloading, "Individual file", 1, current, total);

    private static void AssertSnapshot(RuntimePreparationProgress item, long current, long total)
    {
        Assert.Equal(RuntimePreparationStage.SyncingPack, item.Stage);
        Assert.Equal("Сборка", item.Message);
        Assert.Equal(total, item.TotalBytes);
        Assert.Equal(current, item.DownloadedBytes);
        Assert.InRange(item.DownloadedBytes, 0, total);
        Assert.Equal((double)current / total, item.Fraction);
    }

    private sealed class Recorder : IProgress<RuntimePreparationProgress>
    {
        public List<RuntimePreparationProgress> Reports { get; } = [];
        public RuntimePreparationProgress Last => Reports[^1];
        public void Report(RuntimePreparationProgress value) => Reports.Add(value);
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        public int Posts { get; private set; }
        public override void Post(SendOrPostCallback d, object? state) => Posts++;
    }
}
