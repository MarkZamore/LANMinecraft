using System.IO;
using System.Text.Json;

namespace Minecraft;

/// <summary>The format is established by the played platform, never by a missing UUID tag.</summary>
internal static class WorldPlayerStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public const string Legacy = "legacy-name-v1";
    public const string Uuid = "uuid-v1";

    public static string ForMinecraft(string? minecraftVersion) => minecraftVersion == "1.6.4" ? Legacy : Uuid;

    public static string Resolve(string worldPath, AppPaths? paths = null, PackRuntimeDescriptor? runtime = null)
    {
        var metadata = new WorldMetadataService().Read(worldPath);
        if (metadata is null && File.Exists(Path.Combine(worldPath, WorldMetadataService.MetadataFileName)))
            throw new InvalidDataException("World platform metadata is damaged and cannot be replaced during player preparation.");
        if (metadata is not null && !PortableFormat.CanRead(metadata.SchemaVersion))
            throw new InvalidDataException(PortableFormat.DescribeUnreadable("Мир", metadata.SchemaVersion));
        var recorded = metadata?.PlayerStorage;
        if (!string.IsNullOrEmpty(recorded))
        {
            if (recorded is not (Legacy or Uuid) || string.IsNullOrEmpty(metadata!.MinecraftVersion) ||
                recorded != ForMinecraft(metadata.MinecraftVersion))
                throw new InvalidDataException("World player storage does not match its recorded Minecraft version.");
            if (runtime is not null && recorded != ForMinecraft(runtime.MinecraftVersion))
                throw new InvalidDataException("This world's player storage is incompatible with the selected runtime.");
            return recorded;
        }
        if (!string.IsNullOrEmpty(metadata?.MinecraftVersion))
        {
            var resolved = ForMinecraft(metadata.MinecraftVersion);
            if (runtime is not null && resolved != ForMinecraft(runtime.MinecraftVersion))
                throw new InvalidDataException("This world's player storage is incompatible with the selected runtime.");
            return resolved;
        }
        if (runtime is not null) return ForMinecraft(runtime.MinecraftVersion);
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
                if (PackManifestService.HasManifest(pack)) return ForMinecraft(PackManifestService.Load(pack).MinecraftVersion);
            }
        }
        return Uuid;
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
