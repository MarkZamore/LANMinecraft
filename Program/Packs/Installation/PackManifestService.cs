using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft;

public static class PackManifestService
{
    /// <summary>
    /// Written by whoever prepares a pack, not by the launcher, so it keeps its
    /// own small number: a pack author should not have to track launcher
    /// releases to keep portable-pack.json valid.
    /// </summary>
    public const int CurrentSchemaVersion = 1;
    public const int NativeSchemaVersion = 2;
    public const string ManifestFileName = "portable-pack.json";
    internal const string NativePayloadRoot = "native";
    internal const string NativeRuntimeFeature = "native-runtime-v1";
    internal const string NativeSharedRuntimeFeature = "native-shared-runtime-v1";
    internal const string NativeSteamFeature = "native-steam-v1";
    internal const string Minecraft26WorldLayoutFeature = "minecraft-26-world-layout-v1";
    internal const string SteamWorldSharingFeature = "preserve-steam-world-settings-v1";
    internal const string PortableNativeAdapter = "portable-native";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool HasManifest(string packDirectory) =>
        Directory.Exists(packDirectory) && File.Exists(Path.Combine(packDirectory, ManifestFileName));

    public static PackRuntimeDescriptor Load(string packDirectory)
    {
        var packRoot = Path.GetFullPath(packDirectory);
        var manifestPath = Path.Combine(packRoot, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"Pack manifest is missing. Expected: {manifestPath}",
                manifestPath);
        }

        PackManifestDto manifest;
        try
        {
            using var reader = new StreamReader(File.OpenRead(manifestPath), Encoding.UTF8);
            if (reader.BaseStream.Length > PortablePackSyncService.MaximumLauncherDataBytes)
                throw new InvalidDataException("Pack descriptor exceeds the launcher metadata size limit.");
            manifest = JsonSerializer.Deserialize<PackManifestDto>(reader.ReadToEnd(), JsonOptions)
                ?? throw new InvalidDataException("Pack manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Pack manifest is not valid JSON: {manifestPath}", ex);
        }

        if (manifest.SchemaVersion is not (CurrentSchemaVersion or NativeSchemaVersion))
        {
            throw new InvalidDataException(
                $"Unsupported pack manifest schemaVersion {manifest.SchemaVersion}.");
        }

        var minecraftVersion = ValidateIdentifier(manifest.MinecraftVersion, "minecraftVersion", allowSpaces: false);
        if (string.Equals(minecraftVersion, "latest", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("minecraftVersion must be an exact version, not latest.");
        }
        if (manifest.SchemaVersion == NativeSchemaVersion)
            return LoadNative(manifest, minecraftVersion, packRoot);
        if (manifest.NativeRuntime is not null)
            throw new InvalidDataException("nativeRuntime requires portable-pack schemaVersion 2.");

        var loaderName = manifest.Loader?.Type?.Trim().ToLowerInvariant() ?? "";
        var loaderKind = loaderName switch
        {
            "vanilla" => PackLoaderKind.Vanilla,
            "forge" => PackLoaderKind.Forge,
            "neoforge" => PackLoaderKind.NeoForge,
            "fabric" => PackLoaderKind.Fabric,
            "quilt" => PackLoaderKind.Quilt,
            "legacyfabric" => PackLoaderKind.LegacyFabric,
            _ => throw new InvalidDataException(
                "loader.type must be one of: vanilla, forge, neoforge, fabric, quilt, legacyfabric.")
        };

        string? loaderVersion = null;
        if (loaderKind == PackLoaderKind.Vanilla)
        {
            if (!string.IsNullOrWhiteSpace(manifest.Loader?.Version))
            {
                throw new InvalidDataException("loader.version must be omitted for a vanilla pack.");
            }
        }
        else
        {
            loaderVersion = ValidateIdentifier(manifest.Loader?.Version, "loader.version", allowSpaces: false);
            if (string.Equals(loaderVersion, "latest", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("loader.version must be an exact version, not latest.");
            }
        }

        var clientJar = ValidateClientJarName(manifest.ClientJar);
        if (manifest.JavaMajorVersion is <= 0)
        {
            throw new InvalidDataException("javaMajorVersion must be positive.");
        }
        var canonical = $"{CurrentSchemaVersion}\n{minecraftVersion}\n{loaderKind.ToString().ToLowerInvariant()}\n{loaderVersion}\n{clientJar}";
        // Absent overrides retain the identity of every existing descriptor.
        if (manifest.JavaMajorVersion is { } selectedJava)
            canonical += "\njavaMajorVersion=" + selectedJava.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (manifest.PreserveWorldSettingsOnShare == true)
            canonical += "\npreserveWorldSettingsOnShare=true";
        var descriptorHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var descriptor = new PackRuntimeDescriptor(
            CurrentSchemaVersion,
            minecraftVersion,
            new PackLoaderDescriptor(loaderKind, loaderVersion),
            clientJar,
            descriptorHash)
        {
            JavaMajorVersion = manifest.JavaMajorVersion,
            PreserveWorldSettingsOnShare = manifest.PreserveWorldSettingsOnShare == true
        };
        return descriptor;
    }

    internal static void ValidateCompatibility(PackRuntimeDescriptor descriptor)
    {
        if (descriptor.NativeRuntime is not null) return;
        _ = JavaRuntimeCatalog.RequiredFor(descriptor);
        if (descriptor.PreserveWorldSettingsOnShare && !JavaRuntimeCatalog.SupportsWorldSharing(descriptor))
            throw new InvalidDataException("preserveWorldSettingsOnShare is unsupported for the declared Minecraft, loader and Java versions.");
    }

    internal static bool UsesJava8SteamWorldSharing(PackRuntimeDescriptor descriptor) =>
        descriptor.PreserveWorldSettingsOnShare && descriptor.Loader.Type == PackLoaderKind.Forge &&
        JavaRuntimeCatalog.RequiredFor(descriptor).MajorVersion == 8;

    private static PackRuntimeDescriptor LoadNative(PackManifestDto manifest, string minecraftVersion, string packRoot)
    {
        if (manifest.NativeRuntime is not { } native ||
            manifest.Loader is not null || manifest.ClientJar is not null || manifest.JavaMajorVersion is not null ||
            manifest.PreserveWorldSettingsOnShare is not null)
            throw new InvalidDataException("A native pack requires nativeRuntime and cannot declare Java runtime fields.");
        if (native.Platform != "windows-x64" || native.Adapter != PortableNativeAdapter)
            throw new InvalidDataException("The native runtime platform or adapter is unsupported.");
        var version = ValidateIdentifier(native.Version, "nativeRuntime.version", allowSpaces: false);
        if (version.Equals("latest", StringComparison.OrdinalIgnoreCase) ||
            version.Equals("nightly", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("nativeRuntime.version must identify an exact build.");

        var path = PortablePackSyncService.ValidateManifestPath(native.Executable,
            Path.TrimEndingDirectorySeparator(packRoot));
        if (!path.StartsWith(NativePayloadRoot + "/", StringComparison.Ordinal) ||
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("nativeRuntime.executable must be an .exe under native/.");

        var canonical = $"{NativeSchemaVersion}\n{minecraftVersion}\n{native.Platform}\n{native.Adapter}\n{version}\n{path}";
        var shared = native.SharedRuntime is null ? null : ValidateSharedRuntime(native.SharedRuntime, native.Platform, path,
            Path.TrimEndingDirectorySeparator(packRoot));
        if (shared is not null)
            canonical += $"\nsharedRuntime={shared.Id}/{shared.Version}/{shared.Archive.Sha256}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new PackRuntimeDescriptor(NativeSchemaVersion, minecraftVersion,
            new PackLoaderDescriptor(PackLoaderKind.Native, null), "", hash)
        {
            NativeRuntime = new NativePackRuntimeDescriptor(path, native.Platform, native.Adapter, version) { SharedRuntime = shared }
        };
    }

    private static NativeSharedRuntimeDescriptor ValidateSharedRuntime(NativeSharedRuntimeDescriptor shared,
        string platform, string executable, string packRoot)
    {
        if (shared.SchemaVersion != 1 || shared.Platform != platform || shared.Source is null ||
            !PackCatalogIndex.ValidSource(shared.Source) || shared.Source.Repo is "." or ".." ||
            shared.Source.Tag is "." or ".." || shared.Archive is not { } archive ||
            archive.SizeBytes is <= 0 or > PackArchiveService.MaxArchiveBytes || shared.Files is not { Count: > 0 } ||
            shared.Files.Count > PackArchiveService.MaxEntries)
            throw new InvalidDataException("Invalid shared native runtime descriptor.");
        var id = ValidateIdentifier(shared.Id, "sharedRuntime.id", allowSpaces: false);
        var version = ValidateIdentifier(shared.Version, "sharedRuntime.version", allowSpaces: false);
        if (version.Equals("latest", StringComparison.OrdinalIgnoreCase) || version.Equals("nightly", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A shared native runtime requires an exact version.");
        var name = ValidateIdentifier(archive.Name, "sharedRuntime.archive.name", allowSpaces: false);
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            name.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('.' or '-' or '_')))
            throw new InvalidDataException("A shared native runtime archive must be a release ZIP name.");
        var files = new List<NativeSharedRuntimeFile>(shared.Files.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in shared.Files)
        {
            if (file is null || file.SizeBytes < 0 || file.SizeBytes > PackArchiveService.MaxPayloadBytes - total)
                throw new InvalidDataException("Shared native runtime exceeds its size limit.");
            var relative = PackArchiveService.ValidatePayloadPath(file.Path ?? "", packRoot);
            if (!relative.StartsWith(NativePayloadRoot + "/", StringComparison.Ordinal) || !seen.Add(relative))
                throw new InvalidDataException("Invalid or duplicate shared native runtime path: " + relative);
            files.Add(new(relative, file.SizeBytes, PortablePackSyncService.ValidateSha256(file.Sha256, relative)));
            total += file.SizeBytes;
        }
        var steamLibrary = executable[..(executable.LastIndexOf('/') + 1)] + SteamNativeLibraryService.NativeFileName;
        if (!files.Any(file => file.Path == executable) || !files.Any(file => file.Path == steamLibrary) ||
            !files.Any(file => file.Path == SharedNativeRuntimeStore.FamilyPath))
            throw new InvalidDataException("Shared native runtime lacks its executable, Steam library or family manifest.");
        return shared with
        {
            Id = id, Version = version,
            Archive = new(name, PortablePackSyncService.ValidateSha256(archive.Sha256, name), archive.SizeBytes),
            Files = files
        };
    }

    internal static void VerifySharedRuntimeOverlay(PackRuntimeDescriptor descriptor, IEnumerable<string> paths,
        bool hasSharedRuntimeFeature)
    {
        var shared = descriptor.NativeRuntime?.SharedRuntime;
        if (hasSharedRuntimeFeature != (shared is not null))
            throw new InvalidDataException("The pack manifest and descriptor disagree about a shared native runtime.");
        if (shared is null) return;
        var overlay = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!overlay.Contains("native/presentation.json") || shared.Files.Any(file => overlay.Contains(file.Path)))
            throw new InvalidDataException("Shared native runtime overlaps its pack overlay or has no pack presentation.");
    }

    public static string ResolveClientJarPath(string packDirectory, PackRuntimeDescriptor descriptor)
    {
        if (descriptor.ClientJar.Length == 0)
        {
            throw new InvalidOperationException("This pack brings no client jar of its own.");
        }
        var packRoot = Path.GetFullPath(packDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(packRoot, descriptor.ClientJar));
        var parent = Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(parent, packRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("clientJar must point to a file directly in the pack root.");
        }

        return path;
    }

    /// <summary>
    /// The jar the pack brings, or an empty name where it brings none and the
    /// official client is to be fetched for it instead.
    /// </summary>
    private static string ValidateClientJarName(string? value)
    {
        var name = value?.Trim() ?? "";
        if (name.Length == 0) return "";
        if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("clientJar must be the name of one .jar file in the pack root.");
        }

        return name;
    }

    private static string ValidateIdentifier(string? value, string propertyName, bool allowSpaces)
    {
        var result = value?.Trim() ?? "";
        if (result.Length is < 1 or > 128 ||
            result is "." or ".." ||
            result.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            result.Contains('/') ||
            result.Contains('\\') ||
            result.Any(char.IsControl) ||
            (!allowSpaces && result.Any(char.IsWhiteSpace)))
        {
            throw new InvalidDataException($"{propertyName} is invalid.");
        }

        return result;
    }

    private sealed class PackManifestDto
    {
        public int SchemaVersion { get; set; }
        public string? MinecraftVersion { get; set; }
        public LoaderManifestDto? Loader { get; set; }
        public string? ClientJar { get; set; }
        public int? JavaMajorVersion { get; set; }
        public bool? PreserveWorldSettingsOnShare { get; set; }
        public NativeRuntimeManifestDto? NativeRuntime { get; set; }
    }

    private sealed class NativeRuntimeManifestDto
    {
        public string? Executable { get; set; }
        public string? Platform { get; set; }
        public string? Adapter { get; set; }
        public string? Version { get; set; }
        public NativeSharedRuntimeDescriptor? SharedRuntime { get; set; }
    }

    private sealed class LoaderManifestDto
    {
        public string? Type { get; set; }
        public string? Version { get; set; }
    }
}
