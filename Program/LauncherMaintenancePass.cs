using System.IO;

namespace Minecraft;

internal static class LauncherMaintenancePass
{
    internal static void Run(AppPaths paths, Logger logger, PortablePackSyncService sync, bool full,
        IReadOnlyList<string> pending, Action<CancellationToken> checkpoint, CancellationToken token)
    {
        var removed = 0;
        if (full) LauncherStorageCleanup.Run(paths, logger, Check, IsManagedPack);

        var packs = new HashSet<string>(pending, StringComparer.OrdinalIgnoreCase);
        if (full && MaintenancePath.TryRead(paths, paths.Packs, out var attributes) && attributes.HasFlag(FileAttributes.Directory))
        {
            foreach (var directory in Directory.EnumerateDirectories(paths.Packs))
            {
                Check();
                packs.Add(Path.GetFileName(directory));
            }
        }
        foreach (var pack in packs)
        {
            Check();
            if (!PackCatalogService.ValidFolder(pack)) continue;
            try
            {
                removed += ManagedPackCleanup.Run(paths, sync, pack, checkpoint, token, out var managed);
                if (managed && MaintenancePath.TryRead(paths, paths.CombineUnderInstances(pack), out var instance) &&
                    instance.HasFlag(FileAttributes.Directory))
                    LogCleanupService.RetainRecentSessionDiagnostics(paths.CombineUnderInstances(pack), Check);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                       System.Text.Json.JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                logger.Warn($"Storage maintenance deferred for {pack}: {ex.Message}");
            }
        }
        if (full)
        {
            Check();
            SharedRuntimeStore.SweepIdle(paths, logger, Check);
            JavaRuntimeStorage.SweepUnusedSuperseded(paths, logger, Check);
        }
        if (removed > 0) logger.Info($"Library pack maintenance removed {removed} unneeded item(s).");

        void Check() => checkpoint(token);
        bool IsManagedPack(string pack)
        {
            Check();
            return ManagedPackCleanup.IsManagedPack(paths, sync, pack, checkpoint, token);
        }
    }
}
