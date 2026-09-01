using System.IO;

namespace Minecraft;

/// <summary>
/// Removes what earlier builds left in a player's portable folder and this one
/// no longer owns: the VPN peer table, the voice recordings, the identity file
/// the launcher used before Steam, caches of pinned components that are no
/// longer pinned, and adapter builds for runtimes that are gone.
///
/// Everything here is a file the launcher itself wrote. Nothing a player made
/// is touched - worlds, screenshots, skins, settings and their own backups are
/// none of this service's business - and a file that cannot be deleted (a
/// scanner is holding it, say) is left where it is rather than failing a
/// launch over it.
/// </summary>
public static class DeprecatedFileCleanupService
{
    /// <summary>
    /// Files directly under Personal that no build writes any more.
    ///
    /// UUID.json and steam-identity.json are deliberately absent: nothing reads
    /// them, but they are the only local record of the profile a machine used
    /// to play as. They cost a few hundred bytes and they are the first thing
    /// anyone would want if a player ever came back as the wrong character.
    /// </summary>
    private static readonly string[] PersonalFiles =
    [
        "network-peers.json",     // the VPN route cache
        "lan-relay-ports.json",   // the LAN relay's port store
        "peer-support-certificate.json"
    ];

    /// <summary>Directories under Personal that belonged to removed features.</summary>
    private static readonly string[] PersonalDirectories =
    [
        "Skin",           // flattened into skin-profiles.properties
        "WaypointSync",   // flattened into waypoint-sync.json
        "VoiceRecordings",
        "Audio",
        "Voice"
    ];

    /// <summary>Files under Launcher/ from the era before per-pack runtimes.</summary>
    private static readonly string[] LauncherFiles =
    [
        "Start-MinecraftFromLauncher.ps1",
        "Start-Minecraft.ps1",
        "Start-Minecraft.cmd",
        "standalone-classpath.txt",
        "standalone-jvmargs.txt",
        "standalone-clientargs.txt",
        "minecraft-window-icon.jar",
        "launcher-bootstrap-manifest.json",
        "runtime-bootstrap-manifest.json",
        "bootstrap-manifest.json"
    ];

    /// <summary>Directories under Launcher/ that the shared-runtime era created.</summary>
    private static readonly string[] LauncherDirectories =
    [
        "libraries",
        "assets",
        "natives",
        "versions",
        "java21-windows-x86-64"
    ];

    /// <summary>
    /// Component caches worth keeping. Anything else under ManagedComponents is
    /// a mod the launcher used to pin and does not any more, so the download
    /// sitting there will never be installed again.
    /// </summary>
    private static readonly string[] KeptComponentIds =
    [
        "java-runtime",
        "e4steam"
    ];

    public static void Run(AppPaths paths, Logger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var removed = 0;

        foreach (var name in PersonalFiles)
        {
            removed += TryDeleteFile(Path.Combine(paths.Personal, name), logger);
        }
        foreach (var name in PersonalDirectories)
        {
            removed += TryDeleteDirectory(Path.Combine(paths.Personal, name), logger);
        }
        foreach (var name in LauncherFiles)
        {
            removed += TryDeleteFile(Path.Combine(paths.Launcher, name), logger);
        }
        foreach (var name in LauncherDirectories)
        {
            removed += TryDeleteDirectory(Path.Combine(paths.Launcher, name), logger);
        }

        removed += CleanupUnpinnedComponents(paths, logger);
        removed += CleanupOrphanedAdapterBuilds(paths, logger);
        removed += CleanupSupersededPackJava(paths, logger);
        // And whatever a removal that did not finish left in the shared store.
        // It is cheap when there is nothing to do and it is the only thing that
        // ever takes files out of there.
        removed += SharedRuntimeStore.Sweep(paths, logger);

        if (removed > 0)
        {
            logger?.Info($"Removed {removed} file(s) left by earlier launcher versions.");
        }
    }

    /// <summary>
    /// A component cache is keyed by id and file id. Ids nobody pins any more
    /// are removed whole; a pinned component keeps only the file id it is
    /// pinned to, because the others are superseded downloads of the same mod.
    /// </summary>
    private static int CleanupUnpinnedComponents(AppPaths paths, Logger? logger)
    {
        var root = Path.Combine(paths.Launcher, "ManagedComponents");
        if (!Directory.Exists(root)) return 0;

        var removed = 0;
        foreach (var directory in EnumerateDirectories(root))
        {
            var id = Path.GetFileName(directory);
            if (!KeptComponentIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                removed += TryDeleteDirectory(directory, logger);
                continue;
            }

            var keptFileIds = ManagedComponentService.PinnedCacheFileIds(id);
            if (keptFileIds is null) continue;
            foreach (var version in EnumerateDirectories(directory))
            {
                if (!keptFileIds.Contains(Path.GetFileName(version), StringComparer.OrdinalIgnoreCase))
                {
                    removed += TryDeleteDirectory(version, logger);
                }
            }
        }
        return removed;
    }

    /// <summary>
    /// Java used to be installed inside each pack's own runtime folder. Moving
    /// it to one shared install did nothing about the copies already there, so
    /// a machine that played a build before the move still carries three
    /// hundred megabytes of JDK per build that nothing reads.
    /// </summary>
    /// <remarks>
    /// Only the launcher's own names are taken. Mojang's Java lives in the very
    /// same folder under names of its own - <c>java-runtime-delta</c>,
    /// <c>jre-legacy</c> and the like - and that Java is not spare: it runs the
    /// loader installer, every one of its files is listed in the runtime state,
    /// and removing one would fail the state check and cost a full re-prepare
    /// of the whole runtime. The two naming schemes have never overlapped,
    /// which is what makes this safe to do by name.
    /// </remarks>
    private static int CleanupSupersededPackJava(AppPaths paths, Logger? logger)
    {
        if (!Directory.Exists(paths.Runtimes)) return 0;

        var removed = 0;
        foreach (var build in EnumerateDirectories(paths.Runtimes))
        {
            var components = Path.Combine(build, "runtime", "windows-x64");
            if (!Directory.Exists(components)) continue;
            foreach (var directory in EnumerateDirectories(components))
            {
                if (JavaRuntimeCatalog.InstallDirectoryNames.Contains(
                        Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase))
                {
                    removed += TryDeleteDirectory(directory, logger);
                }
            }
        }
        return removed;
    }

    /// <summary>
    /// The identity adapter is built per runtime descriptor. A descriptor that
    /// no runtime uses any more cannot be reached, and each build is a jar plus
    /// its mapping.
    /// </summary>
    private static int CleanupOrphanedAdapterBuilds(AppPaths paths, Logger? logger)
    {
        var root = Path.Combine(paths.Launcher, "IdentityAdapters");
        if (!Directory.Exists(root) || !Directory.Exists(paths.Runtimes)) return 0;

        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in EnumerateFilesSafe(paths.Runtimes, ".portable-runtime.json"))
        {
            var hash = ReadDescriptorHash(state);
            if (hash is not null) live.Add(hash);
        }

        // Nothing readable means nothing provably orphaned; leave it all.
        if (live.Count == 0) return 0;

        var removed = 0;
        foreach (var directory in EnumerateDirectories(root))
        {
            if (!live.Contains(Path.GetFileName(directory)))
            {
                removed += TryDeleteDirectory(directory, logger);
            }
        }
        return removed;
    }

    private static string? ReadDescriptorHash(string runtimeStatePath)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(runtimeStatePath));
            return document.RootElement.TryGetProperty("descriptorHash", out var value)
                ? value.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string[] EnumerateDirectories(string root)
    {
        try
        {
            return Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string[] EnumerateFilesSafe(string root, string pattern)
    {
        try
        {
            return Directory.GetFiles(root, pattern, SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static int TryDeleteFile(string path, Logger? logger)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            logger?.Info($"Removed a file from an earlier launcher version: {path}.");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static int TryDeleteDirectory(string path, Logger? logger)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            Directory.Delete(path, recursive: true);
            logger?.Info($"Removed a directory from an earlier launcher version: {path}.");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
