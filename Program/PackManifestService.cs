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
            manifest = JsonSerializer.Deserialize<PackManifestDto>(File.ReadAllText(manifestPath), JsonOptions)
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
        if (manifest.JavaMajorVersion is { } javaMajor && JavaRuntimeCatalog.ForMajorVersion(javaMajor) is null)
        {
            throw new InvalidDataException("javaMajorVersion must name a Java feature release installed by the launcher.");
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
        if (descriptor.PreserveWorldSettingsOnShare && !SupportsWorldSharingPolicy(descriptor))
            throw new InvalidDataException("preserveWorldSettingsOnShare is unsupported for the declared Minecraft, loader and Java versions.");
        return descriptor;
    }

    internal static bool UsesJava8SteamWorldSharing(PackRuntimeDescriptor descriptor) =>
        descriptor.PreserveWorldSettingsOnShare && descriptor.Loader.Type == PackLoaderKind.Forge &&
        descriptor.MinecraftVersion is "1.12.2" or "1.16.5" && JavaRuntimeCatalog.RequiredFor(descriptor).MajorVersion == 8;

    private static bool SupportsWorldSharingPolicy(PackRuntimeDescriptor descriptor) =>
        UsesJava8SteamWorldSharing(descriptor) ||
        (descriptor.Loader.Type, descriptor.MinecraftVersion, JavaRuntimeCatalog.RequiredFor(descriptor).MajorVersion) is
            (PackLoaderKind.Forge, "1.18.2" or "1.19.2" or "1.20.1", 17) or (PackLoaderKind.NeoForge, "1.21.1", 21);

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
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new PackRuntimeDescriptor(NativeSchemaVersion, minecraftVersion,
            new PackLoaderDescriptor(PackLoaderKind.Native, null), "", hash)
        {
            NativeRuntime = new NativePackRuntimeDescriptor(path, native.Platform, native.Adapter, version)
        };
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
    }

    private sealed class LoaderManifestDto
    {
        public string? Type { get; set; }
        public string? Version { get; set; }
    }
}
