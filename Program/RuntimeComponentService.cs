using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft;

/// <summary>Downloads one checked component selection and keeps its payloads open until launch registration.</summary>
public sealed class RuntimeComponentService
{
    internal static readonly Uri IndexUri = new("https://raw.githubusercontent.com/MarkZamore/LANMinecraft-Components/main/catalog.json");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly HttpClient _http;
    private readonly Uri _indexUri;
    private readonly int? _launcherRelease;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RuntimeComponentSnapshot? _snapshot;
    internal string CachePath => Path.Combine(Root(_paths), "catalog.json");

    public RuntimeComponentService(AppPaths paths, Logger logger, HttpClient? http = null,
        Uri? indexUri = null, int? launcherRelease = null)
    {
        _paths = paths;
        _logger = logger;
        _http = http ?? PortableHttpClient.Shared;
        _indexUri = indexUri ?? IndexUri;
        _launcherRelease = launcherRelease;
    }

    public async Task<RuntimeComponentSnapshot> LoadAsync(bool refresh, CancellationToken token = default)
        => (await LoadCoreAsync(refresh, token).ConfigureAwait(false)).Snapshot;

    private async Task<CatalogLoad> LoadCoreAsync(bool refresh, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!refresh && _snapshot is not null) return new CatalogLoad(_snapshot, false);
            RuntimeComponentSnapshot? cached = _snapshot;
            try { cached ??= ReadCache(_paths, _launcherRelease); }
            catch (Exception error) when (Recoverable(error)) { _logger.Warn($"Runtime component cache rejected: {error.Message}"); }
            try { cached ??= ReadInstalledSnapshot(); }
            catch (Exception error) when (Recoverable(error)) { _logger.Warn($"Installed runtime component catalog rejected: {error.Message}"); }
            if (!refresh && cached is not null)
            {
                _snapshot = cached;
                return new CatalogLoad(cached, false);
            }
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                var builder = new UriBuilder(_indexUri) { Query = "cache=" + Guid.NewGuid().ToString("N") };
                using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > RuntimeComponentSnapshot.MaximumCatalogBytes)
                    throw new InvalidDataException("Oversized runtime component catalog.");
                await response.Content.LoadIntoBufferAsync(RuntimeComponentSnapshot.MaximumCatalogBytes, deadline.Token).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
                var loaded = RuntimeComponentSnapshot.Parse(json, _launcherRelease);
                EnsureDirectory(_paths, Root(_paths));
                AtomicFile.WriteAllText(CachePath, JsonSerializer.Serialize(new CacheEnvelope(1, json, loaded.Sha256), JsonOptions));
                _logger.Info($"Runtime components: verified catalog {loaded.Revision}, SHA256 {loaded.Sha256}.");
                _snapshot = loaded;
                return new CatalogLoad(loaded, true);
            }
            catch (Exception error) when (Recoverable(error) || error is OperationCanceledException && !token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
                if (cached is null)
                    throw new InvalidOperationException("Для первой загрузки компонентов требуется интернет. Проверенный каталог компонентов отсутствует.", error);
                _logger.Warn($"Runtime components: using checked cached catalog {cached.Revision}: {error.Message}");
                _snapshot = cached;
                return new CatalogLoad(cached, false);
            }
        }
        finally { _gate.Release(); }
    }

    private RuntimeComponentSnapshot? ReadInstalledSnapshot()
    {
        var directory = Path.Combine(Root(_paths), "packs");
        RequireOrdinaryPath(_paths, directory);
        if (!Directory.Exists(directory)) return null;
        var candidates = new List<string>();
        foreach (var candidate in Directory.EnumerateDirectories(directory))
        {
            try
            {
                RequireOrdinaryPath(_paths, candidate);
                var file = Path.Combine(candidate, "selection.json");
                if (File.Exists(file)) candidates.Add(file);
            }
            catch (Exception error) when (Recoverable(error)) { }
        }
        foreach (var file in candidates.OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                var installed = ReadSelection(_paths, file, _launcherRelease);
                if (installed is not null && Directory.Exists(_paths.CombineUnderPacks(installed.PackRelativePath)))
                    return ParseSelectionSnapshot(installed, _launcherRelease);
            }
            catch (Exception error) when (Recoverable(error)) { }
        }
        return null;
    }

    public async Task<PreparedComponentSet> PrepareAsync(string packRelativePath, PackRuntimeDescriptor descriptor,
        IProgress<RuntimePreparationProgress>? progress, CancellationToken token = default)
    {
        if (!PackCatalogService.ValidFolder(packRelativePath)) throw new InvalidDataException("Invalid pack component selection path.");
        try
        {
            var loaded = await LoadCoreAsync(refresh: true, token).ConfigureAwait(false);
            if (!loaded.NetworkVerified && ReadMatchingSelection(packRelativePath, descriptor) is { } installed)
            {
                var snapshot = ParseSelectionSnapshot(installed, _launcherRelease);
                _logger.Info($"Runtime components: using installed offline selection {snapshot.Revision} for {packRelativePath}.");
                return await PrepareSelectionAsync(snapshot, packRelativePath, descriptor, progress, allowDownload: false, token).ConfigureAwait(false);
            }
            return await PrepareSelectionAsync(loaded.Snapshot, packRelativePath, descriptor, progress, allowDownload: true, token).ConfigureAwait(false);
        }
        catch (Exception error) when (Recoverable(error) || error is OperationCanceledException && !token.IsCancellationRequested)
        {
            token.ThrowIfCancellationRequested();
            var installed = ReadMatchingSelection(packRelativePath, descriptor);
            if (installed is null) throw;
            var snapshot = ParseSelectionSnapshot(installed, _launcherRelease);
            _logger.Warn($"Runtime components: preserving installed selection {snapshot.Revision} for {packRelativePath}: {error.Message}");
            return await PrepareSelectionAsync(snapshot, packRelativePath, descriptor, progress, allowDownload: false, token).ConfigureAwait(false);
        }
    }

    private Selection? ReadMatchingSelection(string packRelativePath, PackRuntimeDescriptor descriptor)
    {
        var installed = ReadSelection(_paths, SelectionPath(_paths, packRelativePath), _launcherRelease);
        return installed is not null && installed.DescriptorHash == descriptor.DescriptorHash &&
            installed.PackRelativePath.Equals(packRelativePath, StringComparison.OrdinalIgnoreCase) ? installed : null;
    }

    internal static PreparedComponentSet? PreparePackRename(AppPaths paths, string oldPack, string newPack)
    {
        RuntimeComponentSnapshot.Require(PackCatalogService.ValidFolder(oldPack) && PackCatalogService.ValidFolder(newPack),
            "Invalid component selection rename.");
        var source = SelectionPath(paths, oldPack);
        var destination = SelectionPath(paths, newPack);
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase)) return null;
        var previous = ReadSelection(paths, source, null);
        if (previous is null)
        {
            var emptySource = Path.GetDirectoryName(source)!;
            if (Directory.Exists(emptySource) && !Directory.EnumerateFileSystemEntries(emptySource).Any()) Directory.Delete(emptySource);
            return null;
        }
        RuntimeComponentSnapshot.Require(previous.PackRelativePath.Equals(oldPack, StringComparison.OrdinalIgnoreCase),
            "Component selection does not match its rename source.");
        var renamed = previous with { PackRelativePath = newPack };
        var target = ReadSelection(paths, destination, null);
        if (target is not null && (target.PackRelativePath != renamed.PackRelativePath ||
            target.DescriptorHash != renamed.DescriptorHash || target.CatalogSha256 != renamed.CatalogSha256 ||
            target.JavaRuntimeId != renamed.JavaRuntimeId ||
            !target.ComponentHashes.ToHashSet(StringComparer.Ordinal).SetEquals(renamed.ComponentHashes)))
            throw new IOException("Переименование сборки остановлено: новый путь уже содержит другой набор компонентов.");
        var snapshot = ParseSelectionSnapshot(previous, null);
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
        var handles = new List<FileStream>();
        try
        {
            // Keep existing payloads protected while pack directories and their references move.
            foreach (var artifact in snapshot.Artifacts.Where(artifact => previous.ComponentHashes.Contains(artifact.Sha256)))
            {
                var file = ObjectPath(paths, artifact);
                RequireOrdinaryPath(paths, file);
                if (!File.Exists(file)) continue;
                handles.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read));
                artifacts.Add(artifact.Id, file);
            }
            return new PreparedComponentSet(snapshot, newPack, previous.DescriptorHash, artifacts, handles, () =>
            {
                EnsureDirectory(paths, Path.GetDirectoryName(destination)!);
                AtomicFile.WriteAllText(destination, JsonSerializer.Serialize(renamed, JsonOptions));
                File.Delete(source);
                var sourceDirectory = Path.GetDirectoryName(source)!;
                if (!Directory.EnumerateFileSystemEntries(sourceDirectory).Any()) Directory.Delete(sourceDirectory);
            });
        }
        catch
        {
            foreach (var handle in handles) handle.Dispose();
            throw;
        }
    }

    private async Task<PreparedComponentSet> PrepareSelectionAsync(RuntimeComponentSnapshot snapshot, string pack,
        PackRuntimeDescriptor descriptor, IProgress<RuntimePreparationProgress>? progress, bool allowDownload, CancellationToken token)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var handles = new List<FileStream>();
        try
        {
            foreach (var artifact in snapshot.RequiredArtifacts(descriptor))
            {
                var prepared = await PrepareArtifactAsync(artifact, progress, allowDownload, token).ConfigureAwait(false);
                paths.Add(artifact.Id, prepared.Path);
                handles.Add(prepared.Handle);
            }
            using var scope = RuntimeComponentContext.Use(snapshot);
            var javaId = descriptor.NativeRuntime is null && descriptor.Loader.Type != PackLoaderKind.Native
                ? JavaRuntimeCatalog.RequiredFor(descriptor).RuntimeId : null;
            var selection = new Selection(1, pack, descriptor.DescriptorHash, snapshot.CatalogJson,
                snapshot.Sha256, snapshot.Artifacts.Where(artifact => paths.ContainsKey(artifact.Id)).Select(artifact => artifact.Sha256).Distinct().ToArray(), javaId);
            var selectionPath = SelectionPath(_paths, pack);
            return new PreparedComponentSet(snapshot, pack, descriptor.DescriptorHash, paths, handles, () =>
            {
                EnsureDirectory(_paths, Path.GetDirectoryName(selectionPath)!);
                AtomicFile.WriteAllText(selectionPath, JsonSerializer.Serialize(selection, JsonOptions));
            });
        }
        catch
        {
            foreach (var handle in handles) handle.Dispose();
            throw;
        }
    }

    private async Task<(string Path, FileStream Handle)> PrepareArtifactAsync(RuntimeComponentArtifact artifact,
        IProgress<RuntimePreparationProgress>? progress, bool allowDownload, CancellationToken token)
    {
        using var objectLock = await AcquireObjectLockAsync(_paths, artifact.Sha256, token).ConfigureAwait(false);
        var destination = ObjectPath(_paths, artifact);
        EnsureDirectory(_paths, Path.GetDirectoryName(destination)!);
        if (RuntimeComponentContext.TestPathResolver is { } testResolver)
        {
            var seeded = testResolver(_paths, artifact);
            VerifyArtifact(seeded, artifact);
            if (!Path.GetFullPath(seeded).Equals(destination, StringComparison.OrdinalIgnoreCase)) File.Copy(seeded, destination, true);
        }
        if (!Matches(destination, artifact))
        {
            if (!allowDownload) throw new FileNotFoundException($"Офлайн-компонент {artifact.Id} отсутствует или повреждён.", destination);
            for (var attempt = 1; attempt <= ManagedComponentService.DownloadAttemptsPerSource; attempt++)
            {
                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(45 + artifact.SizeBytes / (128d * 1024d)));
                    using var response = await GitHubReleaseTransport.GetAsync(_http, artifact.DownloadUrl, deadline.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength is { } length && length != artifact.SizeBytes)
                        throw new InvalidDataException($"Runtime component length differs from its catalog: {artifact.Id}");
                    await using (var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                    {
                        var buffer = new byte[64 * 1024];
                        long downloaded = 0;
                        while (true)
                        {
                            using var stall = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                            stall.CancelAfter(ManagedComponentService.DownloadStallTimeout);
                            var read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                            if (read == 0) break;
                            downloaded += read;
                            if (downloaded > artifact.SizeBytes) throw new InvalidDataException("Oversized runtime component payload.");
                            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                            progress?.Report(new RuntimePreparationProgress(RuntimePreparationStage.Downloading,
                                "Компоненты игры", downloaded / (double)artifact.SizeBytes, downloaded, artifact.SizeBytes));
                        }
                    }
                    VerifyArtifact(temporary, artifact);
                    File.Move(temporary, destination, true);
                    break;
                }
                catch (Exception error) when (attempt < ManagedComponentService.DownloadAttemptsPerSource &&
                    (Recoverable(error) || error is OperationCanceledException) && !token.IsCancellationRequested)
                {
                    _logger.Warn($"Runtime component {artifact.Id} download attempt {attempt} failed: {error.Message}");
                    await Task.Delay(ManagedComponentService.BackoffBeforeAttempt(attempt), token).ConfigureAwait(false);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        AtomicFile.WriteAllText(Path.Combine(Path.GetDirectoryName(destination)!, "artifact.json"),
            JsonSerializer.Serialize(new { artifact.Id, artifact.FileName, artifact.Sha256, artifact.SizeBytes }, JsonOptions));
        return (destination, new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read));
    }

    internal static string ObjectPath(AppPaths paths, RuntimeComponentArtifact artifact) =>
        Path.GetFullPath(Path.Combine(Root(paths), "objects", artifact.Sha256, artifact.FileName));

    internal static FileStream? TryAcquireObjectLock(AppPaths paths, string sha256)
    {
        RuntimeComponentSnapshot.Require(RuntimeComponentSnapshot.IsHash(sha256), "Invalid component object lock.");
        var directory = Path.Combine(Root(paths), "locks");
        EnsureDirectory(paths, directory);
        var file = Path.Combine(directory, sha256 + ".lock");
        RequireOrdinaryPath(paths, file);
        try { return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return null; }
    }

    private static async Task<FileStream> AcquireObjectLockAsync(AppPaths paths, string sha256, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (TryAcquireObjectLock(paths, sha256) is { } handle) return handle;
            await Task.Delay(75, token).ConfigureAwait(false);
        }
    }

    internal static IReadOnlySet<string>? ReadProtectedHashes(AppPaths paths, Action? checkpoint = null)
    {
        try
        {
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            if (ReadCache(paths, null) is { } catalog) hashes.UnionWith(catalog.Artifacts.Select(artifact => artifact.Sha256));
            foreach (var selection in ReadSelections(paths, checkpoint)) hashes.UnionWith(selection.ComponentHashes);
            return hashes;
        }
        catch (Exception error) when (Recoverable(error)) { return null; }
    }

    internal static IReadOnlySet<string>? ReadProtectedJavaRuntimeIds(AppPaths paths, Action? checkpoint = null, string? excludingPack = null)
    {
        try
        {
            return ReadSelections(paths, checkpoint).Where(selection => selection.JavaRuntimeId is not null &&
                !selection.PackRelativePath.Equals(excludingPack, StringComparison.OrdinalIgnoreCase))
                .Select(selection => selection.JavaRuntimeId!).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception error) when (Recoverable(error)) { return null; }
    }

    private static IEnumerable<Selection> ReadSelections(AppPaths paths, Action? checkpoint)
    {
        var root = Path.Combine(Root(paths), "packs");
        RequireOrdinaryPath(paths, root);
        if (!Directory.Exists(root)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            checkpoint?.Invoke();
            RequireOrdinaryPath(paths, directory);
            var selection = ReadSelection(paths, Path.Combine(directory, "selection.json"), null)
                ?? throw new InvalidDataException("Unreadable installed component selection.");
            if (Directory.Exists(paths.CombineUnderPacks(selection.PackRelativePath))) yield return selection;
        }
    }

    private static Selection? ReadSelection(AppPaths paths, string file, int? launcherRelease)
    {
        RequireOrdinaryPath(paths, file);
        if (!File.Exists(file)) return null;
        using var input = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > 2L * RuntimeComponentSnapshot.MaximumCatalogBytes) throw new InvalidDataException("Oversized component selection.");
        var selection = JsonSerializer.Deserialize<Selection>(input, JsonOptions)
            ?? throw new InvalidDataException("Empty component selection.");
        RuntimeComponentSnapshot.Require(selection.SchemaVersion == 1 && PackCatalogService.ValidFolder(selection.PackRelativePath) &&
            RuntimeComponentSnapshot.IsHash(selection.DescriptorHash) && selection.ComponentHashes is not null &&
            selection.ComponentHashes.All(RuntimeComponentSnapshot.IsHash), "Invalid installed component selection.");
        var snapshot = ParseSelectionSnapshot(selection, launcherRelease);
        RuntimeComponentSnapshot.Require(selection.ComponentHashes!.All(hash => snapshot.Artifacts.Any(artifact => artifact.Sha256 == hash)),
            "Unknown installed component reference.");
        return selection;
    }

    private static RuntimeComponentSnapshot ParseSelectionSnapshot(Selection selection, int? launcherRelease)
    {
        RuntimeComponentSnapshot.Require(!string.IsNullOrWhiteSpace(selection.CatalogJson) && RuntimeComponentSnapshot.Digest(selection.CatalogJson) == selection.CatalogSha256,
            "Installed component catalog hash mismatch.");
        return RuntimeComponentSnapshot.Parse(selection.CatalogJson, launcherRelease);
    }

    private static RuntimeComponentSnapshot? ReadCache(AppPaths paths, int? launcherRelease)
    {
        var file = Path.Combine(Root(paths), "catalog.json");
        RequireOrdinaryPath(paths, file);
        if (!File.Exists(file)) return null;
        using var input = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > 2L * RuntimeComponentSnapshot.MaximumCatalogBytes) throw new InvalidDataException("Oversized component cache.");
        var saved = JsonSerializer.Deserialize<CacheEnvelope>(input, JsonOptions)
            ?? throw new InvalidDataException("Empty runtime component cache.");
        RuntimeComponentSnapshot.Require(saved.SchemaVersion == 1 && !string.IsNullOrWhiteSpace(saved.CatalogJson) && RuntimeComponentSnapshot.Digest(saved.CatalogJson) == saved.CatalogSha256,
            "Runtime component cache hash mismatch.");
        return RuntimeComponentSnapshot.Parse(saved.CatalogJson, launcherRelease);
    }

    internal static void VerifyArtifact(string path, RuntimeComponentArtifact artifact)
    {
        if (!Matches(path, artifact)) throw new InvalidDataException($"Компонент {artifact.Id} отсутствует или не прошёл проверку SHA-256.");
    }

    private static bool Matches(string path, RuntimeComponentArtifact artifact)
    {
        if (!File.Exists(path)) return false;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked runtime component payload.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return stream.Length == artifact.SizeBytes && Convert.ToHexStringLower(SHA256.HashData(stream)) == artifact.Sha256;
    }

    private static string SelectionPath(AppPaths paths, string pack) => Path.Combine(Root(paths), "packs",
        RuntimeComponentSnapshot.Digest(pack.ToUpperInvariant()), "selection.json");
    private static string Root(AppPaths paths) => Path.Combine(paths.Launcher, "Components");
    private static void EnsureDirectory(AppPaths paths, string path) { RequireOrdinaryPath(paths, path); Directory.CreateDirectory(path); }
    internal static void RequireOrdinaryPath(AppPaths paths, string path)
    {
        paths.EnsureUnderRoot(Path.GetFullPath(path));
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Runtime component path is redirected.");
        }
    }

    private static bool Recoverable(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or
        HttpRequestException or InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or NotSupportedException;
    private sealed record CacheEnvelope(int SchemaVersion, string CatalogJson, string CatalogSha256);
    private readonly record struct CatalogLoad(RuntimeComponentSnapshot Snapshot, bool NetworkVerified);
    private sealed record Selection(int SchemaVersion, string PackRelativePath, string DescriptorHash,
        string CatalogJson, string CatalogSha256, string[] ComponentHashes, string? JavaRuntimeId);
}
