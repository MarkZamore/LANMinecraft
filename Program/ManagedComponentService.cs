using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Minecraft;

/// <summary>
/// Prepares launcher-owned Minecraft components for an already prepared pack instance.
/// The caller must await this service before starting Minecraft; validation and download
/// failures intentionally propagate and therefore fail the launch closed.
/// </summary>
public sealed class ManagedComponentService
{
    public const long FtbEssentialsCurseForgeProjectId = 410811;
    public const long FtbEssentialsCurseForgeFileId = 7608733;
    public const string FtbEssentialsVersion = "2101.1.9";
    public const string FtbEssentialsFileName = "ftb-essentials-neoforge-2101.1.9.jar";
    public const long FtbEssentialsSizeBytes = 209459;
    public const string FtbEssentialsSha256 =
        "4da8e4d461ceef1a5e6f6705265fe24239b132cdc408eb77787231cb56c219bf";
    private static readonly TimeSpan DownloadSourceTimeout = TimeSpan.FromSeconds(45);

    // The timeout covers the whole body copy, so it has to scale with the
    // artifact: a flat 45s fits a 200 KB jar but would abort a 69 MB one on
    // any connection slower than ~12 Mbit/s. The floor assumes 128 KiB/s.
    internal static TimeSpan DownloadTimeoutFor(ManagedComponentDescriptor component) =>
        DownloadSourceTimeout + TimeSpan.FromSeconds(component.SizeBytes / (128d * 1024d));

    public static Uri FtbEssentialsDownloadUri { get; } = new(
        "https://mediafilez.forgecdn.net/files/7608/733/ftb-essentials-neoforge-2101.1.9.jar",
        UriKind.Absolute);

    public static Uri FtbEssentialsEdgeDownloadUri { get; } = new(
        "https://edge.forgecdn.net/files/7608/733/ftb-essentials-neoforge-2101.1.9.jar",
        UriKind.Absolute);

    public static Uri FtbEssentialsCurseForgeDownloadUri { get; } = new(
        "https://www.curseforge.com/api/v1/mods/410811/files/7608733/download",
        UriKind.Absolute);

    public static IReadOnlyList<Uri> FtbEssentialsDownloadUris { get; } =
        Array.AsReadOnly(
        [
            FtbEssentialsDownloadUri,
            FtbEssentialsEdgeDownloadUri,
            FtbEssentialsCurseForgeDownloadUri
        ]);

    private static readonly ManagedComponentDescriptor FtbEssentials = new(
        "ftb-essentials",
        FtbEssentialsCurseForgeFileId,
        FtbEssentialsFileName,
        FtbEssentialsDownloadUris,
        FtbEssentialsSizeBytes,
        FtbEssentialsSha256);
    private static readonly SemaphoreSlim FtbEssentialsGate = new(1, 1);

    // Bumblezone 7.13.2 ships a chunk-packet mixin that crashes the server in
    // its dimension when ModernFix and Smooth Chunk are also installed - both
    // are part of the Infinity pack. The mod's own 7.13.5 changelog names the
    // interaction; 7.15.3 is the latest 1.21.1 build carrying the fix.
    // Synthetic cache key: Modrinth version ids are strings (jHtMMEiy), but the
    // cache layout is keyed by number. Encodes the pinned version 7.15.03.
    public const long BumblezoneCacheFileId = 71503;
    public const string BumblezoneVersion = "7.15.3";
    public const string BumblezoneFileName = "the_bumblezone-7.15.3+1.21.1-neoforge.jar";
    public const string BumblezoneSupersededFileName = "the_bumblezone-7.13.2+1.21.1-neoforge.jar";
    public const long BumblezoneSizeBytes = 69_672_269;
    public const string BumblezoneSha256 =
        "049b688a43886a3a5f70e8c0f195db7e33335e20adc1124c981a377a8b511101";

    public static Uri BumblezoneDownloadUri { get; } = new(
        "https://cdn.modrinth.com/data/38tpSycf/versions/jHtMMEiy/the_bumblezone-7.15.3%2B1.21.1-neoforge.jar",
        UriKind.Absolute);

    public static IReadOnlyList<Uri> BumblezoneDownloadUris { get; } =
        Array.AsReadOnly([BumblezoneDownloadUri]);

    private static readonly ManagedComponentDescriptor BumblezoneHotfix = new(
        "the-bumblezone",
        BumblezoneCacheFileId,
        BumblezoneFileName,
        BumblezoneDownloadUris,
        BumblezoneSizeBytes,
        BumblezoneSha256);

    // Oritech 1.2.3's Promethium Pickaxe area mode re-posts the block-break
    // event from inside its own break handler, recursing until the server
    // dies with "Recursion depth became negative". Fixed upstream in 1.2.5
    // ("Fix promethium pickaxe crash with recursive permission checks on
    // neoforge"), the closest release to the pack's build.
    public const long OritechCacheFileId = 10205;
    public const string OritechVersion = "1.2.5";
    public const string OritechFileName = "oritech-neoforge-1.21.1-1.2.5.jar";
    public const string OritechSupersededFileName = "oritech-neoforge-1.21.1-1.2.3.jar";
    public const long OritechSizeBytes = 10_675_443;
    public const string OritechSha256 =
        "e863aa387cec1eca46fa5ca3c3233d4d05399f73017362f23a86fd6533551873";

    public static Uri OritechDownloadUri { get; } = new(
        "https://cdn.modrinth.com/data/4sYI62kA/versions/cXCIlwHu/oritech-neoforge-1.21.1-1.2.5.jar",
        UriKind.Absolute);

    private static readonly ManagedComponentDescriptor OritechHotfix = new(
        "oritech",
        OritechCacheFileId,
        OritechFileName,
        Array.AsReadOnly([OritechDownloadUri]),
        OritechSizeBytes,
        OritechSha256);

    internal static IReadOnlyList<ManagedModReplacement> DefaultModReplacements { get; } =
        Array.AsReadOnly(
        [
            new ManagedModReplacement(
                BumblezoneHotfix, BumblezoneSupersededFileName, "Bumblezone", BumblezoneVersion),
            new ManagedModReplacement(
                OritechHotfix, OritechSupersededFileName, "Oritech", OritechVersion)
        ]);
    private static readonly SemaphoreSlim ModReplacementGate = new(1, 1);

    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly HttpClient _httpClient;
    private readonly ManagedComponentDescriptor _ftbEssentials;
    private readonly ManagedModReplacement[] _modReplacements;

    public ManagedComponentService(
        AppPaths paths,
        Logger logger,
        HttpClient? httpClient = null)
        : this(paths, logger, httpClient, FtbEssentials)
    {
    }

    internal ManagedComponentService(
        AppPaths paths,
        Logger logger,
        HttpClient? httpClient,
        ManagedComponentDescriptor ftbEssentials,
        IReadOnlyList<ManagedModReplacement>? modReplacements = null)
    {
        _paths = paths;
        _logger = logger;
        _httpClient = httpClient ?? PortableHttpClient.Shared;
        _ftbEssentials = ValidateDescriptor(ftbEssentials);
        _modReplacements = (modReplacements ?? DefaultModReplacements)
            .Select(replacement => replacement with
            {
                Component = ValidateDescriptor(replacement.Component)
            })
            .ToArray();
    }

    /// <summary>
    /// Ensures the pinned FTB Essentials artifact is installed in
    /// <paramref name="preparedInstance"/>. This call never accepts an unverified JAR.
    /// </summary>
    public async Task<ManagedComponentInstallResult> EnsureFtbEssentialsAsync(
        PackInstanceContext preparedInstance,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preparedInstance);
        await FtbEssentialsGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await EnsureFtbEssentialsCoreAsync(preparedInstance, token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn(
                $"Required managed component {_ftbEssentials.Id} could not be prepared; " +
                $"Minecraft launch must remain blocked: {ex.Message}");
            throw;
        }
        finally
        {
            FtbEssentialsGate.Release();
        }
    }

    internal string FtbEssentialsCachePath => GetCachePath(_ftbEssentials);

    private async Task<ManagedComponentInstallResult> EnsureFtbEssentialsCoreAsync(
        PackInstanceContext preparedInstance,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var gameDirectory = ValidatePreparedInstance(preparedInstance);
        var modsDirectory = Path.Combine(gameDirectory, "mods");
        EnsureOrdinaryDirectory(modsDirectory);
        RejectConflictingFtbEssentials(modsDirectory, _ftbEssentials.FileName);

        var installedPath = Path.Combine(modsDirectory, _ftbEssentials.FileName);
        EnsureOrdinaryFileIfPresent(installedPath);
        var cachePath = GetCachePath(_ftbEssentials);
        EnsureOrdinaryFileIfPresent(cachePath);

        var downloaded = false;
        var installed = false;
        var cachePopulated = false;

        if (IsValidFile(installedPath, _ftbEssentials))
        {
            if (!IsValidFile(cachePath, _ftbEssentials))
            {
                await AtomicCopyAsync(
                        installedPath,
                        cachePath,
                        _ftbEssentials,
                        token)
                    .ConfigureAwait(false);
                cachePopulated = true;
                _logger.Info("Recovered FTB Essentials managed cache from the verified instance JAR.");
            }
        }
        else
        {
            if (!IsValidFile(cachePath, _ftbEssentials))
            {
                _logger.Info(
                    $"Downloading FTB Essentials {FtbEssentialsVersion} " +
                    $"(CurseForge file {FtbEssentialsCurseForgeFileId}).");
                await DownloadToCacheAsync(_ftbEssentials, cachePath, token).ConfigureAwait(false);
                downloaded = true;
                cachePopulated = true;
                _logger.Info("Verified pinned FTB Essentials download.");
            }

            await AtomicCopyAsync(cachePath, installedPath, _ftbEssentials, token)
                .ConfigureAwait(false);
            installed = true;
            _logger.Info("Installed pinned FTB Essentials into the prepared instance.");
        }

        return new ManagedComponentInstallResult(
            _ftbEssentials.Id,
            _ftbEssentials.FileId,
            installedPath,
            cachePath,
            downloaded,
            installed,
            cachePopulated);
    }

    /// <summary>
    /// Replaces known-broken pack builds mirrored into the instance with their
    /// pinned fixed builds. Each replacement is a no-op when the instance
    /// carries any other version of that mod, so a manually updated pack is
    /// never touched.
    /// </summary>
    public async Task<IReadOnlyList<ManagedComponentInstallResult>> EnsureModHotfixesAsync(
        PackInstanceContext preparedInstance,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preparedInstance);
        await ModReplacementGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var results = new List<ManagedComponentInstallResult>(_modReplacements.Length);
            foreach (var replacement in _modReplacements)
            {
                try
                {
                    results.Add(await EnsureModReplacementCoreAsync(
                        replacement, preparedInstance, token).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warn(
                        $"Required managed component {replacement.Component.Id} could not be " +
                        $"prepared; Minecraft launch must remain blocked: {ex.Message}");
                    throw;
                }
            }
            return results;
        }
        finally
        {
            ModReplacementGate.Release();
        }
    }

    internal string CachePathFor(ManagedComponentDescriptor component) => GetCachePath(component);

    private async Task<ManagedComponentInstallResult> EnsureModReplacementCoreAsync(
        ManagedModReplacement replacement,
        PackInstanceContext preparedInstance,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var component = replacement.Component;
        var gameDirectory = ValidatePreparedInstance(preparedInstance);
        var modsDirectory = Path.Combine(gameDirectory, "mods");
        EnsureOrdinaryDirectory(modsDirectory);

        var installedPath = Path.Combine(modsDirectory, component.FileName);
        var supersededPath = Path.Combine(modsDirectory, replacement.SupersededFileName);
        var cachePath = GetCachePath(component);
        EnsureOrdinaryFileIfPresent(installedPath);
        EnsureOrdinaryFileIfPresent(supersededPath);
        EnsureOrdinaryFileIfPresent(cachePath);

        var otherVersion = Directory
            .EnumerateFiles(modsDirectory, replacement.VersionScanPattern, SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .FirstOrDefault(name =>
                !string.Equals(name, component.FileName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, replacement.SupersededFileName, StringComparison.OrdinalIgnoreCase));
        var supersededPresent = File.Exists(supersededPath);
        if (otherVersion is not null)
        {
            // Someone updated the pack past the pinned build on their own;
            // installing the pin next to it would duplicate the mod.
            _logger.Warn(
                $"{replacement.DisplayName} hotfix skipped: the instance already carries {otherVersion}.");
            return NoOpResult(component, installedPath, cachePath);
        }
        if (!supersededPresent && !File.Exists(installedPath))
        {
            return NoOpResult(component, installedPath, cachePath);
        }

        var downloaded = false;
        var installed = false;
        var cachePopulated = false;

        if (IsValidFile(installedPath, component))
        {
            if (!IsValidFile(cachePath, component))
            {
                await AtomicCopyAsync(installedPath, cachePath, component, token)
                    .ConfigureAwait(false);
                cachePopulated = true;
                _logger.Info(
                    $"Recovered {replacement.DisplayName} managed cache from the verified instance JAR.");
            }
        }
        else
        {
            try
            {
                if (!IsValidFile(cachePath, component))
                {
                    _logger.Info(
                        $"Downloading {replacement.DisplayName} {replacement.Version} hotfix " +
                        $"(replaces {replacement.SupersededFileName}).");
                    await DownloadToCacheAsync(component, cachePath, token).ConfigureAwait(false);
                    downloaded = true;
                    cachePopulated = true;
                    _logger.Info($"Verified pinned {replacement.DisplayName} download.");
                }

                await AtomicCopyAsync(cachePath, installedPath, component, token)
                    .ConfigureAwait(false);
                installed = true;
                _logger.Info(
                    $"Installed pinned {replacement.DisplayName} into the prepared instance.");
            }
            catch (Exception ex) when (
                ex is HttpRequestException or InvalidDataException or IOException &&
                File.Exists(supersededPath) &&
                !File.Exists(installedPath))
            {
                // Unlike FTB Essentials (200 KB, three mirrors), these are
                // large single-source downloads: blocking the launch on a
                // failed fetch would turn a feature-specific crash into
                // "cannot play at all" for an offline first run. The instance
                // is still in its shipped state, so let the game run and retry
                // next launch.
                _logger.Warn(
                    $"{replacement.DisplayName} hotfix could not be prepared ({ex.Message}); " +
                    $"Minecraft will run with {replacement.SupersededFileName} this session, " +
                    "and its known crash stays possible until the hotfix downloads.");
                return NoOpResult(component, installedPath, cachePath);
            }
        }

        if (supersededPresent)
        {
            try
            {
                File.Delete(supersededPath);
                _logger.Info(
                    $"Removed superseded {replacement.SupersededFileName} from the prepared instance.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Two builds of the same mod side by side would fail mod
                // loading, so fail closed and put the instance back the way
                // it was.
                TryDeleteFile(installedPath);
                throw new InvalidOperationException(
                    $"Superseded {replacement.DisplayName} JAR could not be removed: {ex.Message}", ex);
            }
        }

        return new ManagedComponentInstallResult(
            component.Id,
            component.FileId,
            installedPath,
            cachePath,
            downloaded,
            installed,
            cachePopulated);
    }

    private static ManagedComponentInstallResult NoOpResult(
        ManagedComponentDescriptor component,
        string installedPath,
        string cachePath) =>
        new(
            component.Id,
            component.FileId,
            installedPath,
            cachePath,
            Downloaded: false,
            Installed: false,
            CachePopulated: false);

    private string ValidatePreparedInstance(PackInstanceContext preparedInstance)
    {
        if (string.IsNullOrWhiteSpace(preparedInstance.GameDirectory))
        {
            throw new InvalidOperationException("Prepared instance has no game directory.");
        }

        var gameDirectory = Path.GetFullPath(preparedInstance.GameDirectory);
        if (!IsUnderDirectory(gameDirectory, _paths.Instances))
        {
            throw new InvalidOperationException(
                $"Managed component target is outside the portable instances directory: {gameDirectory}");
        }
        if (!Directory.Exists(gameDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Prepared instance directory does not exist: {gameDirectory}");
        }
        EnsureOrdinaryDirectory(gameDirectory, createIfMissing: false);
        return gameDirectory;
    }

    private string GetCachePath(ManagedComponentDescriptor component)
    {
        var path = Path.Combine(
            _paths.Launcher,
            "ManagedComponents",
            component.Id,
            component.FileId.ToString(CultureInfo.InvariantCulture),
            component.FileName);
        _paths.EnsureUnderRoot(path);
        return path;
    }

    private async Task DownloadToCacheAsync(
        ManagedComponentDescriptor component,
        string cachePath,
        CancellationToken token)
    {
        var cacheDirectory = Path.GetDirectoryName(cachePath)
            ?? throw new InvalidOperationException($"Cache file has no parent directory: {cachePath}");
        EnsureOrdinaryDirectory(cacheDirectory);
        var failures = new List<string>(component.DownloadUris.Count);

        for (var sourceIndex = 0; sourceIndex < component.DownloadUris.Count; sourceIndex++)
        {
            token.ThrowIfCancellationRequested();
            var source = component.DownloadUris[sourceIndex];
            var sourceNumber = sourceIndex + 1;
            var temporaryPath = CreateTemporarySiblingPath(cachePath, "download");
            try
            {
                using var sourceTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                sourceTimeout.CancelAfter(DownloadTimeoutFor(component));
                var sourceToken = sourceTimeout.Token;
                _logger.Info(
                    $"Trying official managed-component source {sourceNumber}/" +
                    $"{component.DownloadUris.Count}: {SanitizeUri(source)}");

                using var request = new HttpRequestMessage(HttpMethod.Get, source);
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    sourceToken)
                    .ConfigureAwait(false);
                var effectiveUri = response.RequestMessage?.RequestUri ?? source;
                if (!IsApprovedEffectiveUri(component, effectiveUri))
                {
                    throw new InvalidDataException(
                        $"Managed component {component.Id} was redirected to an unapproved endpoint " +
                        $"{SanitizeUri(effectiveUri)}.");
                }
                if (!response.IsSuccessStatusCode)
                {
                    var failure = DescribeHttpFailure(response.StatusCode, effectiveUri);
                    RecordSourceFailure(
                        component,
                        sourceNumber,
                        failure,
                        failures);
                    continue;
                }
                if (response.Content.Headers.ContentLength is { } contentLength &&
                    contentLength != component.SizeBytes)
                {
                    throw new InvalidDataException(
                        $"Managed component {component.Id} has an unexpected Content-Length " +
                        $"{contentLength} at {SanitizeUri(effectiveUri)}.");
                }

                await using (var input = await response.Content
                                 .ReadAsStreamAsync(sourceToken)
                                 .ConfigureAwait(false))
                await using (var output = OpenTemporaryOutput(temporaryPath))
                {
                    await CopyExactAsync(input, output, component.SizeBytes, sourceToken)
                        .ConfigureAwait(false);
                    await output.FlushAsync(sourceToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }

                sourceTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                token.ThrowIfCancellationRequested();
                ValidateFile(temporaryPath, component);
                PublishTemporaryFile(temporaryPath, cachePath);
                ValidateFile(cachePath, component);
                _logger.Info(
                    $"Managed component {component.Id} downloaded and verified from " +
                    $"{SanitizeUri(effectiveUri)}.");
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                RecordSourceFailure(
                    component,
                    sourceNumber,
                    $"request timed out after {DownloadSourceTimeout.TotalSeconds:0} seconds at " +
                    $"{SanitizeUri(source)}",
                    failures);
            }
            catch (HttpIOException ex)
            {
                RecordSourceFailure(
                    component,
                    sourceNumber,
                    $"HTTP body error {ex.HttpRequestError} at {SanitizeUri(source)}",
                    failures);
            }
            catch (HttpRequestException ex)
            {
                RecordSourceFailure(
                    component,
                    sourceNumber,
                    DescribeTransportFailure(source, ex),
                    failures);
            }
            catch (InvalidDataException ex)
            {
                RecordSourceFailure(
                    component,
                    sourceNumber,
                    ex.Message,
                    failures);
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }
        }

        throw new HttpRequestException(
            $"All {component.DownloadUris.Count} official download sources failed for managed " +
            $"component {component.Id}: {string.Join("; ", failures)}");
    }

    private void RecordSourceFailure(
        ManagedComponentDescriptor component,
        int sourceNumber,
        string failure,
        List<string> failures)
    {
        failures.Add(failure);
        var hasFallback = sourceNumber < component.DownloadUris.Count;
        _logger.Warn(
            $"Managed component {component.Id} source {sourceNumber}/" +
            $"{component.DownloadUris.Count} failed: {failure}." +
            (hasFallback ? " Trying the next official source." : string.Empty));
    }

    private static string DescribeHttpFailure(HttpStatusCode statusCode, Uri effectiveUri) =>
        $"HTTP {(int)statusCode} {statusCode} at {SanitizeUri(effectiveUri)}";

    private static string DescribeTransportFailure(Uri source, HttpRequestException exception)
    {
        var status = exception.StatusCode is { } statusCode
            ? $"HTTP {(int)statusCode} {statusCode}"
            : "HTTP transport error";
        var socket = exception.InnerException is SocketException socketException
            ? $", SocketError={socketException.SocketErrorCode}"
            : string.Empty;
        return $"{status}{socket} at {SanitizeUri(source)}";
    }

    private static bool IsApprovedEffectiveUri(
        ManagedComponentDescriptor component,
        Uri effectiveUri) =>
        effectiveUri.IsAbsoluteUri &&
        effectiveUri.Scheme == Uri.UriSchemeHttps &&
        effectiveUri.IsDefaultPort &&
        string.IsNullOrEmpty(effectiveUri.UserInfo) &&
        component.DownloadUris.Any(candidate =>
            string.Equals(candidate.Host, effectiveUri.Host, StringComparison.OrdinalIgnoreCase));

    private static string SanitizeUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri) return "<invalid-uri>";
        var sanitized = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };
        return sanitized.Uri.GetLeftPart(UriPartial.Path);
    }

    private static async Task AtomicCopyAsync(
        string sourcePath,
        string destinationPath,
        ManagedComponentDescriptor component,
        CancellationToken token)
    {
        ValidateFile(sourcePath, component);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException(
                $"Managed component destination has no parent directory: {destinationPath}");
        EnsureOrdinaryDirectory(destinationDirectory);
        var temporaryPath = CreateTemporarySiblingPath(destinationPath, "install");
        try
        {
            await using (var input = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = OpenTemporaryOutput(temporaryPath))
            {
                await input.CopyToAsync(output, 128 * 1024, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            ValidateFile(temporaryPath, component);
            token.ThrowIfCancellationRequested();
            PublishTemporaryFile(temporaryPath, destinationPath);
            ValidateFile(destinationPath, component);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static async Task CopyExactAsync(
        Stream input,
        Stream output,
        long expectedSize,
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
                throw new InvalidDataException("Managed component download exceeds its expected size.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        if (total != expectedSize)
        {
            throw new InvalidDataException("Managed component download is shorter than its expected size.");
        }
    }

    private static FileStream OpenTemporaryOutput(string path) =>
        new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);

    private static string CreateTemporarySiblingPath(string destinationPath, string operation) =>
        Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            $".{Path.GetFileName(destinationPath)}.{operation}.{Guid.NewGuid():N}.tmp");

    private static void PublishTemporaryFile(string temporaryPath, string destinationPath)
    {
        EnsureOrdinaryFileIfPresent(destinationPath);
        if (File.Exists(destinationPath))
        {
            File.Replace(
                temporaryPath,
                destinationPath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporaryPath, destinationPath);
        }
    }

    private static void RejectConflictingFtbEssentials(
        string modsDirectory,
        string managedFileName)
    {
        var conflict = Directory
            .EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path =>
                Path.GetFileName(path).StartsWith("ftb-essentials", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetFileName(path), managedFileName, StringComparison.OrdinalIgnoreCase));
        if (conflict is not null)
        {
            throw new InvalidOperationException(
                $"A conflicting FTB Essentials JAR is already present: {Path.GetFileName(conflict)}");
        }
    }

    private static ManagedComponentDescriptor ValidateDescriptor(
        ManagedComponentDescriptor component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (string.IsNullOrWhiteSpace(component.Id) ||
            component.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(Path.GetFileName(component.Id), component.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("Managed component ID is invalid.", nameof(component));
        }
        if (component.FileId <= 0)
        {
            throw new ArgumentException("Managed component file ID is invalid.", nameof(component));
        }
        if (string.IsNullOrWhiteSpace(component.FileName) ||
            !component.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
            component.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(Path.GetFileName(component.FileName), component.FileName, StringComparison.Ordinal))
        {
            throw new ArgumentException("Managed component file name is invalid.", nameof(component));
        }
        if (component.DownloadUris is null || component.DownloadUris.Count == 0)
        {
            throw new ArgumentException(
                "Managed component must have at least one download URL.",
                nameof(component));
        }
        var downloadUris = component.DownloadUris.ToArray();
        foreach (var downloadUri in downloadUris)
        {
            if (downloadUri is null ||
                !downloadUri.IsAbsoluteUri ||
                downloadUri.Scheme != Uri.UriSchemeHttps ||
                !downloadUri.IsDefaultPort ||
                !string.IsNullOrEmpty(downloadUri.UserInfo) ||
                string.IsNullOrWhiteSpace(downloadUri.Host))
            {
                throw new ArgumentException(
                    "Managed component URLs must be absolute and use HTTPS.",
                    nameof(component));
            }
        }
        if (downloadUris
            .Select(uri => uri.AbsoluteUri)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != downloadUris.Length)
        {
            throw new ArgumentException(
                "Managed component download URLs must be unique.",
                nameof(component));
        }
        if (component.SizeBytes <= 0)
        {
            throw new ArgumentException("Managed component size is invalid.", nameof(component));
        }
        if (component.Sha256.Length != 64 ||
            component.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Managed component SHA-256 is invalid.", nameof(component));
        }
        return component with
        {
            DownloadUris = Array.AsReadOnly(downloadUris),
            Sha256 = component.Sha256.ToLowerInvariant()
        };
    }

    private static bool IsValidFile(
        string path,
        ManagedComponentDescriptor component)
    {
        try
        {
            ValidateFile(path, component);
            return true;
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or
                InvalidDataException or
                IOException or
                UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ValidateFile(
        string path,
        ManagedComponentDescriptor component)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException(
                $"Managed component {component.Id} was not found.",
                path);
        }
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Managed component cannot be a reparse point: {path}");
        }
        if (file.Length != component.SizeBytes)
        {
            throw new InvalidDataException(
                $"Managed component {component.Id} size does not match the pinned artifact.");
        }
        var actualHash = HashFile(path);
        if (!string.Equals(actualHash, component.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Managed component {component.Id} SHA-256 does not match the pinned artifact.");
        }
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void EnsureOrdinaryDirectory(
        string path,
        bool createIfMissing = true)
    {
        if (createIfMissing) Directory.CreateDirectory(path);
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException($"Directory does not exist: {path}");
        }
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Managed component directory cannot be a reparse point: {path}");
        }
    }

    private static void EnsureOrdinaryFileIfPresent(string path)
    {
        if (!File.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Managed component file cannot be a reparse point: {path}");
        }
    }

    private static bool IsUnderDirectory(string path, string parent)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullParent = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullPath, fullParent, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(
                   fullParent + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}

public sealed record ManagedComponentInstallResult(
    string ComponentId,
    long FileId,
    string InstalledPath,
    string CachePath,
    bool Downloaded,
    bool Installed,
    bool CachePopulated);

internal sealed record ManagedComponentDescriptor(
    string Id,
    long FileId,
    string FileName,
    IReadOnlyList<Uri> DownloadUris,
    long SizeBytes,
    string Sha256);

internal sealed record ManagedModReplacement(
    ManagedComponentDescriptor Component,
    string SupersededFileName,
    string DisplayName,
    string Version)
{
    // "the_bumblezone-7.13.2+...jar" -> "the_bumblezone-*.jar": any other
    // build of the same mod means a manual update the hotfix must respect.
    public string VersionScanPattern =>
        SupersededFileName[..SupersededFileName.IndexOf('-', StringComparison.Ordinal)] + "-*.jar";
}
