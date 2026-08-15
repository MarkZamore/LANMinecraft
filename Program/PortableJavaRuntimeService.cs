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
/// </summary>
public sealed partial class PortableJavaRuntimeService
{
    public const string PinnedRuntimeId = "temurin-25.0.3+9";
    public const string PinnedJavaVersion = "25.0.3";
    public const string InstallDirectoryName = "java-25";
    public const string ArchiveFileName = "OpenJDK25U-jdk_x64_windows_hotspot_25.0.3_9.zip";
    public const long ArchiveSizeBytes = 141_131_903;
    public const string ArchiveSha256 =
        "709312cd0420296d9b9de917fe6e28a5b979e875ee5ab91783fb79bcd5857235";

    // Temurin packs everything under one versioned directory; it is stripped so
    // the install looks like Mojang's components and carries no '+' in its path.
    private const string ArchiveRootPrefix = "jdk-25.0.3+9/";
    private const string MarkerFileName = ".portable-java.json";
    private const int MarkerSchemaVersion = 1;

    // Measured from the extracted image, with headroom for the archive copy.
    private const long RequiredFreeSpaceBytes = 700L * 1024 * 1024;
    private static readonly TimeSpan DownloadSourceTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FlagProbeTimeout = TimeSpan.FromSeconds(30);

    public static Uri ReleaseDownloadUri { get; } = new(
        "https://github.com/adoptium/temurin25-binaries/releases/download/jdk-25.0.3%2B9/" +
        "OpenJDK25U-jdk_x64_windows_hotspot_25.0.3_9.zip",
        UriKind.Absolute);

    public static Uri ApiDownloadUri { get; } = new(
        "https://api.adoptium.net/v3/binary/version/jdk-25.0.3%2B9/windows/x64/jdk/hotspot/normal/eclipse",
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
    public async Task<PreparedJavaRuntime> EnsureAsync(
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
            return installed;
        }

        progress?.Report(new RuntimePreparationProgress(
            RuntimePreparationStage.InstallingJava,
            $"Установка Java {_pin.JavaVersion}"));

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
            $"Распаковка Java {_pin.JavaVersion}"));
        Install(cachePath, installRoot, token);

        return TryDescribeInstalled(installRoot)
            ?? throw new InvalidDataException(
                $"Java runtime {_pin.RuntimeId} did not verify after installation.");
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
                marker.SchemaVersion != MarkerSchemaVersion ||
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
                    "Скачивание Java 25",
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
            if (_pin.VerifyFlags)
            {
                VerifyLaunchFlags(java);
            }

            File.WriteAllText(
                Path.Combine(stageRoot, MarkerFileName),
                JsonSerializer.Serialize(new JavaRuntimeMarker
                {
                    SchemaVersion = MarkerSchemaVersion,
                    RuntimeId = _pin.RuntimeId,
                    ArchiveSha256 = _pin.ArchiveSha256,
                    JavaVersion = _pin.JavaVersion,
                    InstalledAtUtc = DateTimeOffset.UtcNow
                }));

            if (Directory.Exists(installRoot)) Directory.Delete(installRoot, recursive: true);
            Directory.Move(stageRoot, installRoot);
            _logger.Info($"Java runtime {_pin.RuntimeId} installed at {installRoot}.");
        }
        finally
        {
            TryDeleteDirectory(stageRoot);
        }
    }

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
