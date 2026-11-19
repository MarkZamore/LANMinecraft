using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Minecraft;

internal sealed partial class WorldTransferStorage
{
    private const int TransferCopyBufferBytes = 1024 * 1024;

    // The archive and the extracted world exist side by side before install,
    // so the receiver needs room for both plus the size the entries declare.
    internal static void EnsureSufficientDiskSpace(string transactionRoot, long archiveSize, long extractedSize = 0)
    {
        var companionSize = extractedSize > 0 ? extractedSize : archiveSize;
        if (archiveSize < 0 || extractedSize < 0 ||
            archiveSize > long.MaxValue - companionSize)
        {
            throw new InvalidOperationException("The declared world size is not plausible.");
        }

        var required = archiveSize + companionSize;
        DriveInfo drive;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(transactionRoot));
            if (string.IsNullOrEmpty(root)) return;
            drive = new DriveInfo(root);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return;
        }

        if (drive.AvailableFreeSpace < required)
        {
            throw new InvalidOperationException(
                $"Not enough free disk space to receive the world: {FormatGigabytes(required)} GB required, " +
                $"{FormatGigabytes(drive.AvailableFreeSpace)} GB available on {drive.Name}.");
        }
    }

    private static string FormatGigabytes(long bytes) =>
        (bytes / (1024d * 1024d * 1024d)).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    internal static async Task CopyWorldDirectoryAsync(
        string sourceRoot,
        string destinationRoot,
        Func<long, long, Task> progress,
        Func<Task> heartbeat,
        CancellationToken token)
    {
        EnsureOrdinaryDirectoryPath(destinationRoot);
        Directory.CreateDirectory(destinationRoot);
        var files = new List<(string Source, string Destination)>();
        long totalBytes = 0;
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((sourceRoot, destinationRoot));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (source, destination) = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
            {
                token.ThrowIfCancellationRequested();
                // Walking and stat-ing a huge world can take minutes before the
                // first byte moves; keep telling the peer we are alive.
                await heartbeat().ConfigureAwait(false);
                var name = Path.GetFileName(entry);
                if (string.Equals(name, "session.lock", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(destination, name);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"World contains an unsupported filesystem link: {entry}");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.CreateDirectory(target);
                    pending.Push((entry, target));
                }
                else
                {
                    totalBytes += new FileInfo(entry).Length;
                    files.Add((entry, target));
                }
            }
        }

        long processed = 0;
        var buffer = new byte[TransferCopyBufferBytes];
        foreach (var (source, target) in files)
        {
            token.ThrowIfCancellationRequested();
            long copied = 0;
            await using (var input = new FileStream(
                source, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length,
                FileOptions.SequentialScan | FileOptions.Asynchronous))
            await using (var output = new FileStream(
                target, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length,
                FileOptions.Asynchronous))
            {
                int read;
                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    copied += read;
                    // The peer still has to hear from us inside a big file, but
                    // the count it hears is only what has actually landed.
                    await heartbeat().ConfigureAwait(false);
                }
            }

            // Windows takes a write into its cache and returns; the disk pays
            // later. Counting bytes as they were handed over made the stage run
            // to the end at memory speed and then sit at "done" for minutes
            // while gigabytes drained. A file counts once it is closed - which
            // is when it is really written.
            processed += copied;
            File.SetLastWriteTime(target, File.GetLastWriteTime(source));
            await progress(processed, totalBytes).ConfigureAwait(false);
        }
    }

    // Walking a large world can itself outlast the peer's idle timeout, so the
    // walk beats as it goes instead of after it finishes.
    private static async Task<List<string>> EnumerateFilesWithHeartbeatAsync(
        string fullRoot,
        Func<Task> heartbeat)
    {
        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
        {
            await heartbeat().ConfigureAwait(false);
            files.Add(file);
        }
        files.Sort((left, right) => string.CompareOrdinal(
            Path.GetRelativePath(fullRoot, left).Replace('\\', '/'),
            Path.GetRelativePath(fullRoot, right).Replace('\\', '/')));
        return files;
    }

    private static readonly HashSet<string> PrecompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mca", ".mcc", ".dat", ".dat_old", ".nbt", ".schem",
        ".png", ".jpg", ".jpeg", ".zip", ".jar", ".gz", ".ogg"
    };

    // Creates the archive and computes the directory hash in one disk pass.
    // The hash format must stay byte-identical to HashDirectoryAsync: the
    // receiver recomputes it over the extracted tree.
    internal static async Task<string> CreateWorldArchiveWithHashAsync(
        string sourceRoot,
        string archivePath,
        Func<long, long, Task> progress,
        Func<Task>? heartbeat,
        CancellationToken token)
    {
        heartbeat ??= () => Task.CompletedTask;
        var fullRoot = Path.GetFullPath(sourceRoot);
        var files = await EnumerateFilesWithHeartbeatAsync(fullRoot, heartbeat).ConfigureAwait(false);
        long totalBytes = 0;
        foreach (var file in files)
        {
            await heartbeat().ConfigureAwait(false);
            totalBytes += new FileInfo(file).Length;
        }

        long processed = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        // Directory entries carry no bytes and stay out of the hash, but an
        // empty folder in the world would otherwise be lost in transit.
        foreach (var directory in Directory.EnumerateDirectories(fullRoot, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            await heartbeat().ConfigureAwait(false);
            if (Directory.EnumerateFileSystemEntries(directory).Any()) continue;
            archive.CreateEntry(Path.GetRelativePath(fullRoot, directory).Replace('\\', '/') + "/");
        }

        var buffer = new byte[TransferCopyBufferBytes];
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(fullRoot, file).Replace('\\', '/');
            var relativeBytes = Encoding.UTF8.GetBytes(relativePath);
            AppendInt64(hash, relativeBytes.Length);
            hash.AppendData(relativeBytes);
            AppendInt64(hash, new FileInfo(file).Length);

            // Region and NBT files are already deflate/zlib-compressed;
            // recompressing them costs minutes of CPU for near-zero gain.
            var level = PrecompressedExtensions.Contains(Path.GetExtension(file))
                ? CompressionLevel.NoCompression
                : CompressionLevel.Fastest;
            var entry = archive.CreateEntry(relativePath, level);
            var lastWrite = File.GetLastWriteTime(file);
            if (lastWrite.Year is >= 1980 and <= 2107) entry.LastWriteTime = lastWrite;
            await using var input = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan);
            await using var output = entry.Open();
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
                processed += read;
                await progress(processed, totalBytes).ConfigureAwait(false);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static async Task ExtractWorldArchiveAsync(
        string archivePath,
        string destinationRoot,
        Func<long, long, Task> progress,
        Action<long>? checkDiskSpace,
        CancellationToken token,
        Func<Task>? heartbeat = null,
        TimeSpan? heartbeatInterval = null)
    {
        heartbeat ??= () => Task.CompletedTask;
        // Opening a huge archive parses its whole central directory, which can
        // outlast the peer's idle timeout on slow media.
        await Task.Yield();
        var openArchive = Task.Run(() => ZipFile.OpenRead(archivePath));
        try
        {
            while (!openArchive.IsCompleted)
            {
                await Task.WhenAny(openArchive, Task.Delay(heartbeatInterval ?? TimeSpan.FromSeconds(5), token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!openArchive.IsCompleted) await heartbeat().ConfigureAwait(false);
            }
        }
        catch
        {
            // Abandoning the open would leak the handle on received.zip and
            // make the caller's cleanup delete fail; dispose it when it lands.
            _ = openArchive.ContinueWith(
                completed =>
                {
                    if (completed.Status == TaskStatus.RanToCompletion) completed.Result.Dispose();
                    else _ = completed.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }

        using var archive = await openArchive.ConfigureAwait(false);
        EnsureOrdinaryDirectoryPath(destinationRoot);
        var fullRoot = Path.GetFullPath(destinationRoot);
        var rootPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        Directory.CreateDirectory(fullRoot);
        // Entry streams are capped at the length each entry declares, so the
        // declared total bounds what extraction can write to disk. Confirm the
        // disk can hold that total before writing the first byte.
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            await heartbeat().ConfigureAwait(false);
            if (entry.Length < 0 || totalBytes > long.MaxValue - entry.Length)
            {
                throw new InvalidDataException("Received archive declares an implausible size.");
            }
            totalBytes += entry.Length;
        }
        checkDiskSpace?.Invoke(totalBytes);

        long processed = 0;
        var buffer = new byte[TransferCopyBufferBytes];
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var destination = Path.GetFullPath(Path.Combine(fullRoot, entry.FullName));
            if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Received archive contains an entry outside the world directory.");
            }
            if (string.IsNullOrEmpty(entry.Name))
            {
                EnsureOrdinaryDirectoryPath(destination);
                Directory.CreateDirectory(destination);
                continue;
            }
            var parent = Path.GetDirectoryName(destination)!;
            EnsureOrdinaryDirectoryPath(parent);
            Directory.CreateDirectory(parent);
            await using (var input = entry.Open())
            await using (var output = new FileStream(
                destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    processed += read;
                    await progress(processed, totalBytes).ConfigureAwait(false);
                }
            }
            File.SetLastWriteTime(destination, entry.LastWriteTime.LocalDateTime);
        }
    }

    internal static async Task<string> HashDirectoryAsync(
        string root,
        Func<long, long, Task>? progress,
        CancellationToken token,
        Func<Task>? heartbeat = null)
    {
        heartbeat ??= () => Task.CompletedTask;
        var fullRoot = Path.GetFullPath(root);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = await EnumerateFilesWithHeartbeatAsync(fullRoot, heartbeat).ConfigureAwait(false);
        long totalBytes = 0;
        foreach (var file in files)
        {
            await heartbeat().ConfigureAwait(false);
            totalBytes += new FileInfo(file).Length;
        }

        long processed = 0;
        var buffer = new byte[TransferCopyBufferBytes];
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(fullRoot, file).Replace('\\', '/');
            var relativeBytes = Encoding.UTF8.GetBytes(relativePath);
            AppendInt64(hash, relativeBytes.Length);
            hash.AppendData(relativeBytes);

            var fileInfo = new FileInfo(file);
            AppendInt64(hash, fileInfo.Length);

            await using var stream = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
                processed += read;
                if (progress is not null) await progress(processed, totalBytes).ConfigureAwait(false);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
