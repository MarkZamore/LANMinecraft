using System.Collections.Frozen;
using System.IO;
using System.Text.Json;

namespace Minecraft;

internal enum LauncherStorageSchema { Unversioned, Current, Unsupported }

// Direct children owned by the launcher. The root sweeper uses these names
// alongside the paths used by application services, including native boot.
internal static class LauncherStorageLayout
{
    internal const int CurrentSchemaVersion = 1;
    internal const string SchemaFileName = ".storage-layout.json";
    internal const string Packs = "Packs";
    internal const string Launcher = "Launcher";
    internal const string Personal = "Personal";
    internal const string Worlds = "Worlds";
    internal const string LegacyLauncherRuntime = "Runtime";
    internal const string LauncherRuntime = "LauncherRuntime";
    internal const string LegacyPackRuntimes = "Runtimes";
    internal const string PackRuntimes = "PackRuntimes";
    internal const string JavaRuntimes = "JavaRuntimes";
    internal const string Shared = "Shared";
    internal const string Steam = "Steam";
    internal const string Instances = "Instances";
    internal const string PackConflicts = "PackConflicts";
    internal const string Backups = "Backups";
    internal const string BugReports = "BugReports";
    internal const string ProfileTransactions = "ProfileTransactions";
    internal const string WaypointConflicts = "WaypointConflicts";
    internal const string Settings = "settings.json";
    internal const string Playtime = "time.json";
    internal const string PackHashes = "pack-hashes.json";
    internal const string PackWeights = "pack-weights.json";
    internal const string WindowPlacement = "window-placement.json";
    internal const string MinecraftWindowPlacement = "minecraft-window-placement.json";
    internal const string SkinProfiles = "skin-profiles.properties";
    internal const string LegacySkinProfiles = "legacy-skin-profiles.properties";
    internal const string IdentityProfiles = "identity-profiles.properties";
    internal const string WaypointSync = "waypoint-sync.json";
    internal const string Log = "logs.log";
    internal const string Logs = "Logs";

    internal static FrozenSet<string> ServiceEntries { get; } = Names(Packs, Launcher, Personal, Worlds);

    // Keep import sources reserved even if the importer had to defer.
    internal static FrozenSet<string> LauncherEntries { get; } = Names(
        "Temp", "DotNetBundles", LegacyLauncherRuntime, LauncherRuntime,
        LegacyPackRuntimes, PackRuntimes, JavaRuntimes, Shared, Steam, "ManagedComponents",
        "Library", "IdentityAdapters", "NoticeOverlay", "WindowPresentation", "LaunchArguments", "NativeInterop",
        "Bootstrap", "Components", "ComponentCatalog", "LocalAI", "PowerShell", "bootstrap.log", "Changelog.md",
        ".shared-runtime.lock", LauncherMaintenance.CompletionFileName, SchemaFileName);

    internal static FrozenSet<string> PersonalEntries { get; } = Names(
        Instances, PackConflicts, Backups, WaypointConflicts, ProfileTransactions, BugReports, "Temp",
        "Home", "ClientSessions", "PackRenames", "Transfers", "Updates", Logs, Settings, Playtime, PackHashes,
        PackWeights, "memory-measurements.json", WindowPlacement, MinecraftWindowPlacement,
        SkinProfiles, LegacySkinProfiles, IdentityProfiles, "legacy-identity-profiles.properties",
        WaypointSync, "transfer-pacing.json", ".terrafirma-rename.lock", ".terrafirma-rename.pending",
        ".deployment-prep.lock", "UUID.json", "steam-identity.json");

    private static FrozenSet<string> Names(params string[] entries) =>
        entries.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal static LauncherStorageSchema ReadSchema(AppPaths paths)
    {
        var path = Path.Combine(paths.Launcher, SchemaFileName);
        try
        {
            _ = File.GetAttributes(path);
            if (!MaintenancePath.TryRead(paths, path, out var attributes) || attributes.HasFlag(FileAttributes.Directory))
                return LauncherStorageSchema.Unsupported;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 4096) return LauncherStorageSchema.Unsupported;
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("schemaVersion", out var version) &&
                version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number == CurrentSchemaVersion
                ? LauncherStorageSchema.Current : LauncherStorageSchema.Unsupported;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return LauncherStorageSchema.Unversioned;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return LauncherStorageSchema.Unsupported;
        }
    }

    internal static bool TryRecordCurrentSchema(AppPaths paths)
    {
        using var parents = new GuardedFileSystem.DirectoryLocks();
        if (!parents.HoldAncestors(paths.Launcher)) return false;
        var temporary = Path.Combine(paths.Launcher, $".{SchemaFileName}.{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = CurrentSchemaVersion }));
                stream.Flush(flushToDisk: true);
            }
            if (!GuardedFileSystem.TryPublishNewOrdinaryFile(temporary, SchemaFileName))
                return ReadSchema(paths) == LauncherStorageSchema.Current;
            created = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ReadSchema(paths) == LauncherStorageSchema.Current;
        }
        finally
        {
            if (created) MaintenancePath.TryDelete(paths, temporary, directory: false);
        }
    }
}
