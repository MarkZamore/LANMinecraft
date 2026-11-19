using System.IO;
using System.Text.Json;

namespace Minecraft;

public static class DeprecatedFileCleanupService
{
    private static readonly string[] PersonalEntries =
    [
        "network-peers.json", "lan-relay-ports.json", "peer-support-certificate.json", "Skin", "WaypointSync",
        "VoiceRecordings", "Audio", "Voice", "BootstrapDownloads"
    ];

    private static readonly string[] LauncherEntries =
    [
        "Start-MinecraftFromLauncher.ps1", "Start-Minecraft.ps1", "Start-Minecraft.cmd", "standalone-classpath.txt",
        "standalone-jvmargs.txt", "standalone-clientargs.txt", "minecraft-window-icon.jar",
        "launcher-bootstrap-manifest.json", "runtime-bootstrap-manifest.json", "bootstrap-manifest.json",
        "libraries", "assets", "natives", "versions", "java21-windows-x86-64"
    ];

    private static readonly string[] RetiredComponents =
        ["tiptothescales", "ftb-essentials", "the-bumblezone", "oritech"];

    public static void Run(AppPaths paths, Logger? logger = null, Action? checkpoint = null,
        Func<string, bool>? isManagedPack = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        checkpoint ??= static () => { };
        var removed = RemoveEntries(paths, paths.Personal, PersonalEntries, checkpoint);
        removed += RemoveEntries(paths, paths.Launcher, LauncherEntries, checkpoint);
        removed += CleanupUnpinnedComponents(paths, checkpoint);
        removed += CleanupRetiredJavaComponents(paths, checkpoint);
        if (isManagedPack is not null)
            removed += CleanupSupersededPackJava(paths, checkpoint, isManagedPack);
        if (removed > 0) logger?.Info($"Removed {removed} entries left by earlier launcher versions.");
    }

    private static int RemoveEntries(AppPaths paths, string directory, IEnumerable<string> names, Action checkpoint)
    {
        var removed = 0;
        foreach (var name in names)
            removed += LauncherStorageCleanup.DeleteTree(paths, Path.Combine(directory, name), checkpoint);
        return removed;
    }

    private static int CleanupUnpinnedComponents(AppPaths paths, Action checkpoint)
    {
        var root = Path.Combine(paths.Launcher, "ManagedComponents");
        var removed = RemoveEntries(paths, root, RetiredComponents, checkpoint);
        var component = ManagedComponentService.E4steamComponentId;
        var pinned = ManagedComponentService.PinnedCacheFileIds(component);
        if (pinned is not { Count: > 0 }) return removed;

        foreach (var directory in LauncherStorageCleanup.ReadEntries(paths, Path.Combine(root, component), checkpoint))
        {
            var name = Path.GetFileName(directory);
            if (name.All(char.IsAsciiDigit) && !pinned.Contains(name, StringComparer.OrdinalIgnoreCase))
                removed += LauncherStorageCleanup.DeleteTree(paths, directory, checkpoint);
        }
        // Java archives can still belong to a prepared pack with a historical pin.
        return removed;
    }

    private static int CleanupSupersededPackJava(AppPaths paths, Action checkpoint, Func<string, bool> isManagedPack)
    {
        var removed = 0;
        foreach (var root in paths.PackRuntimeRoots)
        foreach (var build in LauncherStorageCleanup.ReadEntries(paths, root, checkpoint))
        {
            if (!isManagedPack(Path.GetFileName(build))) continue;
            var components = Path.Combine(build, "runtime", "windows-x64");
            foreach (var directory in LauncherStorageCleanup.ReadEntries(paths, components, checkpoint))
            {
                if (JavaRuntimeCatalog.LegacyInstallDirectoryNames.Contains(
                        Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase) &&
                    MaintenancePath.TryRead(paths, directory, out var attributes) &&
                    (attributes & FileAttributes.Directory) != 0 && !JavaRuntimeStorage.IsInUse(directory) &&
                    StructureCleanupService.IsUnusedLegacyJava(paths, build, Path.GetFileName(directory), checkpoint))
                {
                    removed += LauncherStorageCleanup.DeleteTree(paths, directory, checkpoint);
                }
            }
        }
        return removed;
    }

    private static int CleanupRetiredJavaComponents(AppPaths paths, Action checkpoint)
    {
        // A session from an earlier launcher may still hold its extracted Java agents.
        if (new ClientPresenceService(paths).ReadComponentReferences() is null) return 0;
        var removed = 0;
        foreach (var relative in new[] { "IdentityAdapters", "NativeInterop", "NoticeOverlay/Agent" })
        {
            checkpoint();
            removed += LauncherStorageCleanup.DeleteTree(paths, Path.Combine(paths.Launcher, relative), checkpoint);
        }
        return removed;
    }

    internal static int CleanupRuntimeComponents(AppPaths paths, Action checkpoint)
    {
        var root = Path.Combine(paths.Launcher, "Components", "objects");
        if (!MaintenancePath.TryRead(paths, root, out var rootAttributes) ||
            !rootAttributes.HasFlag(FileAttributes.Directory)) return 0;
        var references = ReadComponentReferences(paths, checkpoint);
        if (references is null) return 0;
        var removed = 0;
        foreach (var directory in LauncherStorageCleanup.ReadEntries(paths, root, checkpoint))
        {
            var hash = Path.GetFileName(directory);
            if (!RuntimeComponentSnapshot.IsHash(hash) || !MaintenancePath.TryRead(paths, directory, out var attributes) ||
                !attributes.HasFlag(FileAttributes.Directory)) continue;
            using var gate = RuntimeComponentService.TryAcquireObjectLock(paths, hash);
            if (gate is null) continue;
            removed += CleanupComponentParts(paths, directory, checkpoint);
            if (references.Contains(hash) || !TryReadComponentOwner(paths, directory, hash, out var payload)) continue;
            var current = ReadComponentReferences(paths, checkpoint);
            if (current is null) return removed;
            if (current.Contains(hash)) continue;
            try
            {
                // A preparation keeps its payload read handle until the process is recorded.
                using (var exclusive = new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                checkpoint();
                if (!MaintenancePath.TryDelete(paths, payload, directory: false)) continue;
                removed++;
                var marker = Path.Combine(directory, "artifact.json");
                if (MaintenancePath.TryDelete(paths, marker, directory: false)) removed++;
                if (MaintenancePath.TryDelete(paths, directory, directory: true)) removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    private static IReadOnlySet<string>? ReadComponentReferences(AppPaths paths, Action checkpoint)
    {
        var installed = RuntimeComponentService.ReadProtectedHashes(paths, checkpoint);
        var sessions = new ClientPresenceService(paths).ReadComponentReferences();
        if (installed is null || sessions is null) return null;
        var references = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
        references.UnionWith(sessions);
        return references;
    }

    private static bool TryReadComponentOwner(AppPaths paths, string directory, string hash, out string payload)
    {
        payload = "";
        try
        {
            var marker = Path.Combine(directory, "artifact.json");
            if (!MaintenancePath.TryRead(paths, marker, out var markerAttributes) ||
                markerAttributes.HasFlag(FileAttributes.Directory) || new FileInfo(marker).Length > 64 * 1024) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(marker));
            var owner = document.RootElement;
            if (owner.ValueKind != JsonValueKind.Object ||
                !owner.TryGetProperty("sha256", out var digest) || digest.GetString() is not { } ownerHash ||
                !string.Equals(ownerHash, hash, StringComparison.OrdinalIgnoreCase) ||
                !owner.TryGetProperty("fileName", out var file) || file.GetString() is not { Length: > 0 } name ||
                name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !owner.TryGetProperty("sizeBytes", out var size) || !size.TryGetInt64(out var length) || length < 0)
                return false;
            payload = Path.Combine(directory, name);
            var entries = Directory.GetFileSystemEntries(directory);
            return entries.Length == 2 && entries.Contains(marker, StringComparer.OrdinalIgnoreCase) &&
                entries.Contains(payload, StringComparer.OrdinalIgnoreCase) &&
                MaintenancePath.TryRead(paths, payload, out var attributes) && !attributes.HasFlag(FileAttributes.Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                   InvalidOperationException or ArgumentException) { return false; }
    }

    private static int CleanupComponentParts(AppPaths paths, string directory, Action checkpoint)
    {
        var removed = 0;
        var cutoff = DateTime.UtcNow.AddHours(-1);
        foreach (var path in LauncherStorageCleanup.ReadEntries(paths, directory, checkpoint))
        {
            var name = Path.GetFileName(path);
            var suffix = name.Length - 38;
            if (suffix <= 0 || name[suffix] != '.' || !name.EndsWith(".part", StringComparison.Ordinal) ||
                !Guid.TryParseExact(name.AsSpan(suffix + 1, 32), "N", out _) ||
                !MaintenancePath.TryRead(paths, path, out var attributes) || attributes.HasFlag(FileAttributes.Directory) ||
                File.GetLastWriteTimeUtc(path) > cutoff) continue;
            checkpoint();
            if (MaintenancePath.TryDelete(paths, path, directory: false)) removed++;
        }
        return removed;
    }

}
