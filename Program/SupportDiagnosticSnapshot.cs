using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

public sealed record SupportDiagnosticSnapshotRequest(
    AppPaths Paths,
    string PackRelativePath,
    string PackHash,
    SteamDiagnosticContext Steam,
    IReadOnlyDictionary<string, string> RuntimeState,
    string? JavaExecutable = null);

/// <summary>
/// The Steam side of a support bundle: who is signed in and which friends are
/// reachable. It replaces the interface list, route table and neighbour dump
/// the VPN transport needed to explain itself.
/// </summary>
public sealed record SteamDiagnosticContext(
    string SteamId64,
    string PersonaName,
    string Availability,
    int FriendCount,
    int PeerCount)
{
    public static SteamDiagnosticContext Unavailable { get; } =
        new(string.Empty, string.Empty, "NotStarted", 0, 0);
}

public sealed record SupportEnvironmentSnapshot(
    DateTimeOffset CapturedAtUtc,
    string LauncherVersion,
    string ReleaseNumber,
    string Framework,
    string OperatingSystem,
    string Architecture,
    string JavaVersion,
    string PackName,
    string PackHash,
    IReadOnlyList<SupportModSnapshot> Mods,
    SteamDiagnosticContext Steam,
    IReadOnlyDictionary<string, string> RuntimeState,
    string SocketTable);

public sealed record SupportModSnapshot(
    string FileName,
    long Size,
    string Version);

internal sealed record SupportVersionFallback(
    string MinecraftVersion,
    string JavaVersion,
    string ProfileId)
{
    public static SupportVersionFallback Empty { get; } =
        new(string.Empty, string.Empty, string.Empty);
}

public sealed record SupportNetworkMetrics(
    DateTimeOffset CapturedAtUtc,
    string SteamId64,
    string SteamAvailability,
    int FriendCount,
    int PeerCount,
    bool TransferActive,
    long DiagnosticBytesSent,
    long DiagnosticBytesReceived,
    long DiagnosticReconnects,
    long DiagnosticDecodeErrors,
    long WorkingSetBytes,
    double ProcessCpuSeconds,
    IReadOnlyDictionary<string, string> State);

public static partial class SupportDiagnosticSnapshotBuilder
{
    private const int MaxCommandOutputCharacters = 2 * 1024 * 1024;
    // A cache generation, not a data format: it is bumped to throw the cached
    // work away and redo it, so it is deliberately independent of
    // PortableFormat's version - a release must not cost every player a
    // re-download for an unrelated change.
    private const int RuntimeStateCacheGeneration = 3;
    private const int MaxRuntimeStateBytes = 4 * 1024 * 1024;
    private const int MaxJavaReleaseBytes = 64 * 1024;
    private const string RuntimeStateFileName = ".portable-runtime.json";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions RuntimeStateJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

    public static async Task<SupportEnvironmentSnapshot> CaptureAsync(
        SupportDiagnosticSnapshotRequest request,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Paths);
        ArgumentNullException.ThrowIfNull(request.Steam);

        var packRelativePath = request.PackRelativePath ?? string.Empty;
        var versionFallback = ResolveReadOnlyVersionFallback(
            request.Paths,
            packRelativePath);
        var runtimeState = MergeRuntimeVersionFallback(
            request.RuntimeState,
            versionFallback);
        // The socket table still helps with "the game cannot reach anything"
        // reports; the route, ARP and interface dumps described a transport
        // that no longer exists.
        var commands = await Task.WhenAll(
            RunSystemCommandAsync("netstat.exe", "-ano", token),
            ReadJavaVersionAsync(request.JavaExecutable, token)).ConfigureAwait(false);
        var javaVersion = string.IsNullOrWhiteSpace(commands[1])
            ? versionFallback.JavaVersion
            : commands[1];

        return new SupportEnvironmentSnapshot(
            DateTimeOffset.UtcNow,
            ResolveLauncherVersion(),
            ResolveAssemblyMetadata("ReleaseNumber"),
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            javaVersion,
            Path.GetFileName(packRelativePath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)) ?? string.Empty,
            request.PackHash ?? string.Empty,
            ReadMods(request.Paths, packRelativePath),
            request.Steam,
            runtimeState,
            commands[0]);
    }

    public static SupportNetworkMetrics CaptureMetrics(
        SteamDiagnosticContext steam,
        bool transferActive,
        long diagnosticBytesSent,
        long diagnosticBytesReceived,
        long reconnects,
        long decodeErrors,
        IReadOnlyDictionary<string, string>? state = null)
    {
        var process = Process.GetCurrentProcess();
        return new SupportNetworkMetrics(
            DateTimeOffset.UtcNow,
            steam.SteamId64,
            steam.Availability,
            Math.Max(0, steam.FriendCount),
            Math.Max(0, steam.PeerCount),
            transferActive,
            Math.Max(0, diagnosticBytesSent),
            Math.Max(0, diagnosticBytesReceived),
            Math.Max(0, reconnects),
            Math.Max(0, decodeErrors),
            process.WorkingSet64,
            process.TotalProcessorTime.TotalSeconds,
            state is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(state, StringComparer.Ordinal));
    }

    private static string Truncate(string? value, int maximumLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maximumLength
            ? value ?? string.Empty
            : value[..maximumLength];

    private static SupportModSnapshot[] ReadMods(
        AppPaths paths,
        string packRelativePath)
    {
        if (string.IsNullOrWhiteSpace(packRelativePath))
        {
            return [];
        }

        var roots = new[]
        {
            Path.Combine(paths.CombineUnderPacks(packRelativePath), "mods"),
            Path.Combine(paths.CombineUnderInstances(packRelativePath), "mods")
        };
        return roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.jar", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                try
                {
                    var file = new FileInfo(path);
                    return new SupportModSnapshot(file.Name, file.Length, ReadJarVersion(path));
                }
                catch (IOException)
                {
                    return new SupportModSnapshot(Path.GetFileName(path), 0, string.Empty);
                }
                catch (UnauthorizedAccessException)
                {
                    return new SupportModSnapshot(Path.GetFileName(path), 0, string.Empty);
                }
            })
            .ToArray();
    }

    private static string ReadJarVersion(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            foreach (var metadataName in new[]
                     {
                         "fabric.mod.json",
                         "quilt.mod.json"
                     })
            {
                var metadata = archive.GetEntry(metadataName);
                if (metadata is null || metadata.Length > 1024 * 1024) continue;
                using var document = JsonDocument.Parse(metadata.Open());
                JsonElement version;
                if (metadataName.Equals(
                        "quilt.mod.json",
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (!document.RootElement.TryGetProperty(
                            "quilt_loader",
                            out var loader) ||
                        !loader.TryGetProperty("version", out version))
                    {
                        continue;
                    }
                }
                else if (!document.RootElement.TryGetProperty(
                             "version",
                             out version))
                {
                    continue;
                }

                if (version.ValueKind == JsonValueKind.String &&
                    version.GetString() is { Length: > 0 } declaredVersion)
                {
                    return declaredVersion;
                }
            }

            foreach (var metadataName in new[]
                     {
                         "META-INF/neoforge.mods.toml",
                         "META-INF/mods.toml"
                     })
            {
                var metadata = archive.GetEntry(metadataName);
                if (metadata is null || metadata.Length > 1024 * 1024) continue;
                using var metadataReader = new StreamReader(metadata.Open());
                var match = ModVersionRegex().Match(metadataReader.ReadToEnd());
                if (match.Success &&
                    !match.Groups["value"].Value.StartsWith(
                        "${",
                        StringComparison.Ordinal))
                {
                    return match.Groups["value"].Value.Trim();
                }
            }

            var manifest = archive.GetEntry("META-INF/MANIFEST.MF");
            if (manifest is null) return string.Empty;
            using var manifestReader = new StreamReader(manifest.Open());
            while (manifestReader.ReadLine() is { } line)
            {
                foreach (var prefix in new[]
                         {
                             "Implementation-Version:",
                             "Specification-Version:",
                             "Mod-Version:"
                         })
                {
                    if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return line[prefix.Length..].Trim();
                    }
                }
            }
        }
        catch (InvalidDataException)
        {
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        return string.Empty;
    }

    [GeneratedRegex(
        """(?im)^\s*version\s*=\s*["'](?<value>[^"'\r\n]+)["']""",
        RegexOptions.CultureInvariant)]
    private static partial Regex ModVersionRegex();

    internal static SupportVersionFallback ResolveReadOnlyVersionFallback(
        AppPaths paths,
        string packRelativePath)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (string.IsNullOrWhiteSpace(packRelativePath))
        {
            return SupportVersionFallback.Empty;
        }

        var minecraftVersion = string.Empty;
        try
        {
            var packDirectory = paths.CombineUnderPacks(packRelativePath);
            var descriptor = PackManifestService.Load(packDirectory);
            minecraftVersion = descriptor.MinecraftVersion;
            var runtimeRoot = paths.CombineUnderRuntimes(packRelativePath);
            var statePath = Path.Combine(runtimeRoot, RuntimeStateFileName);
            if (!IsSafeExistingFile(runtimeRoot, statePath, MaxRuntimeStateBytes))
            {
                return new SupportVersionFallback(
                    minecraftVersion,
                    string.Empty,
                    string.Empty);
            }

            DiagnosticRuntimeState? state;
            using (var stream = OpenBoundedReadOnly(
                       statePath,
                       MaxRuntimeStateBytes))
            {
                state = JsonSerializer.Deserialize<DiagnosticRuntimeState>(
                    stream,
                    RuntimeStateJsonOptions);
            }
            if (state is null ||
                state.SchemaVersion != RuntimeStateCacheGeneration ||
                !string.Equals(
                    state.DescriptorHash,
                    descriptor.DescriptorHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new SupportVersionFallback(
                    minecraftVersion,
                    string.Empty,
                    string.Empty);
            }

            var javaExecutable = ResolveRuntimeStatePath(
                runtimeRoot,
                state.JavaPathRelativePath);
            if (!IsSafeExistingFile(runtimeRoot, javaExecutable, int.MaxValue))
            {
                return new SupportVersionFallback(
                    minecraftVersion,
                    string.Empty,
                    NormalizeProfileId(state.ProfileId));
            }

            var javaHome = Directory.GetParent(javaExecutable)?.Parent?.FullName;
            if (string.IsNullOrWhiteSpace(javaHome))
            {
                return new SupportVersionFallback(
                    minecraftVersion,
                    string.Empty,
                    NormalizeProfileId(state.ProfileId));
            }
            var releasePath = Path.Combine(javaHome, "release");
            var javaVersion = IsSafeExistingFile(
                    runtimeRoot,
                    releasePath,
                    MaxJavaReleaseBytes)
                ? ReadJavaReleaseVersion(releasePath)
                : string.Empty;
            return new SupportVersionFallback(
                minecraftVersion,
                javaVersion,
                NormalizeProfileId(state.ProfileId));
        }
        catch (Exception ex) when (ex is IOException or
                                   InvalidDataException or
                                   JsonException or
                                   UnauthorizedAccessException or
                                   ArgumentException or
                                   NotSupportedException)
        {
            return string.IsNullOrWhiteSpace(minecraftVersion)
                ? SupportVersionFallback.Empty
                : new SupportVersionFallback(
                    minecraftVersion,
                    string.Empty,
                    string.Empty);
        }
    }

    internal static Dictionary<string, string> MergeRuntimeVersionFallback(
        IReadOnlyDictionary<string, string>? runtimeState,
        SupportVersionFallback fallback)
    {
        var result = runtimeState is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(runtimeState, StringComparer.Ordinal);
        if ((!result.TryGetValue("game.version", out var gameVersion) ||
             string.IsNullOrWhiteSpace(gameVersion)) &&
            !string.IsNullOrWhiteSpace(fallback.MinecraftVersion))
        {
            result["game.version"] = fallback.MinecraftVersion;
        }
        if ((!result.TryGetValue("game.profile", out var profileId) ||
             string.IsNullOrWhiteSpace(profileId)) &&
            !string.IsNullOrWhiteSpace(fallback.ProfileId))
        {
            result["game.profile"] = fallback.ProfileId;
        }
        return result;
    }

    private static string ResolveRuntimeStatePath(
        string runtimeRoot,
        string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException(
                "Runtime state contains an invalid Java path.");
        }
        var root = Path.GetFullPath(runtimeRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!full.StartsWith(
                root + Path.DirectorySeparatorChar,
                comparison))
        {
            throw new InvalidDataException(
                "Runtime state Java path escapes the selected runtime.");
        }
        return full;
    }

    private static bool IsSafeExistingFile(
        string runtimeRoot,
        string path,
        long maximumBytes)
    {
        try
        {
            var root = Path.GetFullPath(runtimeRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(path);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!full.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    comparison))
            {
                return false;
            }

            var relative = Path.GetRelativePath(root, full);
            var current = root;
            if (HasReparsePoint(current)) return false;
            foreach (var segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (HasReparsePoint(current)) return false;
            }

            var file = new FileInfo(full);
            return file.Exists &&
                   file.Length >= 0 &&
                   file.Length <= maximumBytes;
        }
        catch (Exception ex) when (ex is IOException or
                                   UnauthorizedAccessException or
                                   ArgumentException or
                                   NotSupportedException)
        {
            return false;
        }
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or
                                   UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static FileStream OpenBoundedReadOnly(
        string path,
        long maximumBytes)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
        {
            stream.Dispose();
            throw new InvalidDataException(
                "Diagnostic runtime metadata exceeds its read limit.");
        }
        return stream;
    }

    private static string ReadJavaReleaseVersion(string releasePath)
    {
        try
        {
            using var stream = OpenBoundedReadOnly(
                releasePath,
                MaxJavaReleaseBytes);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                var match = JavaReleaseVersionRegex().Match(line);
                if (!match.Success) continue;
                var value = match.Groups["value"].Value.Trim();
                return value.Length <= 128 && value.All(character =>
                    !char.IsControl(character))
                    ? value
                    : string.Empty;
            }
        }
        catch (Exception ex) when (ex is IOException or
                                   InvalidDataException or
                                   UnauthorizedAccessException)
        {
        }
        return string.Empty;
    }

    private static string NormalizeProfileId(string? profileId)
    {
        var value = profileId?.Trim() ?? string.Empty;
        return value.Length is > 0 and <= 256 &&
               value.All(character => !char.IsControl(character))
            ? value
            : string.Empty;
    }

    [GeneratedRegex(
        """^\s*JAVA_VERSION\s*=\s*["']?(?<value>[A-Za-z0-9._+\-]+)["']?\s*$""",
        RegexOptions.CultureInvariant)]
    private static partial Regex JavaReleaseVersionRegex();

    private static async Task<string> ReadJavaVersionAsync(
        string? javaExecutable,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(javaExecutable) || !File.Exists(javaExecutable))
        {
            return string.Empty;
        }
        return await RunCommandAsync(
            javaExecutable,
            "-version",
            includeStandardError: true,
            token).ConfigureAwait(false);
    }

    private sealed class DiagnosticRuntimeState
    {
        public int SchemaVersion { get; set; }
        public string DescriptorHash { get; set; } = string.Empty;
        public string ProfileId { get; set; } = string.Empty;
        public string JavaPathRelativePath { get; set; } = string.Empty;
    }

    private static async Task<string> RunSystemCommandAsync(
        string executableName,
        string arguments,
        CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return string.Empty;
        var executable = Path.Combine(Environment.SystemDirectory, executableName);
        if (!File.Exists(executable)) return string.Empty;
        var output = await RunCommandAsync(
            executable,
            arguments,
            includeStandardError: false,
            token).ConfigureAwait(false);

        return string.Equals(executableName, "netstat.exe", StringComparison.OrdinalIgnoreCase)
            ? FilterSocketTable(output)
            : output;
    }

    private static async Task<string> RunCommandAsync(
        string executable,
        string arguments,
        bool includeStandardError,
        CancellationToken token)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }
            };
            if (!process.Start()) return string.Empty;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(CommandTimeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var result = includeStandardError
                ? string.Join(Environment.NewLine, new[] { stdout, stderr }
                    .Where(value => !string.IsNullOrWhiteSpace(value)))
                : stdout;
            return result.Length <= MaxCommandOutputCharacters
                ? result.Trim()
                : result[..MaxCommandOutputCharacters].Trim();
        }
        catch (Exception ex) when (ex is IOException or
                                   InvalidOperationException or
                                   OperationCanceledException or
                                   UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Only this process's own sockets are worth keeping: Steam picks its relay
    /// ports at runtime, so there is no fixed port to grep for any more.
    /// </summary>
    private static string FilterSocketTable(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return string.Empty;
        var processId = Environment.ProcessId.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        return string.Join(
            Environment.NewLine,
            output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.TrimEnd().EndsWith(processId, StringComparison.Ordinal))
                .Take(4096));
    }

    private static string ResolveLauncherVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion ??
               assembly.GetName().Version?.ToString() ??
               string.Empty;
    }

    private static string ResolveAssemblyMetadata(string key) =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute =>
                string.Equals(attribute.Key, key, StringComparison.OrdinalIgnoreCase))
            ?.Value ?? string.Empty;

    private static int TryGetInterfaceIndex(
        IPInterfaceProperties properties,
        System.Net.Sockets.AddressFamily family)
    {
        try
        {
            return family == System.Net.Sockets.AddressFamily.InterNetwork
                ? properties.GetIPv4Properties()?.Index ?? 0
                : properties.GetIPv6Properties()?.Index ?? 0;
        }
        catch (NetworkInformationException)
        {
            return 0;
        }
    }

    private static bool IsPhysicalType(NetworkInterfaceType type) =>
        type is NetworkInterfaceType.Ethernet or
            NetworkInterfaceType.GigabitEthernet or
            NetworkInterfaceType.Wireless80211;
}
