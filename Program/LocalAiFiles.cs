using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Minecraft;

/// <summary>Verified reusable downloads and an independently verified native runtime directory.</summary>
internal sealed class LocalAiFiles(string root, HttpClient http)
{
    private const string StageOwnership = "LANMinecraft.LocalAI staging v1\n";
    internal static bool IsHash(string value) => value.Length == 64 && value.All(c => char.IsAsciiHexDigit(c));

    internal static string Under(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Replace('\\', '/').Split('/').Any(s =>
                s is "" or "." or ".." || s.EndsWith(' ') || s.EndsWith('.') || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Unsafe local AI file path.");
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(boundary, relative));
        if (!full.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Local AI path escaped its directory.");
        for (var path = full; path is not null; path = Path.GetDirectoryName(path))
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Local AI files cannot pass through links or junctions.");
        return full;
    }

    internal static async Task<bool> MatchesAsync(string path, long? size, string sha, CancellationToken token)
    {
        if (!File.Exists(path) || size is { } length && new FileInfo(path).Length != length) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)) == sha;
    }

    internal async Task<string> GetAsync(Uri uri, long size, string sha, string relative, string label,
        IProgress<RuntimePreparationProgress>? progress, CancellationToken token)
    {
        var target = Under(root, relative);
        if (uri.Scheme != "https" || size <= 0 || !IsHash(sha)) throw new InvalidDataException("Invalid AI artifact pin.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (await MatchesAsync(target, size, sha, token).ConfigureAwait(false)) return target;
        var partial = Under(root, relative + ".part");
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (offset > size) { File.Delete(partial); offset = 0; }
                if (offset < size)
                {
                    var volume = Path.GetPathRoot(target)!;
                    if (new DriveInfo(volume).AvailableFreeSpace < size - offset + 64L * 1024 * 1024)
                        throw new IOException("Недостаточно места для локального ИИ.");
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    requestTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    if (response.StatusCode == HttpStatusCode.OK) offset = 0;
                    else if (response.StatusCode != HttpStatusCode.PartialContent ||
                             response.Content.Headers.ContentRange is not { } range || range.From != offset || range.Length != size || range.To != size - 1)
                        throw new InvalidDataException("AI download returned an inconsistent range.");
                    if (response.Content.Headers.ContentLength is { } bytes && bytes != size - offset)
                        throw new InvalidDataException("AI download length differs from its pin.");
                    await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    await using var output = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create,
                        FileAccess.Write, FileShare.None, 131072, true);
                    var buffer = new byte[131072];
                    while (true)
                    {
                        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        readTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                        var count = await input.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false);
                        if (count == 0) break;
                        if (count > size - offset) throw new InvalidDataException("AI download exceeded its pin.");
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                        offset += count;
                        progress?.Report(new(RuntimePreparationStage.Downloading, label, (double)offset / size, offset, size));
                    }
                    await output.FlushAsync(token).ConfigureAwait(false);
                    output.Flush(true);
                    if (offset != size) throw new EndOfStreamException("AI download ended before its pinned size.");
                }
                if (!await MatchesAsync(partial, size, sha, token).ConfigureAwait(false))
                {
                    if (File.Exists(partial)) File.Delete(partial);
                    throw new InvalidDataException("AI download failed its size or SHA256 verification.");
                }
                File.Move(partial, target, true);
                return target;
            }
            catch (Exception ex) when (attempt < 2 && !token.IsCancellationRequested && ex is IOException or HttpRequestException or InvalidDataException or OperationCanceledException)
            { await Task.Delay(250 * (attempt + 1), token).ConfigureAwait(false); }
        }
    }

    internal static async Task<string> ExtractRuntimeAsync(string archive, string nativeRoot,
        IProgress<RuntimePreparationProgress>? progress, CancellationToken token)
    {
        CleanupAbandonedStages(nativeRoot);
        using var zip = ZipFile.OpenRead(archive);
        var entries = zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToArray();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.TrimEnd('/');
            if (name.Contains('\\')) throw new InvalidDataException("AI runtime archive uses ambiguous path separators.");
            _ = Under(nativeRoot, name);
            var type = (entry.ExternalAttributes >> 16) & 0xF000;
            if (!names.Add(name) || type is not (0 or 0x8000 or 0x4000) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("AI runtime archive contains duplicate or special files.");
            total = checked(total + entry.Length);
            if (total > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("AI runtime archive is too large.");
        }
        if (!entries.Any(e => e.FullName == "ollama.exe")) throw new InvalidDataException("AI runtime archive has no executable.");
        var installed = Under(nativeRoot, "current");
        async Task<bool> Valid(string directory)
        {
            if (!Directory.Exists(directory)) return false;
            var actual = new Stack<string>(); actual.Push(directory);
            while (actual.Count > 0)
                foreach (var item in Directory.EnumerateFileSystemEntries(actual.Pop()))
                {
                    var relative = Path.GetRelativePath(directory, item).Replace('\\', '/');
                    _ = Under(directory, relative);
                    if (Directory.Exists(item)) actual.Push(item);
                    else if (!names.Contains(relative)) return false;
                }
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var file = Under(directory, entry.FullName);
                if (!File.Exists(file) || new FileInfo(file).Length != entry.Length) return false;
                await using var stream = entry.Open();
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                if (!await MatchesAsync(file, entry.Length, hash, token).ConfigureAwait(false)) return false;
            }
            return true;
        }
        if (await Valid(installed).ConfigureAwait(false)) return Under(installed, "ollama.exe");
        if (new DriveInfo(Path.GetPathRoot(installed)!).AvailableFreeSpace < total + 64L * 1024 * 1024)
            throw new IOException("Недостаточно места для распаковки локального ИИ.");
        var stage = Under(nativeRoot, "stage-" + Guid.NewGuid().ToString("N"));
        var ownership = Under(nativeRoot, Path.GetFileName(stage) + ".owned");
        AtomicFile.WriteAllText(ownership, StageOwnership);
        Directory.CreateDirectory(stage);
        try
        {
            for (var index = 0; index < entries.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var entry = entries[index]; var file = Under(stage, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await using var input = entry.Open();
                await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                await input.CopyToAsync(output, token).ConfigureAwait(false);
                progress?.Report(new(RuntimePreparationStage.Verifying, "Локальный ИИ: файлы Ollama", (double)(index + 1) / entries.Length));
            }
            if (!await Valid(stage).ConfigureAwait(false)) throw new InvalidDataException("Extracted AI runtime verification failed.");
            if (Directory.Exists(installed)) DeleteCheckedTree(nativeRoot, installed);
            Directory.Move(stage, installed);
            return Under(installed, "ollama.exe");
        }
        finally
        {
            if (Directory.Exists(stage)) DeleteCheckedTree(nativeRoot, stage);
            if (File.Exists(ownership)) File.Delete(ownership);
        }
    }

    internal static void CleanupAbandonedStages(string nativeRoot)
    {
        if (!Directory.Exists(nativeRoot)) return;
        // Called while the installation-wide preparation lock is held. A
        // directory name alone does not grant ownership of somebody's files.
        foreach (var marker in Directory.EnumerateFiles(nativeRoot, "stage-*.owned"))
        {
            var name = Path.GetFileNameWithoutExtension(marker);
            if (name.Length != 38 || !name.StartsWith("stage-", StringComparison.Ordinal) || !name[6..].All(char.IsAsciiHexDigit)) continue;
            _ = Under(nativeRoot, Path.GetFileName(marker));
            if (new FileInfo(marker).Length > 128 || File.ReadAllText(marker) != StageOwnership) continue;
            var stage = Under(nativeRoot, name);
            if (Directory.Exists(stage)) DeleteCheckedTree(nativeRoot, stage);
            File.Delete(marker);
        }
    }

    private static void DeleteCheckedTree(string root, string directory)
    {
        _ = Under(root, Path.GetRelativePath(root, directory));
        var pending = new Stack<string>(); pending.Push(directory);
        while (pending.Count > 0)
            foreach (var item in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                _ = Under(root, Path.GetRelativePath(root, item));
                if (Directory.Exists(item)) pending.Push(item);
            }
        Directory.Delete(directory, true);
    }
}
