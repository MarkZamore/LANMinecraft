using System.IO;

namespace Minecraft;

/// <summary>
/// Renames the built-in pack's folders from the launcher's previous pack name
/// to <see cref="PortablePackSyncService.DefaultPackRelativePath"/> and removes
/// what only the retired teleport layer needed. Options, maps, screenshots,
/// backups and worlds travel with the folders.
///
/// There is no "already migrated" marker: every step is gated on its legacy
/// artifact still being there, so a start that finds nothing is silent, and a
/// run stopped halfway - by a running game or an open world - finishes at the
/// next start.
/// </summary>
public static class LegacyPackMigrationService
{
    /// <summary>
    /// Every name the built-in pack has had, oldest first. A rename adds one
    /// here rather than replacing it: a player who skipped a release still has
    /// folders under the older name, and each start walks the whole list.
    /// </summary>
    public static readonly string[] LegacyPackRelativePaths = ["Infinity", "LL8"];

    /// <summary>The first of those, kept for the callers that name one.</summary>
    public const string LegacyPackRelativePath = "Infinity";

    /// <summary>Instance files the retired teleport layer owned.</summary>
    internal static readonly string[] TeleportInstanceFiles =
    [
        "config/ftbessentials.snbt",
        "defaultconfigs/ftbessentials-server.snbt",
        "kubejs/server_scripts/portable/portable_teleport_commands.js",
        "config/xaero/minimap/profiles/default.cfg"
    ];

    /// <summary>The per-world copy the teleport layer wrote on first launch.</summary>
    internal const string TeleportWorldConfigRelativePath = "serverconfig/ftbessentials-server.snbt";

    public static void Run(
        AppPaths paths,
        AppSettings settings,
        SettingsService settingsService,
        Logger logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(logger);

        var changes = 0;
        try
        {
            foreach (var legacy in LegacyPackRelativePaths)
            {
                if (string.Equals(
                        legacy,
                        PortablePackSyncService.DefaultPackRelativePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var instance = MigrateInstance(paths, legacy, logger, ref changes);
                if (instance == MoveResult.Failed)
                {
                    logger.Warn(
                        $"The {legacy} instance is in use; migration is postponed to the next start.");
                    return;
                }

                MigrateRuntime(paths, legacy, logger, ref changes);
                MigratePack(paths, legacy, logger, ref changes);
            }

            MigrateSelectedBuild(settings, settingsService, logger, ref changes);
            changes += RebindWorlds(paths, logger);
            changes += RemoveTemporaryDirectories(paths, logger);
            ForgetPackHashes(paths, logger);
        }
        catch (Exception ex)
        {
            // A rename nobody asked for must never be the reason the launcher
            // refuses to start: whatever is left keeps its old name until the
            // next attempt.
            logger.Warn($"Legacy pack migration stopped early: {ex.Message}");
        }

        if (changes > 0)
        {
            logger.Info(
                $"Legacy pack migration to {PortablePackSyncService.DefaultPackRelativePath} finished: " +
                $"{changes} item(s) renamed or removed.");
        }
    }

    private static MoveResult MigrateInstance(
        AppPaths paths,
        string legacyPackRelativePath,
        Logger logger,
        ref int changes)
    {
        var source = Path.Combine(paths.Instances, legacyPackRelativePath);
        var destination = paths.CombineUnderInstances(PortablePackSyncService.DefaultPackRelativePath);
        var result = TryMove(source, destination, "instance", logger);
        if (result != MoveResult.Moved) return result;

        changes++;
        PackInstanceService.ForgetInstanceFiles(
            destination,
            PortablePackSyncService.DefaultPackRelativePath,
            TeleportInstanceFiles,
            logger);
        return result;
    }

    private static void MigrateRuntime(
        AppPaths paths,
        string legacyPackRelativePath,
        Logger logger,
        ref int changes)
    {
        var source = Path.Combine(paths.Runtimes, legacyPackRelativePath);
        var destination = paths.CombineUnderRuntimes(PortablePackSyncService.DefaultPackRelativePath);
        // The runtime state holds no absolute paths and the pack's descriptor
        // hash has not changed, so the moved runtime is ready without a download.
        if (TryMove(source, destination, "runtime", logger) == MoveResult.Moved) changes++;
    }

    private static void MigratePack(
        AppPaths paths,
        string legacyPackRelativePath,
        Logger logger,
        ref int changes)
    {
        var source = Path.Combine(paths.Packs, legacyPackRelativePath);
        var target = PortablePackSyncService.DefaultPackRelativePath;
        var destination = paths.CombineUnderPacks(target);
        if (TryMove(source, destination, "pack", logger) != MoveResult.Moved) return;

        changes++;
        new PortablePackSyncService(paths, logger).EnsureDefaultSourceMarker(target);
    }

    private static void MigrateSelectedBuild(
        AppSettings settings,
        SettingsService settingsService,
        Logger logger,
        ref int changes)
    {
        if (!IsLegacyPack(settings.ClientRelativePath)) return;
        settings.ClientRelativePath = PortablePackSyncService.DefaultPackRelativePath;
        try
        {
            settingsService.Save(settings);
            changes++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warn($"Selected build could not be renamed in the settings: {ex.Message}");
        }
    }

    /// <summary>
    /// Points every world created in the legacy pack at the renamed one. A world
    /// that is open right now keeps its document and is picked up next start.
    /// </summary>
    private static int RebindWorlds(AppPaths paths, Logger logger)
    {
        if (!Directory.Exists(paths.Worlds)) return 0;
        var metadata = new WorldMetadataService();
        var target = PortablePackSyncService.DefaultPackRelativePath;
        var rebound = 0;
        foreach (var world in Directory.EnumerateDirectories(paths.Worlds))
        {
            try
            {
                var recorded = metadata.Read(world)?.BuildRelativePath;
                if (!IsLegacyPack(recorded)) continue;
                if (WorldAccessGuard.IsOpen(world))
                {
                    logger.Warn(
                        $"World {Path.GetFileName(world)} is open; its build is renamed at the next start.");
                    continue;
                }
                TryDeleteFile(
                    Path.Combine(
                        world,
                        TeleportWorldConfigRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                    logger);
                if (metadata.TryRebindBuild(world, recorded!, target, target)) rebound++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.Warn($"World {Path.GetFileName(world)} could not be rebound: {ex.Message}");
            }
        }
        return rebound;
    }

    private static int RemoveTemporaryDirectories(AppPaths paths, Logger logger)
    {
        var temp = Path.Combine(paths.Personal, "Temp");
        var removed = 0;
        foreach (var name in new[] { "Java", "RuntimeDownloads" })
        {
            removed += TryDeleteDirectory(Path.Combine(temp, name, LegacyPackRelativePath), logger);
        }
        return removed;
    }

    private static void ForgetPackHashes(AppPaths paths, Logger logger)
    {
        try
        {
            using var packHashes = new PackHashService(paths);
            packHashes.ForgetPack(LegacyPackRelativePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warn($"Pack hash cache still holds the {LegacyPackRelativePath} entry: {ex.Message}");
        }
    }

    private static MoveResult TryMove(string source, string destination, string what, Logger logger)
    {
        if (!Directory.Exists(source)) return MoveResult.NothingToMove;
        if (Directory.Exists(destination))
        {
            logger.Warn(
                $"Legacy {what} folder was left in place: {Path.GetFileName(destination)} already exists.");
            return MoveResult.DestinationExists;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(source, destination);
            logger.Info(
                $"Renamed the {LegacyPackRelativePath} {what} folder to {Path.GetFileName(destination)}.");
            return MoveResult.Moved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warn($"Legacy {what} folder could not be renamed: {ex.Message}");
            return MoveResult.Failed;
        }
    }

    /// <summary>
    /// True when this is a name the built-in pack used to have. Public because a
    /// world can arrive from a friend mid-session carrying an older name, long
    /// after the start-of-launch migration has run.
    /// </summary>
    public static bool IsLegacyPack(string? relativePath)
    {
        var name = relativePath?.Trim().Trim('\\', '/');
        if (string.IsNullOrEmpty(name)) return false;
        if (string.Equals(
                name,
                PortablePackSyncService.DefaultPackRelativePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return LegacyPackRelativePaths.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string path, Logger logger)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warn($"{Path.GetFileName(path)} could not be removed: {ex.Message}");
        }
    }

    private static int TryDeleteDirectory(string path, Logger logger)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            Directory.Delete(path, recursive: true);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warn($"{Path.GetFileName(path)} could not be removed: {ex.Message}");
            return 0;
        }
    }

    private enum MoveResult
    {
        NothingToMove,
        Moved,
        DestinationExists,
        Failed
    }
}
