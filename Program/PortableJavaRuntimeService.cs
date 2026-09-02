using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

/// <summary>
/// Installs the pinned Java runtime the game runs on. Minecraft's own metadata
/// still names Mojang's Java 21 component, which keeps driving the loader
/// installer, while the client itself runs on this runtime.
///
/// The pin is Java 21 because that is the version 1.21.1 and everything built
/// for it targets, and because mods may refuse anything newer: Cobblemon
/// declares javaVersion="[21,21.999999)" in its metadata, and NeoForge answers
/// a broken feature bound by refusing to load a single mod, which reads as an
/// unrelated crash deep inside another mod's static initialiser.
/// </summary>
public sealed partial class PortableJavaRuntimeService
{
    public const string PinnedRuntimeId = "temurin-21.0.12.1+1";
    /// <summary>
    /// What this runtime answers for JAVA_VERSION in its own <c>release</c>
    /// file, which is how an installed copy is recognised - not the release
    /// name and not the file name. jdk-21.0.12.1+1 reports "21.0.12.1" where
    /// jdk-21.0.12+8 reported "21.0.12", and a wrong guess here is not a wrong
    /// label but a runtime that reinstalls itself on every launch for ever.
    /// </summary>
    public const string PinnedJavaVersion = "21.0.12.1";
    public const string InstallDirectoryName = "java-21";
    public const string ArchiveFileName = "OpenJDK21U-jdk_x64_windows_hotspot_21.0.12.1_1.zip";
    public const long ArchiveSizeBytes = 205_073_461;
    public const string ArchiveSha256 =
        "f9d6e191ab098c0d416e7d588a24420a8621cd2f4720dab2459b8b7b2d2d8b4e";

    /// <summary>The feature release, which decides which JVM options the game gets.</summary>
    public static int PinnedMajorVersion { get; } =
        int.Parse(PinnedJavaVersion.Split('.')[0], CultureInfo.InvariantCulture);

    // Temurin packs everything under one versioned directory; it is stripped so
    // the install looks like Mojang's components and carries no '+' in its path.
    private const string ArchiveRootPrefix = "jdk-21.0.12.1+1/";
    private const string MarkerFileName = ".portable-java.json";
    // A cache generation, not a data format: it is bumped to throw the cached
    // work away and redo it, so it is deliberately independent of
    // PortableFormat's version - a release must not cost every player a
    // re-download for an unrelated change.
    private const int MarkerCacheGeneration = 1;

    // Measured from the extracted image, with headroom for the archive copy.
    private const long RequiredFreeSpaceBytes = 800L * 1024 * 1024;
    private static readonly TimeSpan DownloadSourceTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FlagProbeTimeout = TimeSpan.FromSeconds(30);

    public static Uri ReleaseDownloadUri { get; } = new(
        "https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.12.1%2B1/" +
        "OpenJDK21U-jdk_x64_windows_hotspot_21.0.12.1_1.zip",
        UriKind.Absolute);

    public static Uri ApiDownloadUri { get; } = new(
        "https://api.adoptium.net/v3/binary/version/jdk-21.0.12.1%2B1/windows/x64/jdk/hotspot/normal/eclipse",
        UriKind.Absolute);

    public static IReadOnlyList<Uri> DownloadUris { get; } =
        Array.AsReadOnly([ReleaseDownloadUri, ApiDownloadUri]);

    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly HttpClient _httpClient;
    private readonly JavaRuntimePin _pin;
    private readonly Func<string, long, bool> _freeSpaceProbe;

    public PortableJavaRuntimeService(AppPaths paths, Logger logger, HttpClient? httpClient = null)
        : this(paths, logger, httpClient, DefaultPin, freeSpaceProbe: null)
    {
    }

    internal PortableJavaRuntimeService(
        AppPaths paths,
        Logger logger,
        HttpClient? httpClient,
        JavaRuntimePin pin,
        Func<string, long, bool>? freeSpaceProbe = null)
    {
        _paths = paths;
        _logger = logger;
        _httpClient = httpClient ?? PortableHttpClient.Shared;
        _pin = ValidatePin(pin);
        _freeSpaceProbe = freeSpaceProbe ?? HasFreeSpace;
    }

    internal static JavaRuntimePin DefaultPin { get; } = new(
        PinnedMajorVersion,
        PinnedRuntimeId,
        PinnedJavaVersion,
        InstallDirectoryName,
        ArchiveFileName,
        ArchiveRootPrefix,
        DownloadUris,
        ArchiveSizeBytes,
        ArchiveSha256,
        RequiredFreeSpaceBytes,
        VerifyFlags: true);

    /// <summary>
    /// Returns the pinned runtime, installing it under <paramref name="runtimeRoot"/> when
    /// it is missing or damaged. Failures propagate so the launch fails closed.
    /// </summary>
    public Task<PreparedJavaRuntime> EnsureAsync(
        string runtimeRoot,
        IProgress<RuntimePreparationProgress>? progress,
        CancellationToken token) =>
        EnsureCoreAsync(runtimeRoot, progress, token);

    /// <summary>
    /// The same, for one named runtime rather than this service's own pin: a
    /// pack runs on the Java its Minecraft was built for, and two packs on one
    /// machine need not agree about which that is.
    /// </summary>
    /// <remarks>
    /// A different runtime is a different service rather than a parameter
    /// threaded through every step. Everything below - where the archive is
    /// cached, what counts as a valid one, which folder is swept as superseded,
    /// what the marker must say - reads the pin, and forty-odd places quietly
    /// taking one from an argument instead of a field is how one of them ends
    /// up still reading the other.
    /// </remarks>
    internal Task<PreparedJavaRuntime> EnsureAsync(
        string runtimeRoot,
        JavaRuntimePin pin,
        IProgress<RuntimePreparationProgress>? progress,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (string.Equals(pin.RuntimeId, _pin.RuntimeId, StringComparison.Ordinal))
        {
            return EnsureCoreAsync(runtimeRoot, progress, token);
        }

        var forThatRuntime = new PortableJavaRuntimeService(
            _paths, _logger, _httpClient, pin, _freeSpaceProbe);
        return forThatRuntime.EnsureCoreAsync(runtimeRoot, progress, token);
    }

    private async Task<PreparedJavaRuntime> EnsureCoreAsync(
        string runtimeRoot,
        IProgress<RuntimePreparationProgress>? progress,
        CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        var installRoot = Path.Combine(
            Path.GetFullPath(runtimeRoot),
            "runtime",
            "windows-x64",
            _pin.InstallDirectoryName);
        _paths.EnsureUnderRoot(installRoot);

        if (TryDescribeInstalled(installRoot) is { } installed)
        {
            RemoveSupersededRuntimes(installRoot);
            return installed;
        }

        progress?.Report(new RuntimePreparationProgress(
            RuntimePreparationStage.InstallingJava,
            $"Java {_pin.JavaVersion}"));

        var cachePath = GetCachePath();
        if (!IsValidArchive(cachePath))
        {
            if (!_freeSpaceProbe(cachePath, _pin.ArchiveSizeBytes))
            {
                throw new IOException(
                    $"Not enough free disk space to download Java {_pin.JavaVersion}: " +
                    $"{FormatMegabytes(_pin.ArchiveSizeBytes)} MB required.");
            }
            await DownloadToCacheAsync(cachePath, progress, token).ConfigureAwait(false);
        }
        else
        {
            _logger.Info($"Java runtime {_pin.RuntimeId} archive was already cached.");
        }

        if (!_freeSpaceProbe(installRoot, _pin.RequiredFreeSpaceBytes))
        {
            throw new IOException(
                $"Not enough free disk space to install Java {_pin.JavaVersion}: " +
                $"{FormatMegabytes(_pin.RequiredFreeSpaceBytes)} MB required.");
        }

        progress?.Report(new RuntimePreparationProgress(
            RuntimePreparationStage.InstallingJava,
            $"Java {_pin.JavaVersion}"));
        Install(cachePath, installRoot, token);

        var prepared = TryDescribeInstalled(installRoot)
            ?? throw new InvalidDataException(
                $"Java runtime {_pin.RuntimeId} did not verify after installation.");
        RemoveSupersededRuntimes(installRoot);
        return prepared;
    }

    /// <summary>
    /// Deletes what an earlier pin left behind: the JDK sitting beside this one
    /// and the archive cached to install it. A pinned runtime is replaced, not
    /// collected, and a superseded pair costs half a gigabyte on every disk.
    /// Only ever runs once this runtime is known good, and only over names this
    /// service writes itself - Mojang's components are named otherwise and keep
    /// driving the loader installer.
    /// </summary>
    /// <summary>
    /// Throws away Java installs and archives nothing pins any more.
    /// </summary>
    /// <remarks>
    /// What counts as superseded is every runtime in the catalogue, not just
    /// the one being asked for. They share a folder now: a machine that plays a
    /// 1.20.1 pack and a 1.21.1 pack keeps a Java 17 beside its Java 21, and a
    /// sweep that recognised only the runtime it was called about would delete
    /// the other one on every launch and download it again on the next.
    /// </remarks>
    private void RemoveSupersededRuntimes(string installRoot)
    {
        var keptInstalls = new HashSet<string>(
            JavaRuntimeCatalog.InstallDirectoryNames, StringComparer.OrdinalIgnoreCase)
        {
            _pin.InstallDirectoryName
        };
        var keptArchives = new HashSet<string>(
            JavaRuntimeCatalog.CacheDirectoryNames, StringComparer.OrdinalIgnoreCase)
        {
            _pin.RuntimeId.Replace('+', '_')
        };

        Sweep(
            Path.GetDirectoryName(installRoot),
            name => PortableInstallDirectoryRegex().IsMatch(name) && !keptInstalls.Contains(name),
            "Java install");
        Sweep(
            Path.GetDirectoryName(Path.GetDirectoryName(GetCachePath())),
            name => !keptArchives.Contains(name),
            "cached Java archive");
        SweepAbandonedStaging(Path.GetDirectoryName(installRoot));
    }

    /// <summary>
    /// Throws away staging directories a killed run left behind, whichever
    /// runtime made them rather than only this one.
    /// </summary>
    /// <remarks>
    /// Install() clears its own pin's leftovers before it starts, and that is
    /// enough for as long as the pin is still asked for. It stops being enough
    /// the moment it is not: a machine whose packs all move to 1.21.1 never
    /// installs java-17 again, so a .java-17.install.&lt;guid&gt; that a kill left
    /// behind is a few hundred megabytes nothing ever looks at again - and it
    /// counts against the free space every later install checks for.
    ///
    /// The superseded-install sweep beside this one cannot reach them: it knows
    /// names of the form java-&lt;major&gt; exactly, and a staging directory is not
    /// one of those.
    ///
    /// Anything still being written is left alone. A tree in the middle of an
    /// extraction has had a directory put in it moments ago, and an hour is far
    /// past what an extraction, a probe and a move take together - so a second
    /// launcher installing a different runtime beside this one is never robbed
    /// halfway through.
    /// </remarks>
    private void SweepAbandonedStaging(string? parent)
    {
        if (parent is null || !Directory.Exists(parent)) return;
        var stale = DateTime.UtcNow - AbandonedStagingAge;
        foreach (var directory in Directory.EnumerateDirectories(parent))
        {
            var name = Path.GetFileName(directory);
            if (!AbandonedStagingRegex().IsMatch(name)) continue;
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) > stale) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            TryDeleteDirectory(directory);
            if (!Directory.Exists(directory))
            {
                _logger.Info($"Removed the abandoned Java staging directory {name}.");
            }
        }
    }

    private static readonly TimeSpan AbandonedStagingAge = TimeSpan.FromHours(1);

    private void Sweep(string? parent, Func<string, bool> superseded, string what)
    {
        if (parent is null || !Directory.Exists(parent)) return;
        foreach (var directory in Directory.EnumerateDirectories(parent))
        {
            var name = Path.GetFileName(directory);
            if (!superseded(name)) continue;
            try
            {
                Directory.Delete(directory, recursive: true);
                _logger.Info($"Removed the superseded {what} {name}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warn($"The superseded {what} {name} could not be removed ({ex.Message}).");
            }
        }
    }

    internal string CachePath => GetCachePath();

    private string GetCachePath()
    {
        var path = Path.Combine(
            _paths.Launcher,
            "ManagedComponents",
            "java-runtime",
            _pin.RuntimeId.Replace('+', '_'),
            _pin.ArchiveFileName);
        _paths.EnsureUnderRoot(path);
        return path;
    }

    private PreparedJavaRuntime? TryDescribeInstalled(string installRoot)
    {
        try
        {
            var markerPath = Path.Combine(installRoot, MarkerFileName);
            if (!File.Exists(markerPath)) return null;
            var marker = JsonSerializer.Deserialize<JavaRuntimeMarker>(File.ReadAllText(markerPath));
            if (marker is null ||
                marker.SchemaVersion != MarkerCacheGeneration ||
                !string.Equals(marker.RuntimeId, _pin.RuntimeId, StringComparison.Ordinal) ||
                !string.Equals(marker.ArchiveSha256, _pin.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var javaw = Path.Combine(installRoot, "bin", "javaw.exe");
            var java = Path.Combine(installRoot, "bin", "java.exe");
            if (!File.Exists(javaw) ||
                !File.Exists(java) ||
                !File.Exists(Path.Combine(installRoot, "lib", "modules")) ||
                ReadReleaseJavaVersion(installRoot) != _pin.JavaVersion)
            {
                return null;
            }

            return new PreparedJavaRuntime(installRoot, javaw, java, _pin.RuntimeId, _pin.JavaVersion);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.Warn($"Installed Java runtime could not be inspected: {ex.Message}");
            return null;
        }
    }

    private static string? ReadReleaseJavaVersion(string installRoot)
    {
        var releasePath = Path.Combine(installRoot, "release");
        if (!File.Exists(releasePath)) return null;
        foreach (var line in File.ReadLines(releasePath))
        {
            var match = JavaVersionRegex().Match(line);
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }

    private bool IsValidArchive(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists ||
                (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
                file.Length != _pin.ArchiveSizeBytes)
            {
                return false;
            }
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return string.Equals(hash, _pin.ArchiveSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task DownloadToCacheAsync(
        string cachePath,
        IProgress<RuntimePreparationProgress>? progress,
        CancellationToken token)
    {
        var cacheDirectory = Path.GetDirectoryName(cachePath)!;
        Directory.CreateDirectory(cacheDirectory);
        SweepAbandonedEntries(cacheDirectory, $"{_pin.ArchiveFileName}.*.tmp");
        var failures = new List<string>(_pin.DownloadUris.Count);
        for (var index = 0; index < _pin.DownloadUris.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var source = _pin.DownloadUris[index];
            var temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                using var sourceTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                sourceTimeout.CancelAfter(DownloadTimeout());
                var sourceToken = sourceTimeout.Token;
                _logger.Info($"Downloading Java runtime {_pin.RuntimeId} from {SanitizeUri(source)}.");

                using var request = new HttpRequestMessage(HttpMethod.Get, source);
                using var response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, sourceToken)
                    .ConfigureAwait(false);
                var effectiveUri = response.RequestMessage?.RequestUri ?? source;
                if (!IsApprovedEffectiveUri(effectiveUri))
                {
                    throw new InvalidDataException(
                        $"Java runtime download was redirected to {SanitizeUri(effectiveUri)}.");
                }
                if (!response.IsSuccessStatusCode)
                {
                    failures.Add($"HTTP {(int)response.StatusCode} at {SanitizeUri(effectiveUri)}");
                    continue;
                }
                if (response.Content.Headers.ContentLength is { } length &&
                    length != _pin.ArchiveSizeBytes)
                {
                    throw new InvalidDataException(
                        $"Java runtime archive has an unexpected Content-Length {length}.");
                }

                await using (var input = await response.Content.ReadAsStreamAsync(sourceToken).ConfigureAwait(false))
                await using (var output = new FileStream(
                                 temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await CopyExactAsync(input, output, _pin.ArchiveSizeBytes, progress, sourceToken)
                        .ConfigureAwait(false);
                    await output.FlushAsync(sourceToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }

                sourceTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                token.ThrowIfCancellationRequested();
                if (!IsValidArchive(temporaryPath))
                {
                    throw new InvalidDataException(
                        "Java runtime archive does not match the pinned size or SHA-256.");
                }
                if (File.Exists(cachePath))
                {
                    File.Replace(temporaryPath, cachePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, cachePath);
                }
                _logger.Info($"Java runtime {_pin.RuntimeId} downloaded and verified.");
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                failures.Add($"request timed out at {SanitizeUri(source)}");
            }
            catch (HttpRequestException ex)
            {
                var socket = ex.InnerException is SocketException socketException
                    ? $", SocketError={socketException.SocketErrorCode}"
                    : string.Empty;
                failures.Add($"HTTP transport error{socket} at {SanitizeUri(source)}");
            }
            catch (InvalidDataException ex)
            {
                failures.Add(ex.Message);
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }
        }

        throw new HttpRequestException(
            $"All {_pin.DownloadUris.Count} download sources failed for Java runtime " +
            $"{_pin.RuntimeId}: {string.Join("; ", failures)}");
    }

    private TimeSpan DownloadTimeout() =>
        DownloadSourceTimeout + TimeSpan.FromSeconds(_pin.ArchiveSizeBytes / (128d * 1024d));

    private bool IsApprovedEffectiveUri(Uri effectiveUri) =>
        effectiveUri.IsAbsoluteUri &&
        effectiveUri.Scheme == Uri.UriSchemeHttps &&
        effectiveUri.IsDefaultPort &&
        string.IsNullOrEmpty(effectiveUri.UserInfo) &&
        (_pin.DownloadUris.Any(candidate =>
             string.Equals(candidate.Host, effectiveUri.Host, StringComparison.OrdinalIgnoreCase)) ||
         effectiveUri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static async Task CopyExactAsync(
        Stream input,
        Stream output,
        long expectedSize,
        IProgress<RuntimePreparationProgress>? progress,
        CancellationToken token)
    {
        var buffer = new byte[128 * 1024];
        long total = 0;
        var lastReport = 0L;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0) break;
            total = checked(total + read);
            if (total > expectedSize)
            {
                throw new InvalidDataException("Java runtime archive exceeds its expected size.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            if (total - lastReport >= 4L * 1024 * 1024 || total == expectedSize)
            {
                lastReport = total;
                progress?.Report(new RuntimePreparationProgress(
                    RuntimePreparationStage.InstallingJava,
                    $"Java {PinnedJavaVersion}",
                    (double)total / expectedSize,
                    total,
                    expectedSize));
            }
        }
        if (total != expectedSize)
        {
            throw new InvalidDataException("Java runtime archive is shorter than its expected size.");
        }
    }

    private void Install(string archivePath, string installRoot, CancellationToken token)
    {
        var parent = Path.GetDirectoryName(installRoot)!;
        SweepAbandonedEntries(parent, $".{_pin.InstallDirectoryName}.install.*");
        var stageRoot = Path.Combine(
            parent,
            $".{_pin.InstallDirectoryName}.install.{Guid.NewGuid():N}");
        _paths.EnsureUnderRoot(stageRoot);
        try
        {
            Directory.CreateDirectory(stageRoot);
            ExtractStripped(archivePath, stageRoot, token);

            var javaw = Path.Combine(stageRoot, "bin", "javaw.exe");
            var java = Path.Combine(stageRoot, "bin", "java.exe");
            if (!File.Exists(javaw) ||
                !File.Exists(java) ||
                !File.Exists(Path.Combine(stageRoot, "lib", "modules")))
            {
                throw new InvalidDataException("Java runtime archive is missing its executables.");
            }
            var version = ReadReleaseJavaVersion(stageRoot);
            if (version != _pin.JavaVersion)
            {
                throw new InvalidDataException(
                    $"Java runtime reports version {version ?? "<unknown>"} instead of {_pin.JavaVersion}.");
            }
            // The move goes first. Nothing has been run out of the staging
            // folder yet, so Windows has no image of it mapped and the one
            // thing that made this move fail cannot have happened.
            if (Directory.Exists(installRoot)) Directory.Delete(installRoot, recursive: true);
            PublishInstall(stageRoot, installRoot);
        }
        finally
        {
            TryDeleteDirectory(stageRoot);
        }

        try
        {
            if (_pin.VerifyFlags)
            {
                VerifyLaunchFlags(Path.Combine(installRoot, "bin", "java.exe"));
            }

            // Written last, and this is what makes an install count as done:
            // TryDescribeInstalled reads the marker first and calls a runtime
            // without one no install at all. So a probe that fails leaves
            // nothing the next launch can mistake for a finished Java.
            File.WriteAllText(
                Path.Combine(installRoot, MarkerFileName),
                JsonSerializer.Serialize(new JavaRuntimeMarker
                {
                    SchemaVersion = MarkerCacheGeneration,
                    RuntimeId = _pin.RuntimeId,
                    ArchiveSha256 = _pin.ArchiveSha256,
                    JavaVersion = _pin.JavaVersion,
                    InstalledAtUtc = DateTimeOffset.UtcNow
                }));
        }
        catch
        {
            // java.exe has just been run from here, so this delete can be
            // denied for the very reason the move used to be. It does not have
            // to succeed: an unmarked tree is not an install, and the next
            // launch writes over it.
            TryDeleteDirectory(installRoot);
            throw;
        }
        _logger.Info($"Java runtime {_pin.RuntimeId} installed at {installRoot}.");
    }

    /// <summary>
    /// Moves the finished runtime into place, giving Windows a moment to let go
    /// of it first.
    /// </summary>
    /// <remarks>
    /// This used to run after java.exe had been started from inside the folder,
    /// to check the launcher's JVM options against it, and Windows keeps an
    /// executable's image mapped for a while after the process object says the
    /// process has gone. Directory.Move then failed with "Access to the path
    /// ... is denied", naming the staging folder, and the whole install was
    /// thrown away and downloaded again. Three seconds of retries were not
    /// enough, so the probe moved after the move instead and that cause is
    /// gone.
    ///
    /// The waiting stays for the one that is left: a real-time scanner reading
    /// a two hundred megabyte tree that appeared a second earlier holds files
    /// nobody in this process opened. It is a breath rather than a wall - the
    /// same move goes through moments later, which is what this waits for.
    /// </remarks>
    internal static void PublishInstall(string stageRoot, string installRoot)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(stageRoot, installRoot);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException &&
                                       attempt < PublishAttempts && !Directory.Exists(installRoot))
            {
                Thread.Sleep(PublishRetryDelay * attempt);
            }
        }
    }

    // Five tries spread over three seconds. Measured against nothing - the wait
    // that matters is a scanner's, and it is not ours to predict - so it is set
    // by what a player would forgive: three seconds of a progress line that was
    // already installing Java, against downloading it all over again.
    private const int PublishAttempts = 6;
    private const int PublishRetryDelay = 200;

    private void ExtractStripped(string archivePath, string stageRoot, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var normalizedStage = Path.GetFullPath(stageRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (!entry.FullName.StartsWith(_pin.ArchiveRootPrefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Java runtime archive contains an entry outside {_pin.ArchiveRootPrefix}: {entry.FullName}");
            }
            var relative = entry.FullName[_pin.ArchiveRootPrefix.Length..];
            if (relative.Length == 0) continue;

            var destination = Path.GetFullPath(Path.Combine(
                normalizedStage,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(normalizedStage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Java runtime archive entry escapes its destination: {entry.FullName}");
            }
            if (relative.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    /// <summary>
    /// Rejects a runtime that would refuse the launcher's JVM options, so the failure
    /// reads as a message instead of the game vanishing two seconds after launch.
    /// </summary>
    private void VerifyLaunchFlags(string javaPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = javaPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in MinecraftProcessService.JavaCompatibilityArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.ArgumentList.Add("-version");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Java runtime flag probe could not be started.");
        // Both pipes drain concurrently while waiting; a synchronous ReadToEnd
        // first would hang past the timeout on a stalled java.exe and deadlock
        // if a user-global JAVA_TOOL_OPTIONS floods the other pipe.
        var standardError = process.StandardError.ReadToEndAsync();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit((int)FlagProbeTimeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            throw new TimeoutException("Java runtime flag probe timed out.");
        }
        process.WaitForExit();
        var error = standardError.GetAwaiter().GetResult();
        standardOutput.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            var detail = error.Length > 500 ? error[^500..] : error;
            throw new InvalidDataException(
                $"The installed Java {_pin.JavaVersion} runtime rejected the launcher's JVM options: {detail}");
        }
    }

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
        (bytes / (1024d * 1024d)).ToString("0", CultureInfo.InvariantCulture);

    private static string SanitizeUri(Uri uri) => uri.IsAbsoluteUri
        ? uri.GetLeftPart(UriPartial.Path)
        : "<invalid-uri>";

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
    /// Removes staging directories and download temporaries a killed earlier run
    /// left behind; each is a few hundred megabytes nothing else ever deletes.
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

    private static JavaRuntimePin ValidatePin(JavaRuntimePin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (string.IsNullOrWhiteSpace(pin.RuntimeId) ||
            string.IsNullOrWhiteSpace(pin.JavaVersion) ||
            string.IsNullOrWhiteSpace(pin.InstallDirectoryName) ||
            pin.InstallDirectoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            string.IsNullOrWhiteSpace(pin.ArchiveFileName) ||
            pin.ArchiveFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Java runtime pin is invalid.", nameof(pin));
        }
        if (!pin.ArchiveRootPrefix.EndsWith('/'))
        {
            throw new ArgumentException("Java runtime archive prefix must end with '/'.", nameof(pin));
        }
        if (pin.DownloadUris is null || pin.DownloadUris.Count == 0 ||
            pin.DownloadUris.Any(uri =>
                uri is null ||
                !uri.IsAbsoluteUri ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !uri.IsDefaultPort ||
                !string.IsNullOrEmpty(uri.UserInfo)))
        {
            throw new ArgumentException("Java runtime URLs must be absolute and use HTTPS.", nameof(pin));
        }
        if (pin.ArchiveSizeBytes <= 0 ||
            pin.ArchiveSha256.Length != 64 ||
            pin.ArchiveSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Java runtime archive pin is invalid.", nameof(pin));
        }
        return pin with { ArchiveSha256 = pin.ArchiveSha256.ToLowerInvariant() };
    }

    [GeneratedRegex("^JAVA_VERSION=\"([^\"]+)\"")]
    private static partial Regex JavaVersionRegex();

    // What Install() names a staging directory: a dot, the install folder's
    // name, ".install." and a guid with no dashes. Anchored both ends so it can
    // only ever match something this service wrote itself.
    [GeneratedRegex(@"^\.java-[0-9]+\.install\.[0-9a-f]{32}$", RegexOptions.IgnoreCase)]
    private static partial Regex AbandonedStagingRegex();

    [GeneratedRegex("^java-[0-9]+$", RegexOptions.IgnoreCase)]
    private static partial Regex PortableInstallDirectoryRegex();

    private sealed class JavaRuntimeMarker
    {
        public int SchemaVersion { get; set; }
        public string RuntimeId { get; set; } = "";
        public string ArchiveSha256 { get; set; } = "";
        public string JavaVersion { get; set; } = "";
        public DateTimeOffset InstalledAtUtc { get; set; }
    }
}

public sealed record PreparedJavaRuntime(
    string JavaHome,
    string JavaWPath,
    string JavaPath,
    string RuntimeId,
    string JavaVersion);

internal sealed record JavaRuntimePin(
    int MajorVersion,
    string RuntimeId,
    string JavaVersion,
    string InstallDirectoryName,
    string ArchiveFileName,
    string ArchiveRootPrefix,
    IReadOnlyList<Uri> DownloadUris,
    long ArchiveSizeBytes,
    string ArchiveSha256,
    long RequiredFreeSpaceBytes,
    bool VerifyFlags);
