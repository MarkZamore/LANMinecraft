using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

public sealed record PackSyncSource(string Owner, string Repo, string Tag);

public enum PackSyncOutcome
{
    SkippedNoSource,
    UpToDate,
    Updated,
    Installed,
    OfflineFallback
}

public sealed record PackSyncResult(
    PackSyncOutcome Outcome,
    string? Revision,
    int FilesChanged,
    long BytesDownloaded,
    string? Warning);

/// <summary>
/// The pack directory was left holding files from two revisions: the download
/// succeeded and writing them out did not. Deliberately outside the exception
/// set that makes a sync fall back to the installed pack.
/// </summary>
public sealed class PackApplyFailedException(Exception inner)
    : InvalidOperationException(
        "Не удалось записать файлы сборки. Закройте игру и попробуйте ещё раз: " + inner.Message, inner);

/// <summary>
/// Mirrors a pack directory from a GitHub release manifest: downloads only the
/// missing or changed files, deletes files the manifest no longer lists, and
/// never rewrites a file whose content already matches. Packs without a sync
/// source are custom and stay untouched.
/// </summary>
public sealed partial class PortablePackSyncService
{
    public const string SourceMarkerFileName = "portable-pack-source.json";
    public const string SyncStateFileName = ".portable-pack-sync.json";
    public const string PackManifestAssetName = "pack-manifest.json";
    public const string DefaultPackRelativePath = "LL8 Extended";

    public static PackSyncSource DefaultPackSource { get; } =
        new("MarkZamore", "LL8-Extended", "pack-latest");

    /// <summary>
    /// Repositories the built-in pack was published from before the rename.
    /// A marker naming one of them inside the default pack folder is what the
    /// rename left behind, not a source the player chose.
    /// </summary>
    private static readonly string[] LegacyDefaultSourceRepos = ["Infinity", "InfinityPack", "LL8"];

    /// <summary>
    /// A pack the launcher knows how to fetch, and therefore may offer before it
    /// exists on disk. Without an entry here a pack can only be seen by someone
    /// who already has its folder, which is no way to hand a new build to
    /// friends: they have nothing to press Play on.
    /// </summary>
    public sealed record KnownPack(string RelativePath, PackSyncSource Source);

    /// <summary>
    /// Every build the launcher offers. The first is the built-in one that a
    /// fresh install lands on; the rest are offered beside it, each syncing from
    /// its own repository.
    /// </summary>
    public static IReadOnlyList<KnownPack> KnownPacks { get; } = Array.AsReadOnly<KnownPack>([
        new(DefaultPackRelativePath, DefaultPackSource),
        new("ATM10", new PackSyncSource("MarkZamore", "ATM10", "pack-latest")),
        new("The Broken Script Enhanced",
            new PackSyncSource("MarkZamore", "The-Broken-Script-Enhanced", "pack-latest")),
    ]);

    /// <summary>The source for a pack the launcher knows, or null for a custom one.</summary>
    public static PackSyncSource? KnownSourceFor(string? packRelativePath)
    {
        var name = packRelativePath?.Trim().Trim('\\', '/');
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var pack in KnownPacks)
        {
            if (string.Equals(pack.RelativePath, name, StringComparison.OrdinalIgnoreCase))
            {
                return pack.Source;
            }
        }
        return null;
    }

    internal const string OfflineCheckWarning =
        "Не удалось проверить обновления сборки — играем с локальной версией.";
    internal const string OfflineSyncWarning =
        "Не удалось обновить сборку — играем с локальной версией.";
    internal const string FirstInstallUnavailableMessage =
        "Сборка ещё не установлена, а сервер сборки недоступен. Проверьте интернет и попробуйте снова.";
    // A cache generation, not a data format: it is bumped to throw the cached
    // work away and redo it, so it is deliberately independent of
    // PortableFormat's version - a release must not cost every player a
    // re-download for an unrelated change.

    private const int SourceMarkerCacheGeneration = 1;
    private const int RemoteManifestCacheGeneration = 1;
    private const int SyncStateCacheGeneration = 1;
    private const string InstanceStateFileName = ".portable-instance.json";
    private const string StagingDirectoryName = ".pack-sync-stage";
    private const int MaximumParallelDownloads = 4;
    private const int ManifestFetchAttempts = 3;
    /// <summary>The pack's word to the launcher: preset, pack list, reset tokens.</summary>
    private const string LauncherDataPrefix = "launcher/";
    /// <summary>A ceiling that keeps this to text files, never a pack's jars.</summary>
    private const long MaximumLauncherDataBytes = 8L * 1024 * 1024;
    private const int AssetDownloadAttempts = 3;
    private const int ReplaceAttempts = 5;
    private const int ReplaceRetryDelay = 120;
    private const long ProgressReportIntervalBytes = 4L * 1024 * 1024;
    private static readonly TimeSpan ManifestAttemptTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadSourceTimeout = TimeSpan.FromSeconds(45);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly HttpClient _httpClient;
    private readonly Func<string, long, bool> _freeSpaceProbe;

    public PortablePackSyncService(
        AppPaths paths,
        Logger logger,
        HttpClient? httpClient = null,
        Func<string, long, bool>? freeSpaceProbe = null)
    {
        _paths = paths;
        _logger = logger;
        _httpClient = httpClient ?? PortableHttpClient.Shared;
        _freeSpaceProbe = freeSpaceProbe ?? HasFreeSpace;
    }

    internal static bool IsLegacyDefaultSource(PackSyncSource source) =>
        string.Equals(source.Owner, DefaultPackSource.Owner, StringComparison.OrdinalIgnoreCase) &&
        LegacyDefaultSourceRepos.Contains(source.Repo, StringComparer.OrdinalIgnoreCase);

    private static bool IsDefaultPack(string packRelativePath) =>
        string.Equals(packRelativePath, DefaultPackRelativePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the pack's source marker. An invalid marker makes the pack custom
    /// (never synced); an absent marker falls back to the source of a pack the
    /// launcher knows, and a marker the rename left pointing at the built-in
    /// pack's former repository is treated as naming the current one.
    /// </summary>
    public PackSyncSource? TryResolveSource(string packRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packRelativePath);
        var markerPath = Path.Combine(_paths.CombineUnderPacks(packRelativePath), SourceMarkerFileName);
        if (File.Exists(markerPath))
        {
            try
            {
                var marker = ReadSourceMarker(File.ReadAllText(markerPath));
                if (marker is null)
                {
                    _logger.Warn($"Pack sync source marker is invalid; the pack is treated as custom: {markerPath}");
                    return null;
                }
                if (IsDefaultPack(packRelativePath) && IsLegacyDefaultSource(marker))
                {
                    _logger.Info(
                        $"Pack sync source marker still names {marker.Repo}; " +
                        $"the built-in {DefaultPackRelativePath} source is used and the marker is rewritten.");
                    return DefaultPackSource;
                }
                return marker;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.Warn($"Pack sync source marker could not be read; the pack is treated as custom: {ex.Message}");
                return null;
            }
        }

        return KnownSourceFor(packRelativePath);
    }

    /// <summary>
    /// Stamps the built-in source on the default pack folder. A marker naming
    /// anyone else - or one this build cannot read - is the player's own and
    /// stays, so a custom pack is never repointed at the built-in release.
    /// </summary>
    public void EnsureDefaultSourceMarker(string packRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packRelativePath);
        var packDir = _paths.CombineUnderPacks(packRelativePath);
        var markerPath = Path.Combine(packDir, SourceMarkerFileName);
        if (File.Exists(markerPath))
        {
            var marker = TryReadSourceMarker(markerPath);
            if (marker is null || !IsLegacyDefaultSource(marker)) return;
        }
        WriteSourceMarker(packDir, DefaultPackSource);
    }

    /// <summary>
    /// Brings just the pack's <c>launcher/</c> folder up to date, without
    /// touching the rest of the pack.
    ///
    /// That folder is what the pack says to the launcher rather than to the
    /// game: the controls preset, the resource pack list, the reset tokens.
    /// All of it is one small asset in the manifest, tens of kilobytes against
    /// the pack's three gigabytes, so it can be fetched while the window opens.
    /// Without this the launcher answers questions about a preset it has not
    /// seen yet - it would call a layout published an hour ago "applied" and
    /// keep its button grey until somebody pressed Play for other reasons.
    ///
    /// Everything is verified against the manifest before it lands, and a
    /// failure is silent: this is a convenience, and the full sync at launch
    /// remains the thing that guarantees the pack.
    /// </summary>
    /// <returns>True when a file was replaced, so the caller can look again.</returns>
    public async Task<bool> RefreshLauncherDataAsync(string packRelativePath, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packRelativePath);
        var source = TryResolveSource(packRelativePath);
        if (source is null) return false;
        var packDir = _paths.CombineUnderPacks(packRelativePath);
        if (!PackManifestService.HasManifest(packDir)) return false;

        var staging = Path.Combine(packDir, StagingDirectoryName + "launcher-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var dto = await FetchManifestAsync(source, token).ConfigureAwait(false);
            var manifest = ValidateRemoteManifest(dto, packDir);

            var wanted = manifest.Files
                .Where(file => file.Path.StartsWith(LauncherDataPrefix, StringComparison.Ordinal))
                .Where(file => !HasFileWithHash(packDir, file))
                .ToList();
            if (wanted.Count == 0) return false;

            var assets = wanted
                .Select(file => manifest.AssetByCoveredPath[file.Path])
                .DistinctBy(asset => asset.Name)
                .ToList();
            // Only ever the small ones: this runs on a window opening, not on a
            // launch, and a pack's mod jars have no business here.
            if (assets.Sum(asset => asset.SizeBytes) > MaximumLauncherDataBytes) return false;

            var stageAssets = Path.Combine(staging, "assets");
            var stageExtract = Path.Combine(staging, "extract");
            Directory.CreateDirectory(stageAssets);
            Directory.CreateDirectory(stageExtract);
            foreach (var asset in assets)
            {
                await DownloadAssetAsync(source, asset, Path.Combine(stageAssets, asset.Name), _ => { }, token)
                    .ConfigureAwait(false);
            }
            ExtractAndVerifyStagedSources(
                manifest,
                assets,
                wanted.Select(file => file.Path).ToList(),
                stageAssets,
                stageExtract,
                token);

            foreach (var file in wanted)
            {
                var staged = ResolveStagedSource(manifest.AssetByCoveredPath[file.Path], stageAssets, stageExtract, file.Path);
                PlaceStagedFile(packDir, file.Path, staged);
            }
            _logger.Info($"Pack {packRelativePath}: {wanted.Count} launcher file(s) refreshed ahead of a full sync.");
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or KeyNotFoundException)
        {
            _logger.Warn($"The pack's launcher data could not be refreshed ({ex.Message}); the next launch will bring it.");
            return false;
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>True when the pack already holds exactly this file.</summary>
    private static bool HasFileWithHash(string packDir, ValidatedFile file)
    {
        var path = Path.Combine(packDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
        var info = new FileInfo(path);
        return info.Exists && info.Length == file.SizeBytes && HashesEqual(HashFile(path), file.Sha256);
    }

    public async Task<PackSyncResult> SyncAsync(
        string packRelativePath,
        IProgress<RuntimePreparationProgress>? progress,
        CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packRelativePath);
        var source = TryResolveSource(packRelativePath);
        if (source is null)
        {
            return new PackSyncResult(PackSyncOutcome.SkippedNoSource, null, 0, 0, null);
        }

        var packDir = _paths.CombineUnderPacks(packRelativePath);
        SweepAbandonedEntries(packDir, StagingDirectoryName + "*");
        var hadManifest = PackManifestService.HasManifest(packDir);
        progress?.Report(new RuntimePreparationProgress(
            RuntimePreparationStage.SyncingPack,
            "Проверка сборки"));

        ValidatedManifest manifest;
        try
        {
            var dto = await FetchManifestAsync(source, token).ConfigureAwait(false);
            manifest = ValidateRemoteManifest(dto, packDir);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException)
        {
            _logger.Warn($"Pack sync manifest for {packRelativePath} is unavailable or invalid: {ex.Message}");
            if (hadManifest)
            {
                return new PackSyncResult(PackSyncOutcome.OfflineFallback, null, 0, 0, OfflineCheckWarning);
            }
            throw new InvalidOperationException(FirstInstallUnavailableMessage, ex);
        }

        try
        {
            return await SyncCoreAsync(source, packDir, manifest, hadManifest, progress, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (hadManifest &&
                                   ex is HttpRequestException or IOException or InvalidDataException
                                       or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _logger.Warn($"Pack sync for {packRelativePath} failed; playing the local version: {ex.Message}");
            return new PackSyncResult(PackSyncOutcome.OfflineFallback, null, 0, 0, OfflineSyncWarning);
        }
    }

    private async Task<PackSyncResult> SyncCoreAsync(
        PackSyncSource source,
        string packDir,
        ValidatedManifest manifest,
        bool hadManifest,
        IProgress<RuntimePreparationProgress>? progress,
        CancellationToken token)
    {
        var statePath = Path.Combine(packDir, SyncStateFileName);
        var diff = await Task.Run(() => ComputeDiff(packDir, statePath, manifest, token), token).ConfigureAwait(false);

        if (string.Equals(diff.StateRevision, manifest.Revision, StringComparison.Ordinal) &&
            diff.ChangedPaths.Count == 0 &&
            diff.Extras.Count == 0)
        {
            progress?.Report(new RuntimePreparationProgress(
                RuntimePreparationStage.SyncingPack,
                "Сборка актуальна",
                1));
            return new PackSyncResult(PackSyncOutcome.UpToDate, manifest.Revision, 0, 0, null);
        }

        var changedSet = new HashSet<string>(diff.ChangedPaths, StringComparer.OrdinalIgnoreCase);
        var neededAssets = manifest.Assets
            .Where(asset => asset.Covers.Any(changedSet.Contains))
            .ToList();
        var totalPendingBytes = neededAssets.Sum(asset => asset.SizeBytes);

        Directory.CreateDirectory(packDir);
        var stageParent = Path.Combine(packDir, StagingDirectoryName);
        try
        {
            var stageRoot = Path.Combine(stageParent, Guid.NewGuid().ToString("N"));
            var stageAssets = Path.Combine(stageRoot, "assets");
            var stageExtract = Path.Combine(stageRoot, "extract");
            if (neededAssets.Count > 0)
            {
                var requiredBytes = checked(totalPendingBytes * 2);
                if (!_freeSpaceProbe(packDir, requiredBytes))
                {
                    throw new IOException(
                        "Not enough free disk space to update the pack: " +
                        $"{FormatMegabytes(requiredBytes)} MB required.");
                }
                Directory.CreateDirectory(stageAssets);
                Directory.CreateDirectory(stageExtract);
                await DownloadAssetsAsync(source, neededAssets, stageAssets, totalPendingBytes, progress, token)
                    .ConfigureAwait(false);
            }

            await Task.Run(
                () =>
                {
                    if (neededAssets.Count > 0)
                    {
                        ExtractAndVerifyStagedSources(
                            manifest, neededAssets, diff.ChangedPaths, stageAssets, stageExtract, token);
                    }
                    try
                    {
                        ApplyChanges(
                            packDir, manifest, diff.ChangedPaths, diff.Extras, stageAssets, stageExtract, token);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                                   or NotSupportedException)
                    {
                        // Files have already landed, so the pack is now a mix of
                        // two revisions. Falling back to "play the local version"
                        // here would start the game on that mix.
                        throw new PackApplyFailedException(ex);
                    }
                    WriteSyncState(statePath, packDir, manifest);
                    WriteSourceMarker(packDir, source);
                },
                token).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(stageParent);
        }

        var outcome = !hadManifest
            ? PackSyncOutcome.Installed
            : diff.ChangedPaths.Count > 0 || diff.Extras.Count > 0
                ? PackSyncOutcome.Updated
                : PackSyncOutcome.UpToDate;
        var bytesDownloaded = neededAssets.Count > 0 ? totalPendingBytes : 0;
        _logger.Info(
            $"Pack sync finished: {outcome}, revision {manifest.Revision}, " +
            $"{diff.ChangedPaths.Count} file(s) written, {diff.Extras.Count} extra(s) removed.");
        progress?.Report(new RuntimePreparationProgress(
            RuntimePreparationStage.SyncingPack,
            outcome == PackSyncOutcome.UpToDate ? "Сборка актуальна" : "Сборка обновлена",
            1));
        return new PackSyncResult(
            outcome,
            manifest.Revision,
            diff.ChangedPaths.Count + diff.Extras.Count,
            bytesDownloaded,
            null);
    }

    private async Task<RemoteManifestDto> FetchManifestAsync(PackSyncSource source, CancellationToken token)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= ManifestFetchAttempts; attempt++)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(ManifestAttemptTimeout);
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    BuildReleaseAssetUri(source, PackManifestAssetName, preventCaching: true));
                request.Headers.CacheControl = new CacheControlHeaderValue
                {
                    NoCache = true,
                    NoStore = true
                };
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
                return await JsonSerializer
                        .DeserializeAsync<RemoteManifestDto>(stream, JsonOptions, timeoutCts.Token)
                        .ConfigureAwait(false)
                    ?? throw new InvalidDataException("Pack sync manifest is empty.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException
                                           or InvalidDataException or OperationCanceledException)
            {
                lastError = ex;
                _logger.Warn($"Pack sync manifest fetch attempt {attempt}/{ManifestFetchAttempts} failed: {ex.Message}");
                if (attempt < ManifestFetchAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt), token).ConfigureAwait(false);
                }
            }
        }

        throw new HttpRequestException(
            $"Pack sync manifest could not be downloaded after {ManifestFetchAttempts} attempts.",
            lastError);
    }

    private async Task DownloadAssetsAsync(
        PackSyncSource source,
        IReadOnlyList<ValidatedAsset> assets,
        string stageAssets,
        long totalBytes,
        IProgress<RuntimePreparationProgress>? progress,
        CancellationToken token)
    {
        long downloadedBytes = 0;
        long lastReportedBytes = 0;
        void ReportBytes(long delta)
        {
            var current = Interlocked.Add(ref downloadedBytes, delta);
            while (true)
            {
                var last = Interlocked.Read(ref lastReportedBytes);
                if (current < totalBytes && current - last < ProgressReportIntervalBytes) return;
                if (Interlocked.CompareExchange(ref lastReportedBytes, current, last) == last) break;
            }
            var bounded = Math.Clamp(current, 0, totalBytes);
            progress?.Report(new RuntimePreparationProgress(
                RuntimePreparationStage.SyncingPack,
                "Обновление",
                totalBytes > 0 ? (double)bounded / totalBytes : null,
                bounded,
                totalBytes));
        }

        progress?.Report(new RuntimePreparationProgress(
            RuntimePreparationStage.SyncingPack,
            "Обновление",
            totalBytes > 0 ? 0d : null,
            0,
            totalBytes));

        await Parallel.ForEachAsync(
            assets,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaximumParallelDownloads,
                CancellationToken = token
            },
            async (asset, assetToken) =>
                await DownloadAssetAsync(
                        source,
                        asset,
                        Path.Combine(stageAssets, asset.Name),
                        ReportBytes,
                        assetToken)
                    .ConfigureAwait(false)).ConfigureAwait(false);
    }

    private async Task DownloadAssetAsync(
        PackSyncSource source,
        ValidatedAsset asset,
        string destinationPath,
        Action<long> reportBytes,
        CancellationToken token)
    {
        var assetUri = BuildReleaseAssetUri(source, asset.Name);
        Exception? lastError = null;
        for (var attempt = 1; attempt <= AssetDownloadAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            long attemptBytes = 0;
            try
            {
                using var sourceTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                sourceTimeout.CancelAfter(DownloadTimeoutFor(asset.SizeBytes));
                var sourceToken = sourceTimeout.Token;
                TryDeleteFile(destinationPath);

                using var request = new HttpRequestMessage(HttpMethod.Get, assetUri);
                using var response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, sourceToken)
                    .ConfigureAwait(false);
                var effectiveUri = response.RequestMessage?.RequestUri ?? assetUri;
                if (!IsApprovedEffectiveUri(effectiveUri))
                {
                    throw new InvalidDataException(
                        $"Pack asset {asset.Name} download was redirected to {SanitizeUri(effectiveUri)}.");
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } length && length != asset.SizeBytes)
                {
                    throw new InvalidDataException(
                        $"Pack asset {asset.Name} has an unexpected Content-Length {length}.");
                }

                await using (var input = await response.Content.ReadAsStreamAsync(sourceToken).ConfigureAwait(false))
                await using (var output = new FileStream(
                                 destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await CopyExactAsync(
                        input,
                        output,
                        asset.SizeBytes,
                        read =>
                        {
                            attemptBytes += read;
                            reportBytes(read);
                        },
                        sourceToken).ConfigureAwait(false);
                    await output.FlushAsync(sourceToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }

                sourceTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                token.ThrowIfCancellationRequested();
                VerifyStagedAsset(destinationPath, asset);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                TryDeleteFile(destinationPath);
                // Rethrown against the caller's token so Parallel.ForEachAsync
                // recognizes it as cooperative cancellation, not a new fault.
                token.ThrowIfCancellationRequested();
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                           or OperationCanceledException)
            {
                lastError = ex;
                if (attemptBytes > 0) reportBytes(-attemptBytes);
                TryDeleteFile(destinationPath);
                _logger.Warn(
                    $"Pack asset {asset.Name} download attempt {attempt}/{AssetDownloadAttempts} failed: {ex.Message}");
                if (attempt < AssetDownloadAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), token).ConfigureAwait(false);
                }
            }
        }

        throw new IOException(
            $"Could not download pack asset after {AssetDownloadAttempts} attempts: {asset.Name}",
            lastError);
    }

    private static async Task CopyExactAsync(
        Stream input,
        Stream output,
        long expectedSize,
        Action<int> reportBytes,
        CancellationToken token)
    {
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0) break;
            total = checked(total + read);
            if (total > expectedSize)
            {
                throw new InvalidDataException("Pack asset exceeds its expected size.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            reportBytes(read);
        }
        if (total != expectedSize)
        {
            throw new InvalidDataException("Pack asset is shorter than its expected size.");
        }
    }

    private static void VerifyStagedAsset(string path, ValidatedAsset asset)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != asset.SizeBytes)
        {
            throw new InvalidDataException($"Pack asset {asset.Name} size does not match the manifest.");
        }
        if (!HashesEqual(HashFile(path), asset.Sha256))
        {
            throw new InvalidDataException($"Pack asset {asset.Name} SHA-256 does not match the manifest.");
        }
    }

    private PackSyncDiff ComputeDiff(
        string packDir,
        string statePath,
        ValidatedManifest manifest,
        CancellationToken token)
    {
        var state = ReadSyncState(statePath);
        var changedPaths = new List<string>();
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(Path.Combine(packDir, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!info.Exists)
            {
                changedPaths.Add(file.Path);
                continue;
            }

            string sha;
            if (state.Files.TryGetValue(file.Path, out var cached) &&
                cached.SizeBytes == info.Length &&
                cached.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks &&
                !string.IsNullOrWhiteSpace(cached.Sha256))
            {
                sha = cached.Sha256;
            }
            else
            {
                sha = HashFile(info.FullName);
            }
            if (!HashesEqual(sha, file.Sha256))
            {
                changedPaths.Add(file.Path);
            }
        }

        return new PackSyncDiff(state.Revision, changedPaths, FindExtras(packDir, manifest));
    }

    /// <summary>
    /// Everything a synced pack ships is listed in its manifest, so anything
    /// else under the pack directory is what the pack used to be. Leaving such
    /// a folder behind would also make this machine hash a different pack than
    /// a fresh install of the same revision, and peers compare that hash.
    /// </summary>
    private static List<string> FindExtras(string packDir, ValidatedManifest manifest)
    {
        var extras = new List<string>();
        if (!Directory.Exists(packDir)) return extras;
        foreach (var entry in Directory.EnumerateFileSystemEntries(packDir))
        {
            var name = Path.GetFileName(entry);
            if (IsProtectedRootName(name) || IsStagingEntryName(name)) continue;
            if (File.Exists(entry))
            {
                if (!manifest.FilesByPath.ContainsKey(name)) extras.Add(name);
                continue;
            }
            if (!Directory.Exists(entry)) continue;
            foreach (var file in Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(packDir, file).Replace('\\', '/');
                if (IsServiceRelativePath(relative)) continue;
                if (!manifest.FilesByPath.ContainsKey(relative)) extras.Add(relative);
            }
        }
        return extras;
    }

    private static void ExtractAndVerifyStagedSources(
        ValidatedManifest manifest,
        IReadOnlyList<ValidatedAsset> neededAssets,
        IReadOnlyList<string> changedPaths,
        string stageAssets,
        string stageExtract,
        CancellationToken token)
    {
        foreach (var asset in neededAssets)
        {
            if (asset.Kind != PackAssetKind.ZipRoot) continue;
            ExtractZipRootAsset(Path.Combine(stageAssets, asset.Name), stageExtract, token);
        }

        foreach (var path in changedPaths)
        {
            token.ThrowIfCancellationRequested();
            var asset = manifest.AssetByCoveredPath[path];
            var staged = ResolveStagedSource(asset, stageAssets, stageExtract, path);
            if (!File.Exists(staged))
            {
                throw new InvalidDataException($"Pack asset {asset.Name} does not provide {path}.");
            }
            if (asset.Kind == PackAssetKind.ZipRoot &&
                !HashesEqual(HashFile(staged), manifest.FilesByPath[path].Sha256))
            {
                throw new InvalidDataException(
                    $"Extracted pack file does not match the manifest SHA-256: {path}");
            }
        }
    }

    private static void ExtractZipRootAsset(string archivePath, string extractRoot, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var normalizedRoot = Path.GetFullPath(extractRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.FullName.Length == 0) continue;
            var destination = Path.GetFullPath(Path.Combine(
                normalizedRoot,
                entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Pack asset entry escapes its destination: {entry.FullName}");
            }
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    /// <summary>
    /// Applies a fully verified staging area: places every changed file with a
    /// fresh file identity, removes extras, and writes the pack manifest last so
    /// an interrupted apply never leaves a pack that claims to be complete.
    /// </summary>
    private static void ApplyChanges(
        string packDir,
        ValidatedManifest manifest,
        IReadOnlyList<string> changedPaths,
        IReadOnlyList<string> extras,
        string stageAssets,
        string stageExtract,
        CancellationToken token)
    {
        foreach (var path in changedPaths)
        {
            if (IsPackManifestPath(path)) continue;
            token.ThrowIfCancellationRequested();
            PlaceStagedFile(
                packDir,
                path,
                ResolveStagedSource(manifest.AssetByCoveredPath[path], stageAssets, stageExtract, path));
        }

        foreach (var extra in extras)
        {
            token.ThrowIfCancellationRequested();
            var livePath = Path.Combine(packDir, extra.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(livePath)) File.Delete(livePath);
            DeleteEmptyParents(Path.GetDirectoryName(livePath), packDir);
        }

        foreach (var path in changedPaths)
        {
            if (!IsPackManifestPath(path)) continue;
            token.ThrowIfCancellationRequested();
            PlaceStagedFile(
                packDir,
                path,
                ResolveStagedSource(manifest.AssetByCoveredPath[path], stageAssets, stageExtract, path));
        }
    }

    private static string ResolveStagedSource(
        ValidatedAsset asset,
        string stageAssets,
        string stageExtract,
        string path) =>
        asset.Kind == PackAssetKind.Jar
            ? Path.Combine(stageAssets, asset.Name)
            : Path.Combine(stageExtract, path.Replace('/', Path.DirectorySeparatorChar));

    private static void PlaceStagedFile(string packDir, string relativePath, string stagedPath)
    {
        var livePath = Path.Combine(packDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
        // A pack update replaces hundreds of jars in a burst, and anything that
        // opens a file as it appears - a virus scanner above all - holds it just
        // long enough for the replacement to fail. Waiting a moment and trying
        // again is what makes the difference; the last attempt clears the way by
        // deleting first, which needs no handle of its own.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(stagedPath, livePath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException &&
                                       attempt < ReplaceAttempts && !Directory.Exists(livePath))
            {
                Thread.Sleep(ReplaceRetryDelay * attempt);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException &&
                                       File.Exists(livePath))
            {
                File.SetAttributes(livePath, FileAttributes.Normal);
                File.Delete(livePath);
                File.Move(stagedPath, livePath);
                return;
            }
        }
    }

    private void WriteSyncState(string statePath, string packDir, ValidatedManifest manifest)
    {
        var state = new SyncStateDto
        {
            SchemaVersion = SyncStateCacheGeneration,
            Revision = manifest.Revision
        };
        foreach (var file in manifest.Files)
        {
            var info = new FileInfo(Path.Combine(packDir, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            state.Files[file.Path] = new SyncStateFileDto
            {
                SizeBytes = info.Length,
                LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                Sha256 = file.Sha256
            };
        }
        AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    /// <summary>
    /// Writes the marker unless it already names this source; the rewrite is
    /// what retires a marker left pointing at the pack's former repository.
    /// </summary>
    private static void WriteSourceMarker(string packDir, PackSyncSource source)
    {
        var markerPath = Path.Combine(packDir, SourceMarkerFileName);
        if (TryReadSourceMarker(markerPath) == source) return;
        AtomicFile.WriteAllText(markerPath, JsonSerializer.Serialize(
            new SourceMarkerDto
            {
                SchemaVersion = SourceMarkerCacheGeneration,
                Owner = source.Owner,
                Repo = source.Repo,
                Tag = source.Tag
            },
            JsonOptions));
    }

    private static PackSyncSource? TryReadSourceMarker(string markerPath)
    {
        try
        {
            return File.Exists(markerPath) ? ReadSourceMarker(File.ReadAllText(markerPath)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static PackSyncSource? ReadSourceMarker(string json)
    {
        var marker = JsonSerializer.Deserialize<SourceMarkerDto>(json, JsonOptions);
        return marker is null ||
               marker.SchemaVersion != SourceMarkerCacheGeneration ||
               !IsValidSourceIdentifier(marker.Owner) ||
               !IsValidSourceIdentifier(marker.Repo) ||
               !IsValidSourceIdentifier(marker.Tag)
            ? null
            : new PackSyncSource(marker.Owner!, marker.Repo!, marker.Tag!);
    }

    private SyncStateDto ReadSyncState(string statePath)
    {
        try
        {
            if (File.Exists(statePath))
            {
                var state = JsonSerializer.Deserialize<SyncStateDto>(File.ReadAllText(statePath), JsonOptions);
                if (state?.SchemaVersion == SyncStateCacheGeneration)
                {
                    state.Files = new Dictionary<string, SyncStateFileDto>(state.Files, StringComparer.OrdinalIgnoreCase);
                    return state;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.Warn($"Pack sync state could not be read and will be rebuilt: {ex.Message}");
        }
        return new SyncStateDto();
    }

    private static ValidatedManifest ValidateRemoteManifest(RemoteManifestDto manifest, string packDir)
    {
        if (manifest.SchemaVersion != RemoteManifestCacheGeneration)
        {
            throw new InvalidDataException(
                $"Unsupported pack sync manifest schemaVersion {manifest.SchemaVersion}; " +
                $"expected {RemoteManifestCacheGeneration}.");
        }
        if (manifest.Files is not { Count: > 0 })
        {
            throw new InvalidDataException("Pack sync manifest lists no files.");
        }
        if (manifest.Assets is not { Count: > 0 })
        {
            throw new InvalidDataException("Pack sync manifest lists no assets.");
        }

        var revision = manifest.Revision?.Trim() ?? "";
        var packRoot = Path.GetFullPath(packDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var files = new List<ValidatedFile>(manifest.Files.Count);
        var filesByPath = new Dictionary<string, ValidatedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null)
            {
                throw new InvalidDataException("Pack sync manifest contains an empty file entry.");
            }
            var path = ValidateManifestPath(file.Path, packRoot);
            if (file.SizeBytes < 0)
            {
                throw new InvalidDataException($"Pack sync manifest file has a negative size: {path}");
            }
            var validated = new ValidatedFile(path, file.SizeBytes, ValidateSha256(file.Sha256, path));
            if (!filesByPath.TryAdd(path, validated))
            {
                throw new InvalidDataException($"Pack sync manifest lists a path twice: {path}");
            }
            files.Add(validated);
        }

        var assets = new List<ValidatedAsset>(manifest.Assets.Count);
        var assetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assetByCoveredPath = new Dictionary<string, ValidatedAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in manifest.Assets)
        {
            if (asset is null)
            {
                throw new InvalidDataException("Pack sync manifest contains an empty asset entry.");
            }
            var name = asset.Name?.Trim() ?? "";
            if (name is "" or "." or ".." ||
                !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) ||
                !IdentifierRegex().IsMatch(name))
            {
                throw new InvalidDataException($"Pack sync manifest asset name is invalid: {name}");
            }
            if (!assetNames.Add(name))
            {
                throw new InvalidDataException($"Pack sync manifest lists an asset twice: {name}");
            }
            var kind = asset.Kind switch
            {
                "jar" => PackAssetKind.Jar,
                "zipRoot" => PackAssetKind.ZipRoot,
                _ => throw new InvalidDataException($"Pack sync manifest asset {name} has an unsupported kind.")
            };
            if (asset.SizeBytes < 0)
            {
                throw new InvalidDataException($"Pack sync manifest asset has a negative size: {name}");
            }
            if (asset.Covers is not { Count: > 0 })
            {
                throw new InvalidDataException($"Pack sync manifest asset {name} covers no files.");
            }
            var covers = new List<string>(asset.Covers.Count);
            foreach (var covered in asset.Covers)
            {
                var coveredPath = covered ?? "";
                if (!filesByPath.TryGetValue(coveredPath, out var coveredFile) ||
                    !string.Equals(coveredFile.Path, coveredPath, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Pack sync manifest asset {name} covers an unknown path: {coveredPath}");
                }
                covers.Add(coveredPath);
            }
            if (kind == PackAssetKind.Jar && covers.Count != 1)
            {
                throw new InvalidDataException(
                    $"Pack sync manifest jar asset {name} must cover exactly one file.");
            }
            var validated = new ValidatedAsset(name, kind, ValidateSha256(asset.Sha256, name), asset.SizeBytes, covers);
            if (kind == PackAssetKind.Jar && !HashesEqual(validated.Sha256, filesByPath[covers[0]].Sha256))
            {
                throw new InvalidDataException(
                    $"Pack sync manifest jar asset {name} does not match its covered file.");
            }
            foreach (var coveredPath in covers)
            {
                if (!assetByCoveredPath.TryAdd(coveredPath, validated))
                {
                    throw new InvalidDataException($"Pack sync manifest covers a path twice: {coveredPath}");
                }
            }
            assets.Add(validated);
        }

        foreach (var file in files)
        {
            if (!assetByCoveredPath.ContainsKey(file.Path))
            {
                throw new InvalidDataException($"Pack sync manifest does not cover: {file.Path}");
            }
        }

        return new ValidatedManifest(revision, files, assets, filesByPath, assetByCoveredPath);
    }

    private static string ValidateManifestPath(string? value, string packRoot)
    {
        var path = value ?? "";
        if (path.Length is 0 or > 512 ||
            path.Contains('\\', StringComparison.Ordinal) ||
            Path.IsPathRooted(path))
        {
            throw new InvalidDataException($"Pack sync manifest path is invalid: {path}");
        }
        foreach (var segment in path.Split('/'))
        {
            if (segment is "." or ".." || !PathSegmentRegex().IsMatch(segment))
            {
                throw new InvalidDataException($"Pack sync manifest path is invalid: {path}");
            }
        }
        if (string.Equals(path, SourceMarkerFileName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, SyncStateFileName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, InstanceStateFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Pack sync manifest may not ship a service file: {path}");
        }
        var full = Path.GetFullPath(Path.Combine(packRoot, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(packRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Pack sync manifest path escapes the pack: {path}");
        }
        return path;
    }

    private static string ValidateSha256(string? value, string context)
    {
        var sha = value?.Trim() ?? "";
        if (sha.Length != 64 || sha.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"Pack sync manifest SHA-256 is invalid for {context}.");
        }
        return sha.ToLowerInvariant();
    }

    private static bool IsValidSourceIdentifier(string? value) =>
        value is { Length: >= 1 and <= 100 } &&
        value is not ("." or "..") &&
        IdentifierRegex().IsMatch(value);

    private static Uri BuildReleaseAssetUri(PackSyncSource source, string assetName, bool preventCaching = false)
    {
        var uri =
            $"https://github.com/{source.Owner}/{source.Repo}/releases/download/{source.Tag}/" +
            Uri.EscapeDataString(assetName);
        if (preventCaching)
        {
            uri += $"?cache={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        }
        return new Uri(uri, UriKind.Absolute);
    }

    private static TimeSpan DownloadTimeoutFor(long sizeBytes) =>
        DownloadSourceTimeout + TimeSpan.FromSeconds(sizeBytes / (128d * 1024d));

    private static bool IsApprovedEffectiveUri(Uri effectiveUri) =>
        effectiveUri.IsAbsoluteUri &&
        effectiveUri.Scheme == Uri.UriSchemeHttps &&
        effectiveUri.IsDefaultPort &&
        string.IsNullOrEmpty(effectiveUri.UserInfo) &&
        (effectiveUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         effectiveUri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
         effectiveUri.Host.Equals("github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
         effectiveUri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static bool IsPackManifestPath(string path) =>
        string.Equals(path, PackManifestService.ManifestFileName, StringComparison.OrdinalIgnoreCase);

    private static bool IsServiceRelativePath(string relativePath) =>
        string.Equals(relativePath, SourceMarkerFileName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(relativePath, SyncStateFileName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(relativePath, InstanceStateFileName, StringComparison.OrdinalIgnoreCase);

    private static bool IsProtectedRootName(string name) =>
        IsServiceRelativePath(name) ||
        PackInstanceService.InstanceOwnedDirectories.Contains(name) ||
        PackInstanceService.InstanceOwnedFiles.Contains(name);

    /// <summary>A sync in flight, or one interrupted and swept on the next start.</summary>
    private static bool IsStagingEntryName(string name) =>
        name.StartsWith(StagingDirectoryName, StringComparison.OrdinalIgnoreCase);

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool HashesEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool HasFreeSpace(string path, long required)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return true;
            return new DriveInfo(root).AvailableFreeSpace >= required;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string FormatMegabytes(long bytes) =>
        (bytes / (1024d * 1024d)).ToString("0", System.Globalization.CultureInfo.InvariantCulture);

    private static string SanitizeUri(Uri uri) => uri.IsAbsoluteUri
        ? uri.GetLeftPart(UriPartial.Path)
        : "<invalid-uri>";

    private static void DeleteEmptyParents(string? path, string stopAt)
    {
        var stop = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrWhiteSpace(path) &&
               !string.Equals(
                   Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar),
                   stop,
                   StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(path) || Directory.EnumerateFileSystemEntries(path).Any()) return;
            Directory.Delete(path);
            path = Path.GetDirectoryName(path);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Removes staging directories a killed earlier run left behind; nothing
    /// else ever deletes them.
    /// </summary>
    private static void SweepAbandonedEntries(string directory, string pattern)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, pattern))
            {
                if (Directory.Exists(entry))
                {
                    TryDeleteDirectory(entry);
                }
                else
                {
                    TryDeleteFile(entry);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex IdentifierRegex();

    // Manifest file paths carry real modpack names (spaces, '#', '$', '+',
    // quotes, brackets...), so unlike asset/source identifiers they only ban
    // what Windows cannot store: control and reserved characters, reserved
    // device names, and trailing dots or spaces.
    [GeneratedRegex("""^(?!(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$))[^\x00-\x1f<>:"/\\|?*]{1,255}(?<![. ])$""")]
    private static partial Regex PathSegmentRegex();

    private enum PackAssetKind
    {
        Jar,
        ZipRoot
    }

    private sealed record PackSyncDiff(
        string StateRevision,
        List<string> ChangedPaths,
        List<string> Extras);

    private sealed record ValidatedFile(string Path, long SizeBytes, string Sha256);

    private sealed record ValidatedAsset(
        string Name,
        PackAssetKind Kind,
        string Sha256,
        long SizeBytes,
        IReadOnlyList<string> Covers);

    private sealed record ValidatedManifest(
        string Revision,
        IReadOnlyList<ValidatedFile> Files,
        IReadOnlyList<ValidatedAsset> Assets,
        Dictionary<string, ValidatedFile> FilesByPath,
        Dictionary<string, ValidatedAsset> AssetByCoveredPath);

    private sealed class SourceMarkerDto
    {
        public int SchemaVersion { get; set; }
        public string? Owner { get; set; }
        public string? Repo { get; set; }
        public string? Tag { get; set; }
    }

    private sealed class RemoteManifestDto
    {
        public int SchemaVersion { get; set; }
        public string? PackId { get; set; }
        public string? Revision { get; set; }
        public List<RemoteFileDto>? Files { get; set; }
        public List<RemoteAssetDto>? Assets { get; set; }
    }

    private sealed class RemoteFileDto
    {
        public string? Path { get; set; }
        public long SizeBytes { get; set; }
        public string? Sha256 { get; set; }
    }

    private sealed class RemoteAssetDto
    {
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public string? Sha256 { get; set; }
        public long SizeBytes { get; set; }
        public List<string>? Covers { get; set; }
    }

    private sealed class SyncStateDto
    {
        public int SchemaVersion { get; set; }
        public string Revision { get; set; } = "";
        public Dictionary<string, SyncStateFileDto> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SyncStateFileDto
    {
        public long SizeBytes { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string Sha256 { get; set; } = "";
    }
}
