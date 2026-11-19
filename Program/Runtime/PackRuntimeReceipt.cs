using System.IO;
using System.Text;
using System.Text.Json;

namespace Minecraft;

/// <summary>Reads the on-disk runtime receipt and supplies evidence to its consumers.</summary>
internal sealed class PackRuntimeReceipt : IDisposable
{
    internal const string FileName = ".portable-runtime.json";

    // A cache generation invalidates the prepared work, independently of the
    // portable data format or launcher release.
    internal const int CacheGeneration = 6;
    internal const int MaximumBytes = 32 * 1024 * 1024;
    private static readonly char[] InvalidPathCharacters = Path.GetInvalidFileNameChars();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly JsonDocument _document;
    private JsonElement Root => _document.RootElement;

    private PackRuntimeReceipt(JsonDocument document) => _document = document;

    internal static PackRuntimeReceipt? TryRead(string path, int maximumBytes = MaximumBytes,
        FileShare share = FileShare.Read)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, share);
            var length = stream.Length;
            if (length > maximumBytes) return null;
            var bytes = new byte[(int)length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) return null;

            // Support the same BOM detection as the former text readers.
            using var snapshot = new MemoryStream(bytes, writable: false);
            using var text = new StreamReader(snapshot, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var document = JsonDocument.Parse(text.ReadToEnd());
            if (document.RootElement.ValueKind == JsonValueKind.Object) return new(document);
            document.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                   ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static PackRuntimeReceiptState? ReadPreparedState(string path)
    {
        using var receipt = TryRead(path);
        var state = receipt?.ReadModel<PackRuntimeReceiptState>();
        return state?.Files is { } files && !files.Values.Any(static file => file is null) ? state : null;
    }

    internal static void WritePreparedState(string path, PackRuntimeReceiptState state) =>
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(state, Json));

    internal VersionMetadata? ReadVersionMetadata() => ReadModel<VersionMetadata>();

    // Consumers that might remove cache files require the explicit on-disk
    // fields. A missing field must not acquire a default from the launch DTO.
    internal int? Generation =>
        Root.TryGetProperty("schemaVersion", out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var generation) ? generation : null;

    internal string? DescriptorHash => TryReadString("descriptorHash", out var hash) ? hash : null;
    internal string? JavaRuntimeId => TryReadString("javaRuntimeId", out var runtimeId) ? runtimeId : null;
    internal bool TryReadProfileId(out string? profileId) => TryReadString("profileId", out profileId);

    internal bool TryReadFiles(out IEnumerable<FileReference> files)
    {
        var present = Root.TryGetProperty("files", out var value) && value.ValueKind == JsonValueKind.Object;
        files = present ? Enumerate(value) : [];
        return present;

        static IEnumerable<FileReference> Enumerate(JsonElement table)
        {
            foreach (var entry in table.EnumerateObject())
                yield return new(entry.Name, entry.Value.ValueKind == JsonValueKind.Object);
        }
    }

    // The older layout also stored paths relative to this pack's private
    // runtime. Report both spellings relative to the private directory.
    // Callers must discard the projection if a later reference is invalid.
    internal bool TryReadPrivateReferences(string pack, Action checkpoint, Action<string> record)
    {
        if (Generation is not int generation || generation <= 0 || generation > CacheGeneration ||
            !TryReadFiles(out var files)) return false;
        var localPrefix = LauncherStorageLayout.PackRuntimes + "/" + pack + "/";
        var legacyPrefix = LauncherStorageLayout.LegacyPackRuntimes + "/" + pack + "/";
        var hasFiles = false;
        foreach (var file in files)
        {
            checkpoint();
            hasFiles = true;
            if (!file.HasProof || !Record(file.RelativePath)) return false;
        }

        return (generation != CacheGeneration || hasFiles) &&
               TryReadString("clientJarRelativePath", out var client) && (string.IsNullOrEmpty(client) || Record(client)) &&
               TryReadString("javaPathRelativePath", out var java) && (string.IsNullOrEmpty(java) || Record(java));

        bool Record(string relative)
        {
            var normalized = relative.Replace('\\', '/');
            if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(part =>
                part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(InvalidPathCharacters) >= 0)) return false;
            if (normalized.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase))
                normalized = normalized[localPrefix.Length..];
            else if (normalized.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase))
                normalized = normalized[legacyPrefix.Length..];
            record(normalized);
            return true;
        }
    }

    internal static string ResolveLauncherPath(AppPaths paths, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Runtime state contains an invalid path.");
        var root = Path.GetFullPath(paths.Launcher).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var portablePath = paths.ResolvePackRuntimeReceiptPrefix(relativePath);
        var full = Path.GetFullPath(Path.Combine(root, portablePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Runtime state path escapes runtime root.");
        return full;
    }

    private bool TryReadString(string name, out string? result)
    {
        result = null;
        if (!Root.TryGetProperty(name, out var value)) return true;
        if (value.ValueKind != JsonValueKind.String) return false;
        result = value.GetString();
        return true;
    }

    private T? ReadModel<T>() where T : class
    {
        try { return Root.Deserialize<T>(Json); }
        catch (JsonException) { return null; }
    }

    public void Dispose() => _document.Dispose();

    internal readonly record struct FileReference(string RelativePath, bool HasProof);

    internal sealed class VersionMetadata
    {
        public int SchemaVersion { get; set; }
        public string DescriptorHash { get; set; } = "";
        public string ProfileId { get; set; } = "";
        public string JavaPathRelativePath { get; set; } = "";
        public string JavaVersion { get; set; } = "";
    }
}

internal sealed class PackRuntimeReceiptState
{
    public int SchemaVersion { get; set; } = PackRuntimeReceipt.CacheGeneration;
    public string DescriptorHash { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string JavaPathRelativePath { get; set; } = "";

    // Keep the prepared Java version in the receipt, including when the
    // shared Java install is outside this pack's runtime directory.
    public string JavaVersion { get; set; } = "";
    public string JavaRuntimeId { get; set; } = "";
    public string ClientJarRelativePath { get; set; } = "";
    public long SourceClientJarSizeBytes { get; set; }
    public long SourceClientJarLastWriteUtcTicks { get; set; }
    public string SourceClientJarSha1 { get; set; } = "";
    public DateTimeOffset PreparedAtUtc { get; set; }
    public Dictionary<string, RuntimeFileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class RuntimeFileState
{
    public long SizeBytes { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public string Sha256 { get; set; } = "";
}
