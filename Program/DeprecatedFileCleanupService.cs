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
        removed += CleanupOrphanedAdapterBuilds(paths, checkpoint);
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
        foreach (var build in LauncherStorageCleanup.ReadEntries(paths, paths.Runtimes, checkpoint))
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

    private static int CleanupOrphanedAdapterBuilds(AppPaths paths, Action checkpoint)
    {
        var root = Path.Combine(paths.Launcher, "IdentityAdapters");
        if (!MaintenancePath.TryRead(paths, root, out var rootAttributes) ||
            (rootAttributes & FileAttributes.Directory) == 0 ||
            !MaintenancePath.TryRead(paths, paths.Runtimes, out var runtimeAttributes) ||
            (runtimeAttributes & FileAttributes.Directory) == 0) return 0;

        // If any runtime cannot be read, an adapter could still belong to it.
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var runtime in Directory.EnumerateFileSystemEntries(paths.Runtimes))
            {
                checkpoint();
                if (!MaintenancePath.TryRead(paths, runtime, out var attributes)) return 0;
                if ((attributes & FileAttributes.Directory) == 0) continue;
                var statePath = Path.Combine(runtime, ".portable-runtime.json");
                try { _ = File.GetAttributes(statePath); }
                catch (FileNotFoundException) { continue; }
                if (!MaintenancePath.TryRead(paths, statePath, out var stateAttributes) ||
                    (stateAttributes & FileAttributes.Directory) != 0) return 0;

                using var input = File.OpenRead(statePath);
                using var state = JsonDocument.Parse(input);
                if (state.RootElement.ValueKind != JsonValueKind.Object ||
                    !state.RootElement.TryGetProperty("descriptorHash", out var descriptor) ||
                    descriptor.ValueKind != JsonValueKind.String ||
                    descriptor.GetString() is not { } hash || !IsHash(hash)) return 0;
                live.Add(hash);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return 0;
        }
        if (live.Count == 0) return 0;

        var removed = 0;
        foreach (var directory in LauncherStorageCleanup.ReadEntries(paths, root, checkpoint))
        {
            var name = Path.GetFileName(directory);
            if (IsHash(name) && !live.Contains(name) &&
                MaintenancePath.TryRead(paths, directory, out var attributes) &&
                (attributes & FileAttributes.Directory) != 0)
                removed += LauncherStorageCleanup.DeleteTree(paths, directory, checkpoint);
        }
        return removed;
    }

    private static bool IsHash(string name) => name.Length == 64 && name.All(char.IsAsciiHexDigit);
}
