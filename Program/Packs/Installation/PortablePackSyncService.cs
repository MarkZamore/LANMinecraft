using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

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
    public IPackSourceCatalog? Catalog { get; set; }
    public const string SourceMarkerFileName = PackSourceResolver.MarkerFileName;
    public const string SyncStateFileName = ".portable-pack-sync.json";
    public const string ApplyPendingFileName = ".portable-pack-apply.json";
    public const string PackManifestAssetName = "pack-manifest.json";
    private const int MaximumManifestBytes = 8 * 1024 * 1024;
    public const string DefaultPackRelativePath = PackSyncSource.DefaultPackRelativePath;
    public const string LegacyTerraFirmaRelativePath = PackSyncSource.LegacyTerraFirmaRelativePath;
    public const string TerraFirmaRelativePath = PackSyncSource.TerraFirmaRelativePath;

    public static PackSyncSource TerraFirmaSource => PackSyncSource.TerraFirmaSource;
    public static PackSyncSource DefaultPackSource => PackSyncSource.DefaultPackSource;

    /// <summary>
    /// One entry from the embedded GitHub index snapshot, retained as an
    /// offline fallback and for callers without a session catalogue.
    /// </summary>
    public sealed record KnownPack(string RelativePath, PackSyncSource Source);

    /// <summary>
    /// Compatibility access to the embedded GitHub snapshot. The verified
    /// online or cached session index determines the active catalogue.
    /// </summary>
    public static IReadOnlyList<KnownPack> KnownPacks => EmbeddedKnownPacks.Value;

    private static readonly Lazy<IReadOnlyList<KnownPack>> EmbeddedKnownPacks = new(() =>
        Array.AsReadOnly(PackCatalogIndex.ReadEmbeddedSources()
            .Select(entry => new KnownPack(entry.RelativePath, entry.Source)).ToArray()));

    /// <summary>The source for a pack the launcher knows, or null for a custom one.</summary>
    public static PackSyncSource? KnownSourceFor(string? packRelativePath) => PackSourceResolver.KnownSourceFor(packRelativePath);

    internal const string OfflineCheckWarning =
        "Не удалось проверить обновления сборки — играем с локальной версией.";
    internal const string OfflineSyncWarning =
        "Не удалось обновить сборку — играем с локальной версией.";
    internal const string FirstInstallUnavailableMessage =
        "Сборка ещё не установлена, а сервер сборки недоступен. Проверьте интернет и попробуйте снова.";
    // Published pack manifests have a separate format from launcher-owned
    // caches. Version 2 makes required capabilities visible to old launchers:
    // their version-1 validator refuses the pack before installing its files.
    private const int LegacyRemoteManifestGeneration = 1;
    private const int FeatureManifestGeneration = 2;
    private static readonly HashSet<string> SupportedLauncherFeatures =
        new(["local-ai-native-v1", "local-ai-recipe-v1", "local-ai-blob-urls-v1", "curated-mod-list-v1", "launch-context-v1", "private-identity-asm-v1", "registry-player-skins-v1",
            "legacy-fabric-runtime-v1", "legacy-player-profiles-v1", "legacy-steam-v1",
            PackManifestService.NativeRuntimeFeature, PackManifestService.NativeSteamFeature, PackManifestService.NativeSharedRuntimeFeature,
            PackManifestService.Minecraft26WorldLayoutFeature, RuntimeLibrarySource.Feature, GameNoticeOverlayService.NativeFeature,
            PortableIdentityAdapterService.SteamNativePublicationMetadataFeature, PackManifestService.SteamWorldSharingFeature], StringComparer.Ordinal);
    // This local cache generation is independent of launcher releases and remote manifests.
    private const int SyncStateCacheGeneration = 1;
    private const string InstanceStateFileName = ".portable-instance.json";
    private const string StagingDirectoryName = ".pack-sync-stage";
    private const string DownloadPlanDirectoryPrefix = StagingDirectoryName + "-plan-";
    private const string DownloadPlanOwnershipFile = ".pack-sync-plan-owner";
    private const string DownloadPlanOwnership = "LANMinecraft.PackSync download plan v1\n";
    private const int MaximumParallelDownloads = 4;
    private const int ManifestFetchAttempts = 3;
    /// <summary>The pack's word to the launcher: preset, pack list, reset tokens.</summary>
    private const string LauncherDataPrefix = "launcher/";
    /// <summary>A ceiling that keeps this to text files, never a pack's jars.</summary>
    internal const long MaximumLauncherDataBytes = 8L * 1024 * 1024;
    private sealed record PresetPreview(ControlsPreset? Preset);
    private readonly ConcurrentDictionary<string, PresetPreview> _presetPreviews = new(StringComparer.OrdinalIgnoreCase);

    internal bool TryGetControlsPresetPreview(string packRelativePath, out ControlsPreset? preset)
    {
        var hasPreview = _presetPreviews.TryGetValue(packRelativePath, out var entry);
        preset = entry?.Preset;
        return hasPreview;
    }

    private bool RememberPresetPreview(string packRelativePath, ControlsPreset? preset)
    {
        if (_presetPreviews.TryGetValue(packRelativePath, out var previous) &&
            string.Equals(previous.Preset?.Sha256, preset?.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        _presetPreviews[packRelativePath] = new PresetPreview(preset);
        return true;
    }
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

    /// <summary>An interrupted write must be repaired before any local launch.</summary>
    public static bool IsUpdatePending(string packDirectory) =>
        File.Exists(Path.Combine(packDirectory, ApplyPendingFileName));

    /// <summary>
    /// The authoritative active paths of a synced pack. Missing state means a
    /// custom/legacy pack; damaged existing state must never activate unknown
    /// files as if the pack were custom.
    /// </summary>
    internal static IReadOnlySet<string>? ReadManagedPaths(string packDirectory) =>
        ReadManagedFiles(packDirectory)?.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<ManagedFileSnapshot>? ReadManagedFiles(
        string packDirectory, IReadOnlyList<string>? requiredFeatures = null)
    {
        var path = Path.Combine(packDirectory, SyncStateFileName);
        if (!File.Exists(path)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<SyncStateDto>(File.ReadAllText(path), JsonOptions);
            if (state?.SchemaVersion != SyncStateCacheGeneration || state.Files is not { Count: > 0 })
                throw new InvalidDataException("Pack sync state has no valid managed file snapshot.");
            if (requiredFeatures is not null && requiredFeatures.Any(feature =>
                    state.RequiredLauncherFeatures?.Contains(feature, StringComparer.Ordinal) != true))
                throw new InvalidDataException("The verified installation does not declare the required launcher features.");
            var packRoot = Path.GetFullPath(packDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var files = new List<ManagedFileSnapshot>(state.Files.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (relative, file) in state.Files)
            {
                var managedPath = ValidateManifestPath(relative, packRoot);
                if (!seen.Add(managedPath) || file is null || file.SizeBytes < 0)
                    throw new InvalidDataException("Pack sync state contains an invalid managed file.");
                files.Add(new ManagedFileSnapshot(managedPath, file.SizeBytes, ValidateSha256(file.Sha256, managedPath)));
            }
            return files;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Pack sync state is damaged; repair the pack before launching.", ex);
        }
    }

    internal sealed record ManagedFileSnapshot(string Path, long SizeBytes, string Sha256);

    internal static IReadOnlyList<ManagedFileSnapshot> ReadInstalledSnapshot(AppPaths paths, string packDirectory)
    {
        if (IsUpdatePending(packDirectory))
            throw new IOException("Обновление сборки было прервано. Для игры нужна полная копия.");
        RequireOrdinarySnapshotFile(paths, packDirectory, SyncStateFileName);
        RequireOrdinarySnapshotFile(paths, packDirectory, PackManifestService.ManifestFileName);
        var files = ReadManagedFiles(packDirectory) ??
            throw new IOException("В локальной сборке нет списка установленных файлов.");
        var features = ReadInstalledSnapshotFeatures(packDirectory);
        var descriptor = PackManifestService.Load(packDirectory);
        RequireSnapshotEntry(files, PackManifestService.ManifestFileName);
        PackManifestService.VerifySharedRuntimeOverlay(descriptor, files.Select(file => file.Path),
            features.Contains(PackManifestService.NativeSharedRuntimeFeature, StringComparer.Ordinal));
        var bundledClient = descriptor.NativeRuntime?.Executable ?? descriptor.ClientJar;
        if (descriptor.NativeRuntime?.SharedRuntime is null && !string.IsNullOrEmpty(bundledClient))
            RequireSnapshotEntry(files, bundledClient);
        return files;
    }

    private static void RequireSnapshotEntry(IReadOnlyList<ManagedFileSnapshot> files, string path)
    {
        if (!files.Any(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            throw new IOException($"Локальная сборка не содержит обязательный файл: {path}");
    }

    private static async Task<IReadOnlyList<ManagedFileSnapshot>> VerifyInstalledSnapshotAsync(
        AppPaths paths, string packDirectory, bool verifySharedRuntime, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var files = ReadInstalledSnapshot(paths, packDirectory);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var path = RequireOrdinarySnapshotFile(paths, packDirectory, file.Path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 128 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
            if (input.Length != file.SizeBytes ||
                !HashesEqual(Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)), file.Sha256))
                throw new IOException($"Файл локальной сборки изменён или повреждён: {file.Path}");
        }
        if (verifySharedRuntime)
        {
            var descriptor = PackManifestService.Load(packDirectory);
            if (descriptor.NativeRuntime?.SharedRuntime is { } shared)
                using (new SharedNativeRuntimeStore(paths).Open(shared, descriptor.MinecraftVersion, token)) { }
        }
        token.ThrowIfCancellationRequested();
        if (IsUpdatePending(packDirectory))
            throw new IOException("Локальная сборка изменяется. Повторите попытку.");
        return files;
    }

    private static string RequireOrdinarySnapshotFile(AppPaths paths, string packDirectory, string relative)
    {
        var path = Path.Combine(packDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!MaintenancePath.TryRead(paths, path, out var attributes) || attributes.HasFlag(FileAttributes.Directory))
            throw new IOException($"Файл локальной сборки отсутствует или недоступен: {relative}");
        return path;
    }

    internal static string[] ReadInstalledSnapshotFeatures(string packDirectory)
    {
        var path = Path.Combine(packDirectory, SyncStateFileName);
        try
        {
            var state = JsonSerializer.Deserialize<SyncStateDto>(File.ReadAllText(path), JsonOptions);
            if (state?.SchemaVersion != SyncStateCacheGeneration || state.Files is not { Count: > 0 })
                throw new InvalidDataException("Локальная сборка не содержит проверенного списка файлов.");
            var features = state.RequiredLauncherFeatures ?? [];
            ValidateSnapshotFeatures(features);
            return features;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Список установленных файлов локальной сборки повреждён.", ex);
        }
    }

    private static void ValidateSnapshotFeatures(IReadOnlyList<string> features)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (features.Any(feature => !SupportedLauncherFeatures.Contains(feature) || !seen.Add(feature)))
            throw new InvalidDataException("Локальная сборка требует неподдерживаемую функцию лаунчера.");
    }

    // The transfer manifest is validated independently and never supplies a
    // receipt or source marker. Rebuild the local receipt from ordinary files
    // in the receiver's staging directory.
    internal static void WriteImportedSnapshot(string packDirectory,
        IReadOnlyList<ManagedFileSnapshot> files, IReadOnlyList<string> requiredFeatures)
    {
        ValidateSnapshotFeatures(requiredFeatures);
        RequireSnapshotEntry(files, PackManifestService.ManifestFileName);
        var descriptor = PackManifestService.Load(packDirectory);
        PackManifestService.VerifySharedRuntimeOverlay(descriptor, files.Select(file => file.Path),
            requiredFeatures.Contains(PackManifestService.NativeSharedRuntimeFeature, StringComparer.Ordinal));
        var bundledClient = descriptor.NativeRuntime?.Executable ?? descriptor.ClientJar;
        if (descriptor.NativeRuntime?.SharedRuntime is null && !string.IsNullOrEmpty(bundledClient))
            RequireSnapshotEntry(files, bundledClient);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packDirectory));
        var state = new SyncStateDto
        {
            SchemaVersion = SyncStateCacheGeneration,
            HasCompleteLauncherPaths = true,
            RequiredLauncherFeatures = requiredFeatures.Count == 0 ? null : [.. requiredFeatures]
        };
        foreach (var file in files)
        {
            var relative = ValidateManifestPath(file.Path, root);
            var info = new FileInfo(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!info.Exists || info.Length != file.SizeBytes || !state.Files.TryAdd(relative, new SyncStateFileDto
                {
                    SizeBytes = file.SizeBytes,
                    LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                    Sha256 = ValidateSha256(file.Sha256, relative)
                }))
                throw new InvalidDataException($"Недействительный файл локальной сборки: {relative}");
        }
        AtomicFile.WriteAllText(Path.Combine(root, SyncStateFileName), JsonSerializer.Serialize(state, JsonOptions));
    }

    /// <summary>Historical ownership proofs, including older installations without instance state.</summary>
    internal static IReadOnlyList<ManagedFileSnapshot> ReadRetiredFiles(string packDirectory)
    {
        var path = Path.Combine(packDirectory, SyncStateFileName);
        if (!File.Exists(path)) return [];
        try
        {
            var state = JsonSerializer.Deserialize<SyncStateDto>(File.ReadAllText(path), JsonOptions);
            if (state?.SchemaVersion != SyncStateCacheGeneration)
                throw new InvalidDataException("Pack sync state has an unsupported managed file snapshot.");
            var packRoot = Path.GetFullPath(packDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return (state.RetiredFiles ?? []).Select(file => new ManagedFileSnapshot(
                ValidateManifestPath(file.Path, packRoot),
                file.SizeBytes >= 0 ? file.SizeBytes : throw new InvalidDataException("Negative retired file size."),
                ValidateSha256(file.Sha256, file.Path ?? "")))
                .Where(file => !IsProtectedRootName(file.Path.Split('/')[0])).ToArray();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Pack sync state is damaged; repair the pack before launching.", ex);
        }
    }

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

    /// <summary>Uses the same source-selection rules as catalog preflight.</summary>
    public PackSyncSource? TryResolveSource(string packRelativePath) =>
        PackSourceResolver.Resolve(_paths, _logger, packRelativePath, Catalog);

    /// <summary>
    /// Stamps the built-in source on the default pack folder. A marker naming
    /// anyone else - or one this build cannot read - is the player's own and
    /// stays, so a custom pack is never repointed at the built-in release.
    /// </summary>
    public void EnsureDefaultSourceMarker(string packRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packRelativePath);
        using var access = PackAccess.RequireAvailable(_paths, [packRelativePath]);
        var catalog = Catalog;
        if (catalog?.IsWithdrawn(packRelativePath) == true) return;
        var source = catalog is null ? DefaultPackSource : catalog.SourceFor(packRelativePath);
        if (source is null) return;
        var packDir = _paths.CombineUnderPacks(packRelativePath);
        var markerPath = Path.Combine(packDir, SourceMarkerFileName);
        if (File.Exists(markerPath))
        {
            var marker = PackSourceResolver.TryReadInstalledMarker(markerPath);
            if (marker is null || !PackSyncSource.IsLegacyDefaultSource(marker)) return;
        }
        PackSourceResolver.WriteMarker(packDir, source);
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
    /// <summary>
    /// What a pack weighs, read from the manifest it publishes rather than from
    /// a folder - which is the only way to answer for a pack nobody has
    /// downloaded yet.
    /// </summary>
    /// <remarks>
    /// Until this existed, a build that was offered but not installed had no
    /// weight at all, and the memory rules fell back to giving it two thirds of
    /// the machine. On a 32 GB machine that put "от 13 ГБ" under a pack built to
    /// run on eight - more than the 880-mod pack beside it asks for, which is
    /// exactly backwards and is what a player notices.
    ///
    /// The manifest names every file with its size, so the mod jars can be
    /// counted and weighed without fetching one of them. What it cannot say is
    /// how many mods those jars carry inside themselves - a large build's 882
    /// files are 1128 mods - so this under-counts, and knowingly: an answer a
    /// little low for a pack that is about to be downloaded and weighed
    /// properly is worth having, and two thirds of the machine never was.
    /// </remarks>
    public async Task<PackMemoryProfile> WeighFromSourceAsync(string packRelativePath, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(packRelativePath)) return PackMemoryProfile.Unknown;
        var source = TryResolveSource(packRelativePath);
        if (source is null) return PackMemoryProfile.Unknown;

        string? metadataStage = null;
        FileStream? metadataLease = null;
        try
        {
            var dto = await FetchManifestAsync(source, token).ConfigureAwait(false);
            var files = (dto.Files ?? [])
                .Where(file => !string.IsNullOrWhiteSpace(file.Path))
                .Select(file => (file.Path!, file.SizeBytes));
            var profile = PackMemoryProfile.FromPublishedFiles(files, dto.MinecraftVersion, dto.ModCount);
            if (!profile.IsKnown || !(dto.Files ?? []).Any(file => file.Path == "launcher/local-ai.json")) return profile;

            // Source weighing must also work before installation. Only the
            // small declared launcher data is fetched, never a JAR or model.
            var packDir = _paths.CombineUnderPacks(packRelativePath);
            var manifest = ValidateRemoteManifest(dto, packDir);
            metadataStage = Path.Combine(_paths.Launcher, DownloadPlanDirectoryPrefix + Guid.NewGuid().ToString("N"));
            metadataLease = CreateDownloadPlanLease(metadataStage);
            var metadata = await PrepareLauncherMetadataSnapshotAsync(source, packDir, manifest, metadataStage,
                token, launcherAssetsOnly: true).ConfigureAwait(false);
            var requirements = LocalAiProvisioningService.ReadRequirements(metadata.Directory)
                ?? throw new InvalidDataException("The published local AI descriptor is missing from its snapshot.");
            if (!manifest.FilesByPath.TryGetValue(requirements.ModPath, out var mod) ||
                !HashesEqual(requirements.ModSha256, mod.Sha256))
                throw new InvalidDataException("The published local AI descriptor does not match its mod JAR.");
            _ = LocalAiRecipeReader.Read(metadata.Directory, requirements);
            return profile with { LocalAiReserveMb = requirements.ReserveMemoryMb };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return PackMemoryProfile.Unknown;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException
                                       or UnauthorizedAccessException or NotSupportedException)
        {
            // Silent by design: this is a number beside a field, and a pack that
            // cannot be weighed simply keeps the older, pack-blind answer.
            _logger.Warn($"Pack {packRelativePath} could not be weighed from its manifest ({ex.Message}).");
            return PackMemoryProfile.Unknown;
        }
        finally
        {
            metadataLease?.Dispose();
            if (metadataStage is not null) TryDeleteDownloadPlanDirectory(_paths.Launcher, metadataStage);
        }
    }

    /// <summary>
    /// Checks for a fresh preset without changing a managed installation.
    /// Legacy packs without an install receipt keep their small-file refresh.
    /// </summary>
    /// <returns>True when launcher data or its in-memory preset preview changed.</returns>
    public async Task<bool> RefreshLauncherDataAsync(string packRelativePath, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packRelativePath);
        try
        {
            using var access = await PackAccess.AcquireAsync(_paths, packRelativePath, token).ConfigureAwait(false);
            return await RefreshLauncherDataUnderAccessAsync(packRelativePath, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> RefreshLauncherDataUnderAccessAsync(string packRelativePath, CancellationToken token)
    {
        if (PackAccess.IsRunning(_paths, [packRelativePath], _logger)) return false;
        var source = TryResolveSource(packRelativePath);
        if (source is null) return false;
        var packDir = _paths.CombineUnderPacks(packRelativePath);
        if (!PackManifestService.HasManifest(packDir)) return false;

        var staging = Path.Combine(packDir, DownloadPlanDirectoryPrefix + Guid.NewGuid().ToString("N"));
        FileStream? stagingLease = null;
        try
        {
            var dto = await FetchManifestAsync(source, token).ConfigureAwait(false);
            var manifest = ValidateRemoteManifest(dto, packDir);
            var remotePreset = manifest.Files.FirstOrDefault(file =>
                string.Equals(file.Path, ControlsPresetService.PresetRelativePath, StringComparison.OrdinalIgnoreCase));
            var previewOnly = ExistsForCleanup(Path.Combine(packDir, SyncStateFileName));
            var presetPath = ControlsPresetService.GetPresetPath(packDir);
            if (previewOnly)
            {
                // Launcher metadata belongs to the same verified receipt as the
                // rest of the installed pack. A later preset can be shown before
                // Play without moving new bytes into an older offline snapshot.
                if (remotePreset is null)
                    return File.Exists(presetPath)
                        ? RememberPresetPreview(packRelativePath, null)
                        : _presetPreviews.TryRemove(packRelativePath, out _);
                if (remotePreset.SizeBytes > MaximumLauncherDataBytes) return false;
                if (HasFileWithHash(packDir, remotePreset)) return _presetPreviews.TryRemove(packRelativePath, out _);
                if (_presetPreviews.TryGetValue(packRelativePath, out var preview) && preview.Preset is { } cached &&
                    HashesEqual(cached.Sha256, remotePreset.Sha256)) return false;
            }
            else _presetPreviews.TryRemove(packRelativePath, out _);

            IEnumerable<ValidatedFile> candidates = previewOnly
                ? [remotePreset!]
                : manifest.Files.Where(file => file.Path.StartsWith(LauncherDataPrefix, StringComparison.Ordinal));
            var wanted = candidates
                .Where(file => !HasFileWithHash(packDir, file))
                .ToList();
            var removePreset = remotePreset is null && File.Exists(presetPath);
            if (wanted.Count == 0 && !removePreset) return false;

            var assets = wanted
                .Select(file => manifest.AssetByCoveredPath[file.Path])
                .DistinctBy(asset => asset.Name)
                .ToList();
            // Only ever the small ones: this runs on a window opening, not on a
            // launch, and a pack's mod jars have no business here.
            if (assets.Sum(asset => asset.SizeBytes) > MaximumLauncherDataBytes) return false;

            var stageAssets = Path.Combine(staging, "assets");
            var stageExtract = Path.Combine(staging, "extract");
            stagingLease = CreateDownloadPlanLease(staging);
            Directory.CreateDirectory(stageAssets);
            Directory.CreateDirectory(stageExtract);
            foreach (var asset in assets)
            {
                await DownloadAssetAsync(source, asset, Path.Combine(stageAssets, asset.Name), _ => { }, token: token)
                    .ConfigureAwait(false);
            }
            ExtractAndVerifyStagedSources(
                manifest,
                assets,
                wanted.Select(file => file.Path).ToList(),
                stageAssets,
                stageExtract,
                token);

            token.ThrowIfCancellationRequested();
            if (previewOnly)
            {
                var preset = remotePreset!;
                var staged = ResolveStagedSource(manifest.AssetByCoveredPath[preset.Path], stageAssets, stageExtract, preset.Path);
                var parsed = new ControlsPresetService(_logger).TryLoadPath(staged);
                return parsed is not null && RememberPresetPreview(packRelativePath, parsed);
            }

            foreach (var file in wanted)
            {
                var staged = ResolveStagedSource(manifest.AssetByCoveredPath[file.Path], stageAssets, stageExtract, file.Path);
                PlaceStagedFile(packDir, file.Path, staged);
            }
            // Deletion is applied only after every replacement has been downloaded
            // and verified, so an unavailable or damaged release keeps the preset.
            token.ThrowIfCancellationRequested();
            if (removePreset) File.Delete(presetPath);
            _logger.Info($"Pack {packRelativePath}: {wanted.Count} launcher file(s) refreshed, " +
                         $"{(removePreset ? 1 : 0)} preset(s) removed ahead of a full sync.");
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
            stagingLease?.Dispose();
            TryDeleteDownloadPlanDirectory(packDir, staging);
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
        using var plan = await PrepareDownloadPlanAsync(packRelativePath, progress, token).ConfigureAwait(false);
        return await SyncAsync(plan, progress, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Selects the update once, measures its remaining payload, and obtains a
    /// verified small launcher-data snapshot before the large downloads begin.
    /// </summary>
    public async Task<PackSyncDownloadPlan> PrepareDownloadPlanAsync(
        string packRelativePath,
        IProgress<RuntimePreparationProgress>? progress,
        CancellationToken token)
    {
        var access = await PackAccess.AcquireAsync(_paths, packRelativePath, token).ConfigureAwait(false);
        try
        {
            var plan = await PrepareDownloadPlanUnderAccessAsync(packRelativePath, progress, access, token).ConfigureAwait(false);
            plan.OwnedAccess = access;
            return plan;
        }
        catch
        {
            access.Dispose();
            throw;
        }
    }

    internal async Task<PackSyncDownloadPlan> PrepareDownloadPlanUnderAccessAsync(
        string packRelativePath,
        IProgress<RuntimePreparationProgress>? progress,
        PackAccess.Lease access,
        CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packRelativePath);
        access.RequireOwner(_paths, packRelativePath);
        var source = TryResolveSource(packRelativePath);
        var packDir = _paths.CombineUnderPacks(packRelativePath);
        if (source is null)
        {
            Func<string, string?>? installedFiles = null;
            if (Catalog?.IsWithdrawn(packRelativePath) == true)
            {
                if (!Catalog.IsRetiredInstalled(packRelativePath))
                    throw new IOException("Сборка снята с публикации и не установлена полностью.");
                installedFiles = await VerifyOfflineInstallationAsync(packDir, token).ConfigureAwait(false);
            }
            else if (ReadSyncState(Path.Combine(packDir, SyncStateFileName)).RequiredLauncherFeatures?
                .Contains(PackManifestService.NativeSharedRuntimeFeature, StringComparer.Ordinal) == true)
            {
                // A transferred pack carries the verified overlay and its
                // immutable runtime pin, even without a published pack source.
                var files = await VerifyInstalledSnapshotAsync(_paths, packDir, verifySharedRuntime: false, token)
                    .ConfigureAwait(false);
                var descriptor = PackManifestService.Load(packDir);
                var shared = descriptor.NativeRuntime!.SharedRuntime!;
                var hashes = files.ToDictionary(file => file.Path, file => file.Sha256, StringComparer.OrdinalIgnoreCase);
                installedFiles = path => hashes.GetValueOrDefault(ValidateManifestPath(path, packDir));
                if (!new SharedNativeRuntimeStore(_paths).IsInstalled(shared, descriptor.MinecraftVersion, token))
                    return new PackSyncDownloadPlan(this, new SharedRuntimeRecovery(descriptor), packRelativePath,
                        packDir, packDir, shared.Archive.SizeBytes, installedFiles);
            }
            return LocalDownloadPlan(packRelativePath, packDir,
                new PackSyncResult(PackSyncOutcome.SkippedNoSource, null, 0, 0, null), installedFiles);
        }

        EnsureGameStopped(packRelativePath);
        TryDeleteLegacyApplyStage(packDir);
        var hadManifest = PackManifestService.HasManifest(packDir);
        var repairingStructure = IsUpdatePending(packDir);
        progress?.Report(new RuntimePreparationProgress(
            repairingStructure ? RuntimePreparationStage.RepairingPackStructure : RuntimePreparationStage.SyncingPack,
            repairingStructure ? "Восстанавливаем файлы сборки" : "Проверка сборки"));

        ValidatedManifest manifest;
        try
        {
            var dto = await FetchManifestAsync(source, token).ConfigureAwait(false);
            manifest = ValidateRemoteManifest(dto, packDir, token);
            var revisionForLog = manifest.Revision is { Length: > 160 }
                ? manifest.Revision[..160] + "..." : manifest.Revision;
            _logger.Info($"Pack {packRelativePath}: verified launch manifest revision " +
                         JsonSerializer.Serialize(revisionForLog) + ".");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException)
        {
            _logger.Warn($"Pack sync manifest for {packRelativePath} is unavailable or invalid: {ex.Message}");
            if (IsUpdatePending(packDir))
                throw new PackApplyFailedException(new IOException(
                    "Обновление сборки было прервано. Подключитесь к интернету и повторите «Играть».", ex));
            if (hadManifest)
            {
                return await PrepareOfflinePlanAsync(packRelativePath, packDir, OfflineCheckWarning, token).ConfigureAwait(false);
            }
            throw new InvalidOperationException(FirstInstallUnavailableMessage, ex);
        }

        var stageRoot = Path.Combine(packDir, DownloadPlanDirectoryPrefix + Guid.NewGuid().ToString("N"));
        FileStream? stageLease = null;
        try
        {
            stageLease = CreateDownloadPlanLease(stageRoot);
            PackSyncDiff diff;
            try
            {
                diff = await Task.Run(() => ComputeDiff(packDir,
                    Path.Combine(packDir, SyncStateFileName), manifest, token), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PackApplyFailedException(ex);
            }
            var metadata = await PrepareLauncherMetadataSnapshotAsync(source, packDir, manifest, stageRoot, token)
                .ConfigureAwait(false);
            var changed = diff.ChangedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var prefetched = metadata.PrefetchedAssetNames;
            PackRuntimeDescriptor? runtimeDescriptor = null;
            long sharedBytes = 0;
            if (manifest.RequiredLauncherFeatures.Contains(PackManifestService.NativeSharedRuntimeFeature))
            {
                runtimeDescriptor = PackManifestService.Load(metadata.Directory);
                PackManifestService.VerifySharedRuntimeOverlay(runtimeDescriptor, manifest.FilesByPath.Keys, hasSharedRuntimeFeature: true);
                var shared = runtimeDescriptor.NativeRuntime!.SharedRuntime!;
                if (!new SharedNativeRuntimeStore(_paths).IsInstalled(shared, runtimeDescriptor.MinecraftVersion, token))
                    sharedBytes = shared.Archive.SizeBytes;
            }
            var pendingBytes = manifest.Assets.Where(asset => asset.Covers.Any(changed.Contains) && !prefetched.Contains(asset.Name))
                .Aggregate(sharedBytes, (sum, asset) => checked(sum + asset.SizeBytes));
            var state = new PreparedSyncState(source, manifest, diff, hadManifest, stageRoot, prefetched, runtimeDescriptor, sharedBytes);
            var ownership = stageLease;
            var plan = new PackSyncDownloadPlan(this, state, packRelativePath, packDir, metadata.Directory, pendingBytes,
                path => manifest.FilesByPath.TryGetValue(ValidateManifestPath(path, packDir), out var file) ? file.Sha256 : null,
                cleanup: () =>
                {
                    ownership.Dispose();
                    TryDeleteDownloadPlanDirectory(packDir, stageRoot);
                });
            stageLease = null;
            return plan;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            stageLease?.Dispose();
            TryDeleteDownloadPlanDirectory(packDir, stageRoot);
            throw;
        }
        catch (Exception ex) when (hadManifest && !IsUpdatePending(packDir) &&
                                   ex is HttpRequestException or IOException or InvalidDataException
                                       or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            stageLease?.Dispose();
            TryDeleteDownloadPlanDirectory(packDir, stageRoot);
            var plan = await PrepareOfflinePlanAsync(packRelativePath, packDir, OfflineSyncWarning, token).ConfigureAwait(false);
            _logger.Warn($"Pack sync for {packRelativePath} failed; playing the checked local version: {ex.Message}");
            return plan;
        }
        catch
        {
            stageLease?.Dispose();
            TryDeleteDownloadPlanDirectory(packDir, stageRoot);
            throw;
        }
    }

    private async Task<PreparedLauncherMetadata> PrepareLauncherMetadataSnapshotAsync(PackSyncSource source,
        string packDir, ValidatedManifest manifest, string stageRoot, CancellationToken token,
        bool launcherAssetsOnly = false)
    {
        var metadataDirectory = Path.Combine(stageRoot, "metadata");
        var stageAssets = Path.Combine(stageRoot, "assets");
        var stageExtract = Path.Combine(stageRoot, "extract");
        Directory.CreateDirectory(metadataDirectory);
        var hasSharedDescriptor = !launcherAssetsOnly && manifest.RequiredLauncherFeatures.Contains(PackManifestService.NativeSharedRuntimeFeature);
        if (hasSharedDescriptor && (!manifest.AssetByCoveredPath.TryGetValue(PackManifestService.ManifestFileName, out var descriptorAsset) ||
            descriptorAsset.Kind != PackAssetKind.ZipRoot || descriptorAsset.Covers.Count != 1))
            throw new InvalidDataException("The verified shared native runtime descriptor must have its own ZIP asset.");
        var metadata = manifest.Files.Where(file => file.Path.StartsWith(LauncherDataPrefix, StringComparison.Ordinal) ||
            hasSharedDescriptor && file.Path == PackManifestService.ManifestFileName).ToList();
        if (metadata.Sum(file => file.SizeBytes) > MaximumLauncherDataBytes)
            throw new InvalidDataException("Pack launcher metadata exceeds its byte limit.");
        var missing = metadata.Where(file => !HasFileWithHash(packDir, file)).ToList();
        var metadataAssets = missing.Select(file => manifest.AssetByCoveredPath[file.Path])
            .DistinctBy(asset => asset.Name).ToList();
        if (metadataAssets.Sum(asset => asset.SizeBytes) > MaximumLauncherDataBytes ||
            launcherAssetsOnly && metadataAssets.Any(asset => asset.Covers.Any(path => !path.StartsWith(LauncherDataPrefix, StringComparison.Ordinal))))
            throw new InvalidDataException("Pack launcher metadata assets exceed their byte or content limit.");
        Directory.CreateDirectory(stageAssets);
        Directory.CreateDirectory(stageExtract);
        foreach (var asset in metadataAssets)
        {
            await DownloadAssetAsync(source, asset, Path.Combine(stageAssets, asset.Name), _ => { }, token: token)
                .ConfigureAwait(false);
            if (hasSharedDescriptor && asset.Covers.Contains(PackManifestService.ManifestFileName, StringComparer.Ordinal))
            {
                using var archiveBytes = File.OpenRead(Path.Combine(stageAssets, asset.Name));
                PackArchiveService.CheckCentralDirectory(archiveBytes, minimumEntries: 1);
                archiveBytes.Position = 0;
                using var descriptorZip = new ZipArchive(archiveBytes, ZipArchiveMode.Read);
                if (descriptorZip.Entries.Count != 1 || descriptorZip.Entries[0] is not { } entry ||
                    entry.FullName != PackManifestService.ManifestFileName ||
                    entry.Length != manifest.FilesByPath[PackManifestService.ManifestFileName].SizeBytes ||
                    (entry.ExternalAttributes & (int)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                    ((entry.ExternalAttributes >> 16) & 0xf000) is not (0 or 0x8000))
                    throw new InvalidDataException("Invalid shared native runtime descriptor ZIP.");
            }
            if (launcherAssetsOnly && asset.Kind == PackAssetKind.ZipRoot)
            {
                using var archive = ZipFile.OpenRead(Path.Combine(stageAssets, asset.Name));
                var paths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in archive.Entries.Where(entry => !entry.FullName.EndsWith('/')))
                    if (!paths.Add(entry.FullName) || !asset.Covers.Contains(entry.FullName, StringComparer.Ordinal) ||
                        !manifest.FilesByPath.TryGetValue(entry.FullName, out var file) || entry.Length != file.SizeBytes)
                        throw new InvalidDataException("Source weighing requires an archive containing only its declared launcher metadata.");
            }
        }
        ExtractAndVerifyStagedSources(manifest, metadataAssets,
            missing.Select(file => file.Path).ToList(), stageAssets, stageExtract, token);
        var missingPaths = missing.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in metadata)
        {
            token.ThrowIfCancellationRequested();
            var sourcePath = missingPaths.Contains(file.Path)
                ? ResolveStagedSource(manifest.AssetByCoveredPath[file.Path], stageAssets, stageExtract, file.Path)
                : Path.Combine(packDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
            var snapshotPath = Path.Combine(metadataDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            File.Copy(sourcePath, snapshotPath);
            if (!HasFileWithHash(metadataDirectory, file))
                throw new InvalidDataException("Pack launcher metadata changed while its snapshot was prepared.");
        }
        return new PreparedLauncherMetadata(metadataDirectory,
            metadataAssets.Select(asset => asset.Name).ToHashSet(StringComparer.Ordinal));
    }

    private async Task<PackSyncDownloadPlan> PrepareOfflinePlanAsync(
        string relativePath, string packDir, string warning, CancellationToken token)
    {
        var installedFiles = await VerifyOfflineInstallationAsync(packDir, token).ConfigureAwait(false);
        return LocalDownloadPlan(relativePath, packDir,
            new PackSyncResult(PackSyncOutcome.OfflineFallback, null, 0, 0, warning), installedFiles);
    }

    private async Task<Func<string, string?>> VerifyOfflineInstallationAsync(string packDir, CancellationToken token)
    {
        try
        {
            var files = await VerifyInstalledSnapshotAsync(_paths, packDir, verifySharedRuntime: true, token).ConfigureAwait(false);
            var hashes = files.ToDictionary(file => file.Path, file => file.Sha256, StringComparer.OrdinalIgnoreCase);
            return path => hashes.GetValueOrDefault(ValidateManifestPath(path, packDir));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new IOException("Локальную версию сборки не удалось проверить. Подключитесь к интернету, чтобы восстановить файлы. "
                + ex.Message, ex);
        }
    }

    private PackSyncDownloadPlan LocalDownloadPlan(string relativePath, string packDir, PackSyncResult result,
        Func<string, string?>? verifiedFileSha256)
    {
        if (IsUpdatePending(packDir))
            throw new PackApplyFailedException(new IOException("Обновление сборки было прервано. Повторите «Играть»."));
        return new PackSyncDownloadPlan(this, null, relativePath, packDir, packDir, 0, verifiedFileSha256 ?? ReadLocalHash, result);

        string? ReadLocalHash(string path)
        {
            var relative = ValidateManifestPath(path, packDir);
            var file = Path.Combine(packDir, relative.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(file) ? HashFile(file) : null;
        }
    }

    /// <summary>Consumes exactly the update selected by PrepareDownloadPlanAsync.</summary>
    public async Task<PackSyncResult> SyncAsync(PackSyncDownloadPlan plan,
        IProgress<RuntimePreparationProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.ThrowIfDisposed();
        if (!ReferenceEquals(plan.Owner, this)) throw new ArgumentException("Pack download plan belongs to another service.", nameof(plan));
        if (plan.Consumed) throw new InvalidOperationException("Pack download plan has already been consumed.");
        plan.Consumed = true;
        token.ThrowIfCancellationRequested();
        if (plan.ImmediateResult is { } immediate)
        {
            _presetPreviews.TryRemove(plan.PackRelativePath, out _);
            return immediate;
        }
        if (plan.State is SharedRuntimeRecovery recovery)
        {
            EnsureGameStopped(plan.PackRelativePath);
            var downloaded = await PrepareSharedRuntimeAsync(recovery.Descriptor, packBytes: 0, progress, token)
                .ConfigureAwait(false);
            return new PackSyncResult(PackSyncOutcome.UpToDate, null, 0, downloaded, null);
        }
        var prepared = (PreparedSyncState)plan.State!;
        EnsureGameStopped(plan.PackRelativePath);
        try
        {
            var result = await SyncCoreAsync(prepared.Source, plan.PackDirectory, prepared.Manifest,
                prepared.HadManifest, progress, token, prepared).ConfigureAwait(false);
            _presetPreviews.TryRemove(plan.PackRelativePath, out _);
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (prepared.HadManifest && !IsUpdatePending(plan.PackDirectory) &&
                                   ex is HttpRequestException or IOException or InvalidDataException
                                       or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            plan.VerifiedInstalledFileSha256 = await VerifyOfflineInstallationAsync(plan.PackDirectory, token).ConfigureAwait(false);
            _presetPreviews.TryRemove(plan.PackRelativePath, out _);
            _logger.Warn($"Pack sync for {plan.PackRelativePath} failed; playing the checked local version: {ex.Message}");
            return new PackSyncResult(PackSyncOutcome.OfflineFallback, null, 0, 0, OfflineSyncWarning);
        }
    }

    private async Task<PackSyncResult> SyncCoreAsync(
        PackSyncSource source,
        string packDir,
        ValidatedManifest manifest,
        bool hadManifest,
        IProgress<RuntimePreparationProgress>? progress,
        CancellationToken token,
        PreparedSyncState? prepared = null)
    {
        var statePath = Path.Combine(packDir, SyncStateFileName);
        var pendingPath = Path.Combine(packDir, ApplyPendingFileName);
        var repairProgress = File.Exists(pendingPath) ? progress : null;
        PackSyncDiff diff;
        try
        {
            var current = await Task.Run(() => ComputeDiff(packDir, statePath, manifest, token), token).ConfigureAwait(false);
            if (prepared is not null) RequireUnchangedDownloadPlan(prepared.Diff, current);
            diff = prepared?.Diff ?? current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable local file cannot be certified as an intact offline
            // installation; in particular it can belong to a running game.
            throw new PackApplyFailedException(ex);
        }

        var changedSet = new HashSet<string>(diff.ChangedPaths, StringComparer.OrdinalIgnoreCase);
        var neededAssets = manifest.Assets.Where(asset => asset.Covers.Any(changedSet.Contains)).ToList();
        var totalPendingBytes = neededAssets.Sum(asset => asset.SizeBytes);
        var downloadAssets = prepared is null ? neededAssets :
            neededAssets.Where(asset => !prepared.PrefetchedAssetNames.Contains(asset.Name)).ToList();
        var remainingBytes = downloadAssets.Sum(asset => asset.SizeBytes);
        long sharedBytesDownloaded = 0;
        var sharedBudget = prepared?.SharedArchiveBytes ?? 0;
        if (prepared?.RuntimeDescriptor is { } descriptor)
        {
            sharedBytesDownloaded = await PrepareSharedRuntimeAsync(descriptor, remainingBytes, progress, token).ConfigureAwait(false);
            sharedBudget = Math.Max(sharedBudget, sharedBytesDownloaded);
        }

        if (string.Equals(diff.StateRevision, manifest.Revision, StringComparison.Ordinal) &&
            diff.ChangedPaths.Count == 0 &&
            diff.Extras.Count == 0)
        {
            lock (PackInstanceService.ArchiveGate)
            {
                token.ThrowIfCancellationRequested();
                ReportStructureRepair(repairProgress, 0);
                WriteSyncState(statePath, packDir, manifest, ReadSyncState(statePath));
                PackInstanceService.EnsureArchiveFolders(packDir);
                File.Delete(pendingPath);
                ReportStructureRepair(repairProgress, 1);
            }
            progress?.Report(new RuntimePreparationProgress(
                RuntimePreparationStage.SyncingPack,
                "Сборка актуальна",
                1));
            return new PackSyncResult(PackSyncOutcome.UpToDate, manifest.Revision, 0, sharedBytesDownloaded, null);
        }

        Directory.CreateDirectory(packDir);
        var stageParent = Path.Combine(packDir, StagingDirectoryName);
        try
        {
            var stageRoot = prepared?.StageRoot ?? Path.Combine(stageParent, Guid.NewGuid().ToString("N"));
            var stageAssets = Path.Combine(stageRoot, "assets");
            var stageExtract = Path.Combine(stageRoot, "extract");
            if (neededAssets.Count > 0)
            {
                if (prepared is not null)
                    foreach (var asset in neededAssets.Where(asset => prepared.PrefetchedAssetNames.Contains(asset.Name)))
                        VerifyStagedAsset(Path.Combine(stageAssets, asset.Name), asset);
                var requiredBytes = checked(totalPendingBytes * 2);
                if (!_freeSpaceProbe(packDir, requiredBytes))
                {
                    throw new IOException(
                        "Not enough free disk space to update the pack: " +
                        $"{FormatMegabytes(requiredBytes)} MB required.");
                }
                Directory.CreateDirectory(stageAssets);
                Directory.CreateDirectory(stageExtract);
                await DownloadAssetsAsync(source, downloadAssets, stageAssets, checked(remainingBytes + sharedBudget), sharedBudget, progress, token)
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
                        lock (PackInstanceService.ArchiveGate)
                        {
                            token.ThrowIfCancellationRequested();
                            EnsureGameStopped(Path.GetRelativePath(_paths.Packs, packDir));
                            if (prepared is not null)
                                RequireUnchangedDownloadPlan(prepared.Diff, ComputeDiff(packDir, statePath, manifest, token));
                            ReportStructureRepair(repairProgress, 0);
                            AtomicFile.WriteAllText(
                                pendingPath,
                                JsonSerializer.Serialize(new { schemaVersion = 1, revision = manifest.Revision }, JsonOptions));
                            var previous = ReadSyncState(statePath);
                            ApplyChanges(
                                packDir, manifest, previous, diff.ChangedPaths, diff.Extras, stageAssets, stageExtract, token, repairProgress);
                            // Keep ownership proofs for the old files until the
                            // new snapshot has been written and the apply is complete.
                            WriteSyncState(statePath, packDir, manifest, previous);
                            PackInstanceService.EnsureArchiveFolders(packDir);
                            PackSourceResolver.WriteMarker(packDir, source);
                            File.Delete(pendingPath);
                            ReportStructureRepair(repairProgress, 1);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                                   or NotSupportedException)
                    {
                        // Files have already landed, so the pack is now a mix of
                        // two revisions. Falling back to "play the local version"
                        // here would start the game on that mix.
                        throw new PackApplyFailedException(ex);
                    }
                },
                token).ConfigureAwait(false);
        }
        finally
        {
            if (prepared is null) TryDeleteDirectory(stageParent);
        }

        var outcome = !hadManifest
            ? PackSyncOutcome.Installed
            : diff.ChangedPaths.Count > 0 || diff.Extras.Count > 0
                ? PackSyncOutcome.Updated
                : PackSyncOutcome.UpToDate;
        var bytesDownloaded = checked(totalPendingBytes + sharedBytesDownloaded);
        _logger.Info(
            $"Pack sync finished: {outcome}, revision {manifest.Revision}, " +
            $"{diff.ChangedPaths.Count} file(s) written, {diff.Extras.Count} extra(s) removed.");
        // The one consequence of an update that a player cannot see coming and
        // cannot undo afterwards. Said on the update that does it, rather than
        // discovered on the launch that opens the world.
        var removedModsWarning = outcome == PackSyncOutcome.Updated
            ? DescribeRemovedMods(diff.RemovedMods)
            : null;
        if (removedModsWarning is not null) _logger.Warn(removedModsWarning);
        progress?.Report(new RuntimePreparationProgress(
            RuntimePreparationStage.SyncingPack,
            outcome == PackSyncOutcome.UpToDate ? "Сборка актуальна" : "Сборка обновлена",
            1));
        return new PackSyncResult(
            outcome,
            manifest.Revision,
            diff.ChangedPaths.Count + diff.Extras.Count,
            bytesDownloaded,
            removedModsWarning);
    }

    private static void RequireUnchangedDownloadPlan(PackSyncDiff planned, PackSyncDiff current)
    {
        if (!string.Equals(planned.StateRevision, current.StateRevision, StringComparison.Ordinal) ||
            !planned.ChangedPaths.SequenceEqual(current.ChangedPaths, StringComparer.OrdinalIgnoreCase) ||
            !planned.Extras.SequenceEqual(current.Extras, StringComparer.OrdinalIgnoreCase) ||
            !planned.RemovedMods.SequenceEqual(current.RemovedMods, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Файлы сборки изменились после подготовки загрузки. Повторите «Играть».");
    }

    private void EnsureGameStopped(string packRelativePath)
    {
        if (PackAccess.IsRunning(_paths, [packRelativePath], _logger))
            throw new InvalidOperationException("Закройте запущенную игру перед обновлением сборки.");
    }

    /// <summary>
    /// What to tell the player about mods this update took away, or null when
    /// it took none.
    /// </summary>
    private static string? DescribeRemovedMods(List<string> removed)
    {
        if (removed.Count == 0) return null;

        var mods = removed.Count(IsModJar);
        var subject = mods == removed.Count
            ? "моды"
            : mods == 0
                ? "скрипты, добавлявшие блоки и предметы"
                : "моды и скрипты";

        const int named = 3;
        var names = string.Join(", ", removed.Take(named).Select(Path.GetFileNameWithoutExtension));
        var rest = removed.Count - named;
        return $"Обновление убрало из сборки {subject} ({removed.Count}): {names}" +
               (rest > 0 ? $" и ещё {rest}" : "") +
               ". Всё, что они поставили в ваших мирах, пропадёт при следующем открытии.";
    }

    private async Task<RemoteManifestDto> FetchManifestAsync(PackSyncSource source, CancellationToken token)
    {
        // The catalogue fixes membership/source selection for this session, not
        // the version to play. Each preparation reads current mutable metadata;
        // PrepareDownloadPlanAsync then pins that one manifest through apply and
        // the local-AI plan, even if the release changes during the download.
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
                using var response = await GitHubReleaseTransport.SendAsync(_httpClient, request, timeoutCts.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaximumManifestBytes)
                    throw new InvalidDataException("Pack sync manifest exceeds its byte limit.");
                await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
                using var bounded = new MemoryStream();
                var chunk = new byte[64 * 1024];
                while (true)
                {
                    var read = await stream.ReadAsync(chunk, timeoutCts.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (bounded.Length > MaximumManifestBytes - read)
                        throw new InvalidDataException("Pack sync manifest exceeds its byte limit.");
                    bounded.Write(chunk, 0, read);
                }
                bounded.Position = 0;
                return await JsonSerializer
                        .DeserializeAsync<RemoteManifestDto>(bounded, JsonOptions, timeoutCts.Token)
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
        long alreadyDownloaded,
        IProgress<RuntimePreparationProgress>? progress,
        CancellationToken token)
    {
        long downloadedBytes = alreadyDownloaded;
        long lastReportedBytes = alreadyDownloaded;
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
            totalBytes > 0 ? (double)alreadyDownloaded / totalBytes : null,
            alreadyDownloaded,
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
                        token: assetToken)
                    .ConfigureAwait(false)).ConfigureAwait(false);
    }

    private Task<long> PrepareSharedRuntimeAsync(PackRuntimeDescriptor descriptor, long packBytes,
        IProgress<RuntimePreparationProgress>? progress, CancellationToken token)
    {
        var shared = descriptor.NativeRuntime!.SharedRuntime!;
        var asset = new ValidatedAsset(shared.Archive.Name, PackAssetKind.ZipRoot, shared.Archive.Sha256,
            shared.Archive.SizeBytes, []);
        long retained = 0;
        var total = checked(packBytes + shared.Archive.SizeBytes);
        return new SharedNativeRuntimeStore(_paths).PrepareAsync(shared, descriptor.MinecraftVersion,
            (destination, ct) => DownloadAssetAsync(shared.Source, asset, destination, delta =>
            {
                retained += delta;
                progress?.Report(new(RuntimePreparationStage.SyncingPack, "Загрузка файлов игры", (double)retained / total, retained, total));
            }, refreshCatalogOnMissingAsset: false, token: ct), _freeSpaceProbe, token);
    }

    private async Task DownloadAssetAsync(
        PackSyncSource source,
        ValidatedAsset asset,
        string destinationPath,
        Action<long> reportBytes,
        bool refreshCatalogOnMissingAsset = true, CancellationToken token = default)
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
                using var response = await GitHubReleaseTransport.SendAsync(_httpClient, request, sourceToken)
                    .ConfigureAwait(false);
                var effectiveUri = response.RequestMessage?.RequestUri ?? assetUri;
                if (!IsApprovedEffectiveUri(effectiveUri))
                {
                    throw new InvalidDataException(
                        $"Pack asset {asset.Name} download was redirected to {SanitizeUri(effectiveUri)}.");
                }
                if (refreshCatalogOnMissingAsset && Catalog is not null &&
                    response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
                    throw new PackSessionExpiredException();
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

            // Archive timestamps, replaced git history and same-length edits
            // must not make stale bytes look current. The manifest's actual
            // size and content hash are the complete reuse criterion.
            if (info.Length != file.SizeBytes || !HashesEqual(HashFile(info.FullName), file.Sha256))
            {
                changedPaths.Add(file.Path);
            }
        }

        return new PackSyncDiff(
            state.Revision,
            changedPaths,
            FindExtras(packDir, state, manifest),
            FindRemovedMods(state, manifest));
    }

    /// <summary>
    /// The mods this revision drops, as against the one already installed.
    /// </summary>
    /// <remarks>
    /// Not the same question as "what is about to be deleted", which is what
    /// <see cref="FindExtras"/> answers and which mixes two unrelated things:
    /// files the author removed, and files the player put there themselves. The
    /// sync state holds every path of the revision on disk - written after the
    /// last update finished, read before this one touches anything - so a path
    /// in it that the new manifest does not name is one the author took out,
    /// and nothing else is.
    ///
    /// Only what registers blocks and items, which is mods and one other thing:
    /// a KubeJS startup script. One build creates a sword and a block of its
    /// own in kubejs/startup_scripts, and to a save those are not different
    /// from a mod's - the script going away turns placed blocks to air and
    /// empties them out of chests just the same. A datapack or a
    /// resource pack going away only changes how a world looks or what can be
    /// made in it, and a warning that fires for those stops being believed
    /// before it is needed. A first install has an empty state and so reports
    /// nothing, which is right: there was no world before it.
    ///
    /// And it asks about mods rather than about paths, because the two are not
    /// the same question and the difference is the whole update. Raising a
    /// mod's version renames its jar - jei-1.21.1-19.21.0.jar becomes
    /// jei-1.21.1-19.22.0.jar - so by path the old one is gone from every
    /// revision that bumps it, which is most of them. Answering by path would
    /// name thirty mods that are still installed on an ordinary update, and a
    /// warning that cries wolf on every update is not read on the one that
    /// matters.
    /// </remarks>
    private static List<string> FindRemovedMods(SyncStateDto state, ValidatedManifest manifest)
    {
        var kept = manifest.FilesByPath.Keys
            .Where(IsModJar)
            .Select(ModStem)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return state.Files.Keys
            .Where(path => IsModJar(path)
                ? !kept.Contains(ModStem(path))
                : IsStartupScript(path) && !manifest.FilesByPath.ContainsKey(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsModJar(string path) =>
        path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A script that runs before the registries close, and so can add to them.
    /// Its name carries no version, so unlike a jar it is the same path from one
    /// revision to the next and can be compared as one.
    /// </summary>
    private static bool IsStartupScript(string path) =>
        path.StartsWith("kubejs/startup_scripts/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The part of a jar's name that its author does not change when they raise
    /// its version: everything before the first hyphenated piece that starts
    /// with a digit.
    /// </summary>
    /// <remarks>
    /// Measured against the 882 jars of one build and the 95 of an older
    /// Fabric build, which between them carry every shape their authors use:
    /// AE2-Things-1.4.2-beta, AdvancedAE-1.6.11-1.21.1,
    /// AdChimneys-1.21.1-11.1.8.0-NeoForge-build.1029,
    /// connector-2.0.0-beta.17+1.21.1-full. All four keep their name and lose
    /// the rest.
    ///
    /// The first piece is never cut, because a name may open with a digit -
    /// 0Pack2Reload-NeoForge-1.21.1-1.0.1.jar, 1.21.1-identity2-neoforge-2.2.1
    /// - and cutting there would leave nothing to compare. A piece that is a
    /// Minecraft version rather than a mod version survives into the stem
    /// (sodium-fabric-mc1.20.1), which costs nothing: a pack has one Minecraft
    /// version, so it is the same on both sides of every update.
    ///
    /// Where it errs, it errs quietly. Two mods sharing a stem hide one
    /// removal; an author who renames a mod outright reports one that did not
    /// happen. Both are rare, and the first - saying too little about a rare
    /// event - is the side to be wrong on for a sentence whose whole purpose is
    /// to be believed.
    /// </remarks>
    private static string ModStem(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path["mods/".Length..]);
        var pieces = name.Split('-');
        var length = pieces.Length;
        for (var index = 1; index < pieces.Length; index++)
        {
            if (pieces[index].Length > 0 && char.IsAsciiDigit(pieces[index][0]))
            {
                length = index;
                break;
            }
        }
        return string.Join('-', pieces, 0, length);
    }

    /// <summary>
    /// Remove only files with established pack ownership. A directory name or
    /// an unfamiliar jar is never proof that a player did not put it there.
    /// </summary>
    private static List<string> FindExtras(string packDir, SyncStateDto state, ValidatedManifest manifest)
    {
        var extras = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(packDir)) return [];
        var packRoot = Path.GetFullPath(packDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var relative in state.Files.Keys)
        {
            try
            {
                ValidateManifestPath(relative, packRoot);
                if (!IsProtectedRootName(relative.Split('/')[0]) &&
                    !manifest.FilesByPath.ContainsKey(relative) &&
                    File.Exists(Path.Combine(packDir, relative.Replace('/', Path.DirectorySeparatorChar))))
                    extras.Add(relative);
            }
            catch (InvalidDataException)
            {
                // Local state is not trusted with paths outside the managed tree.
            }
        }
        foreach (var legacy in manifest.LegacyFiles)
        {
            if (!manifest.FilesByPath.ContainsKey(legacy.Path) &&
                !IsProtectedRootName(legacy.Path.Split('/')[0]) &&
                HasFileWithHash(packDir, legacy)) extras.Add(legacy.Path);
        }
        foreach (var legacy in state.RetiredFiles ?? [])
        {
            if (legacy is null) continue;
            try
            {
                var relative = ValidateManifestPath(legacy.Path, packRoot);
                if (!manifest.FilesByPath.ContainsKey(relative) && !IsProtectedRootName(relative.Split('/')[0]) &&
                    legacy.SizeBytes >= 0 && HasFileWithHash(packDir,
                        new ValidatedFile(relative, legacy.SizeBytes, ValidateSha256(legacy.Sha256, relative))))
                    extras.Add(relative);
            }
            catch (InvalidDataException) { }
        }
        return extras.Order(StringComparer.OrdinalIgnoreCase).ToList();
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
        SyncStateDto previous,
        IReadOnlyList<string> changedPaths,
        IReadOnlyList<string> extras,
        string stageAssets,
        string stageExtract,
        CancellationToken token,
        IProgress<RuntimePreparationProgress>? repairProgress)
    {
        var historicalArchives = manifest.LegacyFiles
            .Concat((previous.RetiredFiles ?? []).Where(file => file is { Path: not null, Sha256: not null, SizeBytes: >= 0 })
                .Select(file => new ValidatedFile(file.Path!, file.SizeBytes, file.Sha256!)))
            .Where(file => PackInstanceService.IsPackArchive(file.Path))
            .ToLookup(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var repaired = 0;
        var total = changedPaths.Count + extras.Count + 1;
        foreach (var path in changedPaths)
        {
            if (IsPackManifestPath(path)) continue;
            token.ThrowIfCancellationRequested();
            PreserveArchive(path, manifest.FilesByPath[path].Sha256);
            PlaceStagedFile(
                packDir,
                path,
                ResolveStagedSource(manifest.AssetByCoveredPath[path], stageAssets, stageExtract, path));
            ReportApplied();
        }

        foreach (var extra in extras)
        {
            token.ThrowIfCancellationRequested();
            PreserveArchive(extra, null);
            var livePath = Path.Combine(packDir, extra.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(livePath)) File.Delete(livePath);
            DeleteEmptyParents(Path.GetDirectoryName(livePath), packDir);
            ReportApplied();
        }

        foreach (var path in changedPaths)
        {
            if (!IsPackManifestPath(path)) continue;
            token.ThrowIfCancellationRequested();
            PlaceStagedFile(
                packDir,
                path,
                ResolveStagedSource(manifest.AssetByCoveredPath[path], stageAssets, stageExtract, path));
            ReportApplied();
        }

        void PreserveArchive(string path, string? installedHash)
        {
            if (!PackInstanceService.IsPackArchive(path)) return;
            var history = historicalArchives[path];
            if (history.Any())
            {
                ValidateManifestPath(path, packDir);
                if (history.Any(file => HasFileWithHash(packDir, file with { Path = path }))) return;
            }
            PackInstanceService.PreserveArchiveBeforePackUpdate(packDir, path, previous.Files.GetValueOrDefault(path)?.Sha256,
                installedHash, candidate => manifest.FilesByPath.ContainsKey(candidate) ||
                    previous.Files.ContainsKey(candidate) || historicalArchives.Contains(candidate),
                token.ThrowIfCancellationRequested);
        }

        void ReportApplied()
        {
            if (repairProgress is not null && (++repaired % 64 == 0 || repaired == total - 1))
                ReportStructureRepair(repairProgress, (double)repaired / total);
        }
    }

    private static void ReportStructureRepair(IProgress<RuntimePreparationProgress>? progress, double fraction) =>
        progress?.Report(new(RuntimePreparationStage.RepairingPackStructure, "Восстанавливаем файлы сборки", fraction));

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

    private static void WriteSyncState(string statePath, string packDir, ValidatedManifest manifest, SyncStateDto previous)
    {
        var state = new SyncStateDto
        {
            SchemaVersion = SyncStateCacheGeneration,
            Revision = manifest.Revision,
            HasCompleteLauncherPaths = true,
            RequiredLauncherFeatures = manifest.RequiredLauncherFeatures.Count == 0 ? null : [.. manifest.RequiredLauncherFeatures]
        };
        foreach (var file in manifest.Files)
        {
            var info = new FileInfo(Path.Combine(packDir, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!info.Exists || info.Length != file.SizeBytes || !HashesEqual(HashFile(info.FullName), file.Sha256))
                throw new InvalidDataException($"Applied pack file does not match its manifest: {file.Path}");
            state.Files[file.Path] = new SyncStateFileDto
            {
                SizeBytes = info.Length,
                LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                Sha256 = file.Sha256
            };
        }
        // Keep historical proofs after the canonical old files have gone: a
        // player's instance may first be migrated on a later launch, even after
        // a repaired download or an idempotent synchronization.
        var retired = new Dictionary<string, RemoteFileDto>(StringComparer.OrdinalIgnoreCase);
        var packRoot = Path.GetFullPath(packDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        void RememberRetired(string? path, long size, string? sha)
        {
            try
            {
                var relative = ValidateManifestPath(path, packRoot);
                if (size < 0 || IsProtectedRootName(relative.Split('/')[0]) || manifest.FilesByPath.ContainsKey(relative)) return;
                var hash = ValidateSha256(sha, relative);
                retired[relative + "\0" + hash] = new RemoteFileDto { Path = relative, SizeBytes = size, Sha256 = hash };
            }
            catch (InvalidDataException) { }
        }
        foreach (var file in previous.RetiredFiles ?? [])
            if (file is not null) RememberRetired(file.Path, file.SizeBytes, file.Sha256);
        foreach (var (path, file) in previous.Files)
            if (file is not null) RememberRetired(path, file.SizeBytes, file.Sha256);
        foreach (var file in manifest.LegacyFiles)
            RememberRetired(file.Path, file.SizeBytes, file.Sha256);
        state.RetiredFiles = retired.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.Sha256, StringComparer.OrdinalIgnoreCase).ToList();
        AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(state, JsonOptions));
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
                    state.Files = new Dictionary<string, SyncStateFileDto>(state.Files ?? [], StringComparer.OrdinalIgnoreCase);
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

    public static void ValidateSnapshot(string json, string packDir, CancellationToken token = default)
    {
        var dto = JsonSerializer.Deserialize<RemoteManifestDto>(json, JsonOptions)
            ?? throw new InvalidDataException("Empty pack manifest.");
        ValidateRemoteManifest(dto, packDir, token);
    }

    private static ValidatedManifest ValidateRemoteManifest(RemoteManifestDto manifest, string packDir, CancellationToken token = default)
    {
        if (manifest.SchemaVersion is not (LegacyRemoteManifestGeneration or FeatureManifestGeneration))
        {
            throw new NotSupportedException(
                $"Сборке требуется другой формат манифеста ({manifest.SchemaVersion}). Обновите лаунчер и повторите «Играть».");
        }
        var requiredFeatures = manifest.RequiredLauncherFeatures;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (manifest.SchemaVersion == LegacyRemoteManifestGeneration)
        {
            if (requiredFeatures.ValueKind != JsonValueKind.Undefined)
                throw new NotSupportedException(
                    "Сборка с обязательными функциями требует формат манифеста 2. Обновите лаунчер и повторите «Играть».");
        }
        else
        {
            if (requiredFeatures.ValueKind != JsonValueKind.Array || requiredFeatures.GetArrayLength() == 0)
                throw new NotSupportedException(
                    "Манифест сборки не указывает обязательные функции. Обновите лаунчер и повторите «Играть».");
            foreach (var value in requiredFeatures.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } feature ||
                    !SupportedLauncherFeatures.Contains(feature) || !seen.Add(feature))
                    throw new NotSupportedException(
                        "Обязательная функция сборки неизвестна или указана неверно. Обновите лаунчер и повторите «Играть».");
            }
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
            token.ThrowIfCancellationRequested();
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

        var legacyFiles = new List<ValidatedFile>();
        foreach (var legacy in manifest.LegacyFiles ?? [])
        {
            if (legacy is null || legacy.SizeBytes < 0)
                throw new InvalidDataException("Pack sync manifest contains an invalid legacy file.");
            var path = ValidateManifestPath(legacy.Path, packRoot);
            legacyFiles.Add(new ValidatedFile(path, legacy.SizeBytes, ValidateSha256(legacy.Sha256, path)));
        }
        return new ValidatedManifest(revision, files, assets, filesByPath, assetByCoveredPath, legacyFiles, seen);
    }

    internal static string ValidateManifestPath(string? value, string packRoot, bool checkAncestors = true)
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
        if (string.Equals(path.Split('/')[0], PackLaunchContextService.RuntimeDirectory, StringComparison.OrdinalIgnoreCase) ||
            IsServiceRelativePath(path))
        {
            throw new InvalidDataException($"Pack sync manifest may not ship a service file: {path}");
        }
        var full = Path.GetFullPath(Path.Combine(packRoot, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(packRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Pack sync manifest path escapes the pack: {path}");
        }
        if (!checkAncestors) return path;
        var current = packRoot;
        foreach (var segment in path.Split('/').Prepend(""))
        {
            if (segment.Length > 0) current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Pack sync path passes through a link: {path}");
        }
        return path;
    }

    internal static string ValidateSha256(string? value, string context)
    {
        var sha = value?.Trim() ?? "";
        if (sha.Length != 64 || sha.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"Pack sync manifest SHA-256 is invalid for {context}.");
        }
        return sha.ToLowerInvariant();
    }

    private static Uri BuildReleaseAssetUri(PackSyncSource source, string assetName, bool preventCaching = false)
    {
        var uri =
            $"https://github.com/{source.Owner}/{source.Repo}/releases/download/{source.Tag}/" +
            Uri.EscapeDataString(assetName);
        if (preventCaching)
        {
            uri += $"?cache={Guid.NewGuid():N}";
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

    internal static bool IsServiceRelativePath(string relativePath) =>
        string.Equals(relativePath, SourceMarkerFileName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(relativePath, SyncStateFileName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(relativePath, ApplyPendingFileName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(relativePath, InstanceStateFileName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(relativePath, LauncherCleanupStateFileName, StringComparison.OrdinalIgnoreCase);

    private static bool IsProtectedRootName(string name) =>
        IsServiceRelativePath(name) ||
        PackInstanceService.InstanceOwnedDirectories.Contains(name) ||
        PackInstanceService.InstanceOwnedFiles.Contains(name);

    /// <summary>A sync in flight, or one interrupted and swept on the next start.</summary>
    private static bool IsStagingEntryName(string name) =>
        name.StartsWith(StagingDirectoryName, StringComparison.OrdinalIgnoreCase);

    private static string HashFile(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
            catch (IOException) when (attempt < ReplaceAttempts)
            {
                Thread.Sleep(ReplaceRetryDelay * attempt);
            }
        }
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

    private static void TryDeleteDownloadPlanDirectory(string packDir, string path, string ownership = DownloadPlanOwnership)
    {
        try
        {
            var packRoot = Path.GetFullPath(packDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var relative = Path.GetRelativePath(packRoot, Path.GetFullPath(path)).Replace('\\', '/');
            if (relative.Contains('/') || !relative.StartsWith(DownloadPlanDirectoryPrefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(relative[DownloadPlanDirectoryPrefix.Length..], "N", out _)) return;
            ValidateManifestPath(relative, packRoot);
            if (!Directory.Exists(path)) return;
            var marker = relative + "/" + DownloadPlanOwnershipFile;
            ValidateManifestPath(marker, packRoot);
            var ownerFile = Path.Combine(packRoot, marker.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(ownerFile) || new FileInfo(ownerFile).Length != System.Text.Encoding.UTF8.GetByteCount(ownership) ||
                File.ReadAllText(ownerFile) != ownership) return;
            // Do not descend into links introduced after staging was created.
            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.TryPop(out var directory))
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return;
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            ValidateManifestPath(relative, packRoot);
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
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
        List<string> Extras,
        List<string> RemovedMods);

    private sealed record PreparedSyncState(
        PackSyncSource Source,
        ValidatedManifest Manifest,
        PackSyncDiff Diff,
        bool HadManifest,
        string StageRoot,
        IReadOnlySet<string> PrefetchedAssetNames,
        PackRuntimeDescriptor? RuntimeDescriptor,
        long SharedArchiveBytes);

    private sealed record SharedRuntimeRecovery(PackRuntimeDescriptor Descriptor);

    private sealed record PreparedLauncherMetadata(string Directory, IReadOnlySet<string> PrefetchedAssetNames);

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
        Dictionary<string, ValidatedAsset> AssetByCoveredPath,
        IReadOnlyList<ValidatedFile> LegacyFiles,
        IReadOnlySet<string> RequiredLauncherFeatures);

    private sealed class RemoteManifestDto
    {
        public int SchemaVersion { get; set; }
        public JsonElement RequiredLauncherFeatures { get; set; }
        public string? PackId { get; set; }
        public string? Revision { get; set; }
        /// <summary>Read only for weighing a pack that is not installed yet.</summary>
        public string? MinecraftVersion { get; set; }
        /// <summary>
        /// Jars plus the mods nested inside them, as the publisher counted them.
        /// Absent from a manifest written before the field, and the weighing
        /// falls back to the jar count exactly as it always did.
        /// </summary>
        public int? ModCount { get; set; }
        public List<RemoteFileDto>? Files { get; set; }
        /// <summary>Previously published hashes, used only to identify legacy managed files.</summary>
        public List<RemoteFileDto>? LegacyFiles { get; set; }
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
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public bool HasCompleteLauncherPaths { get; set; }
        public Dictionary<string, SyncStateFileDto> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<RemoteFileDto>? RetiredFiles { get; set; } = [];
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string[]? RequiredLauncherFeatures { get; set; }
    }

    private sealed class SyncStateFileDto
    {
        public long SizeBytes { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string Sha256 { get; set; } = "";
    }
}
