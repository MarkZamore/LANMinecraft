using System.IO;
using System.Text.Json;

namespace Minecraft;

/// <summary>The format is established by the played platform, never by a missing UUID tag.</summary>
internal static class WorldPlayerStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public const string Legacy = "legacy-name-v1";
    public const string Uuid = "uuid-v1";
    public const string NestedUuid = "nested-uuid-v1";
    public const string SingleplayerUuidTag = "singleplayer_uuid";

    public static string ForMinecraft(string? minecraftVersion)
    {
        if (minecraftVersion == "1.6.4") return Legacy;
        // Java Edition 26.1 introduced this layout, which Horizon 26.3 also writes.
        return Version.TryParse(minecraftVersion, out var version) && version.Major == 26 && version.Minor is >= 1 and <= 3
            ? NestedUuid : Uuid;
    }

    public static string WorldGenSettingsPath(string worldPath) =>
        Path.Combine(worldPath, "data", "minecraft", "world_gen_settings.dat");

    public static string Resolve(string worldPath, AppPaths? paths = null, PackRuntimeDescriptor? runtime = null)
    {
        var metadata = new WorldMetadataService().Read(worldPath);
        if (metadata is null && File.Exists(Path.Combine(worldPath, WorldMetadataService.MetadataFileName)))
            throw new InvalidDataException("World platform metadata is damaged and cannot be replaced during player preparation.");
        if (metadata is not null && !PortableFormat.CanRead(metadata.SchemaVersion))
            throw new InvalidDataException(PortableFormat.DescribeUnreadable("Мир", metadata.SchemaVersion));
        var recorded = metadata?.PlayerStorage;
        var version = metadata?.MinecraftVersion;
        if (!string.IsNullOrEmpty(version))
        {
            var storage = ForMinecraft(version);
            if (!string.IsNullOrEmpty(recorded) && recorded != storage && !CanCorrectRecordedStorage(recorded, storage))
                throw new InvalidDataException("World player storage does not match its recorded Minecraft version.");
            if (runtime is not null && storage != ForMinecraft(runtime.MinecraftVersion))
                throw new InvalidDataException("This world's player storage is incompatible with the selected runtime.");
            return ResolvePlatform(worldPath, storage);
        }
        if (!string.IsNullOrEmpty(recorded))
            throw new InvalidDataException("World player storage has no recorded Minecraft version.");
        if (runtime is not null) return ResolvePlatform(worldPath, ForMinecraft(runtime.MinecraftVersion));
        if (paths is not null)
        {
            var build = metadata?.BuildRelativePath;
            if (string.IsNullOrEmpty(build))
            {
                var relative = Path.GetRelativePath(paths.Worlds, worldPath);
                var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Length == 2 && parts.All(part => part is not ("." or ".."))) build = parts[0];
            }
            if (!string.IsNullOrEmpty(build))
            {
                var pack = paths.CombineUnderPacks(build);
                if (PackManifestService.HasManifest(pack))
                    return ResolvePlatform(worldPath, ForMinecraft(PackManifestService.Load(pack).MinecraftVersion));
            }
        }
        return File.Exists(WorldGenSettingsPath(worldPath)) ? NestedUuid : Uuid;
    }

    // Older launchers recorded every non-legacy world as uuid-v1, including 26.2 and 26.3.
    public static bool CanCorrectRecordedStorage(string recorded, string storage) => recorded == Uuid && storage == NestedUuid;

    private static string ResolvePlatform(string worldPath, string storage)
    {
        if (storage != NestedUuid && File.Exists(WorldGenSettingsPath(worldPath)))
            throw new InvalidDataException("This world's player storage is incompatible with the selected runtime.");
        return storage;
    }

    public static void RecordRuntime(string worldPath, string buildPath, PackRuntimeDescriptor runtime,
        ProfileFileTransaction transaction)
    {
        _ = Resolve(worldPath, runtime: runtime);
        var metadataService = new WorldMetadataService();
        var metadata = metadataService.Read(worldPath) ?? new WorldMetadata
        {
            WorldId = Guid.NewGuid().ToString("D"), BuildRelativePath = buildPath
        };
        metadata.MinecraftVersion = runtime.MinecraftVersion;
        metadata.PlayerStorage = ForMinecraft(runtime.MinecraftVersion);
        metadata.SchemaVersion = PortableFormat.SchemaVersion;
        var path = Path.Combine(worldPath, WorldMetadataService.MetadataFileName);
        transaction.Track(path);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(metadata, JsonOptions));
    }
}
