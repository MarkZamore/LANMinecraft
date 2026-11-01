namespace Minecraft;

/// <summary>
/// One frozen pack update and its verified launcher metadata. The metadata
/// snapshot is read-only input for planning other pack-owned downloads; it is
/// never applied to the installed pack ahead of the rest of the update.
/// </summary>
public sealed class PackSyncDownloadPlan : IDisposable
{
    private readonly Func<string, string?> _expectedFileSha256;
    private Action? _cleanup;
    private bool _disposed;

    internal PackSyncDownloadPlan(object owner, object? state, string packRelativePath,
        string packDirectory, string metadataDirectory, long pendingBytes,
        Func<string, string?> expectedFileSha256, PackSyncResult? immediateResult = null,
        Action? cleanup = null)
    {
        Owner = owner;
        State = state;
        PackRelativePath = packRelativePath;
        PackDirectory = packDirectory;
        MetadataDirectory = metadataDirectory;
        PendingBytes = pendingBytes;
        _expectedFileSha256 = expectedFileSha256;
        ImmediateResult = immediateResult;
        _cleanup = cleanup;
    }

    public string PackRelativePath { get; }
    public string PackDirectory { get; }
    public string MetadataDirectory { get; }
    /// <summary>Bytes still to transfer, excluding verified metadata fetched while planning.</summary>
    public long PendingBytes { get; }

    /// <summary>The selected remote file hash, or the actual local hash for an offline/custom plan.</summary>
    public string? ExpectedFileSha256(string relativePath)
    {
        ThrowIfDisposed();
        return _expectedFileSha256(relativePath);
    }

    internal object Owner { get; }
    internal object? State { get; }
    internal PackSyncResult? ImmediateResult { get; }
    internal bool Consumed { get; set; }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Exchange(ref _cleanup, null)?.Invoke();
    }
}
