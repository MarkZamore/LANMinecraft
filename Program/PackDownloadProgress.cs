namespace Minecraft;

/// <summary>One byte budget for the pack and its separately downloaded artifacts.</summary>
internal sealed class PackDownloadProgress
{
    private readonly object _gate = new();
    private readonly IProgress<RuntimePreparationProgress>? _output;
    private readonly Scope _pack;
    private readonly Dictionary<string, Scope> _artifacts = new(StringComparer.Ordinal);
    private readonly long _total;

    public PackDownloadProgress(IProgress<RuntimePreparationProgress>? output, long packBytes,
        IReadOnlyDictionary<string, long> artifactSizes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(packBytes);
        ArgumentNullException.ThrowIfNull(artifactSizes);
        _output = output;
        _pack = new(packBytes);
        var total = packBytes;
        foreach (var (id, size) in artifactSizes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentOutOfRangeException.ThrowIfNegative(size);
            _artifacts.Add(id, new Scope(size));
            total = checked(total + size);
        }
        _total = total;
        PackProgress = new ScopeProgress(this, _pack);
        Publish();
    }

    public IProgress<RuntimePreparationProgress> PackProgress { get; }

    public IProgress<RuntimePreparationProgress> ArtifactProgress(string id) => new ScopeProgress(this, Artifact(id));

    public void MarkPackComplete() => Complete(_pack);

    public void MarkArtifactComplete(string id) => Complete(Artifact(id));

    private Scope Artifact(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _artifacts.TryGetValue(id, out var scope)
            ? scope : throw new KeyNotFoundException("Artifact is outside the download plan: " + id);
    }

    private void Report(Scope scope, RuntimePreparationProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        lock (_gate)
        {
            // Each scope reports its absolute retained bytes. A discarded
            // partial file can retract credit; a repeated report cannot add it.
            if (!scope.Complete && progress.TotalBytes > 0)
                scope.Downloaded = Math.Clamp(progress.DownloadedBytes, 0, scope.Size);
            Publish();
        }
    }

    private void Complete(Scope scope)
    {
        lock (_gate)
        {
            scope.Downloaded = scope.Size;
            scope.Complete = true;
            Publish();
        }
    }

    // Updates publish under the same lock so concurrent producers cannot
    // publish older snapshots after newer ones. No SynchronizationContext is captured.
    private void Publish()
    {
        if (_total == 0)
        {
            _output?.Report(new(RuntimePreparationStage.Checking, "Проверка"));
            return;
        }
        var downloaded = _pack.Downloaded;
        var complete = _pack.Complete;
        foreach (var scope in _artifacts.Values)
        {
            downloaded += scope.Downloaded;
            complete &= scope.Complete;
        }
        // Receiving the final byte precedes hash verification. Only the
        // caller's explicit completions can finish the shared download step.
        if (!complete) downloaded = Math.Min(downloaded, _total - 1);
        var fraction = (double)downloaded / _total;
        if (!complete) fraction = Math.Min(fraction, Math.BitDecrement(1d));
        _output?.Report(new(RuntimePreparationStage.SyncingPack, "Сборка",
            fraction, downloaded, _total));
    }

    private sealed class Scope(long size)
    {
        public long Size { get; } = size;
        public long Downloaded { get; set; }
        public bool Complete { get; set; }
    }

    private sealed class ScopeProgress(PackDownloadProgress owner, Scope scope) : IProgress<RuntimePreparationProgress>
    {
        public void Report(RuntimePreparationProgress value) => owner.Report(scope, value);
    }
}
