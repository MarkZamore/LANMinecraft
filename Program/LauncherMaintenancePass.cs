using System.IO;

namespace Minecraft;

internal static class LauncherMaintenancePass
{
    private const string SharedRuntimeStep = "shared-runtime";
    private const string JavaRuntimeStep = "java-runtime";

    internal static void Run(AppPaths paths, Logger logger, PortablePackSyncService sync,
        LauncherMaintenanceRun run)
    {
        var removed = 0;
        var token = run.Token;
        var priorityPacks = run.PriorityPacks.Concat(run.RequestedPacks)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var visited = priorityPacks.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var packs = new List<string>();
        var serviceSteps = run.FullPass ? Enum.GetValues<LauncherStorageCleanupStep>() : [];

        if (run.FullPass && MaintenancePath.TryRead(paths, paths.Packs, out var attributes) &&
            attributes.HasFlag(FileAttributes.Directory))
        {
            foreach (var directory in Directory.EnumerateDirectories(paths.Packs))
            {
                Check();
                var pack = Path.GetFileName(directory);
                if (PackCatalogService.ValidFolder(pack) && visited.Add(pack)) packs.Add(pack);
            }
        }
        var plannedSteps = priorityPacks.Concat(packs).Select(PackStep).Concat(serviceSteps.Select(ServiceStep));
        if (run.FullPass) plannedSteps = plannedSteps.Concat([SharedRuntimeStep, JavaRuntimeStep]);
        run.PlanSteps(plannedSteps);

        foreach (var pack in priorityPacks) VisitPack(pack);
        if (run.FullPass)
        {
            foreach (var step in serviceSteps) VisitServiceStep(step);
            foreach (var pack in packs) VisitPack(pack);
            WithoutGame(SharedRuntimeStep, () => SharedRuntimeStore.SweepIdle(paths, logger, CheckWithoutGame));
            WithoutGame(JavaRuntimeStep, () => JavaRuntimeStorage.SweepUnusedSuperseded(paths, logger, CheckWithoutGame));
        }
        if (removed > 0) logger.Info($"Library pack maintenance removed {removed} unneeded item(s).");

        void VisitPack(string pack)
        {
            Check();
            if (run.IsPackInUse(pack)) { run.DeferPack(pack); return; }

            var key = PackStep(pack);
            try
            {
                run.RunStep(key, () =>
                {
                    removed += ManagedPackCleanup.Run(paths, sync, pack, CheckPack, token, out var managed,
                        run.Cursor(key), position => run.SaveCursor(key, position));
                    if (managed && MaintenancePath.TryRead(paths, paths.CombineUnderInstances(pack), out var instance) &&
                        instance.HasFlag(FileAttributes.Directory))
                        LogCleanupService.RetainRecentSessionDiagnostics(paths.CombineUnderInstances(pack), () => CheckPack(token));
                });
                run.CompletePack(pack);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                       System.Text.Json.JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                run.DeferPack(pack);
                logger.Warn($"Storage maintenance deferred for {pack}: {ex.Message}");
            }

            void CheckPack(CancellationToken caller)
            {
                run.Checkpoint(caller);
                if (run.IsPackInUse(pack)) throw new OperationCanceledException(token);
            }
        }

        void VisitServiceStep(LauncherStorageCleanupStep step)
        {
            var key = ServiceStep(step);
            var safeDuringGame = LauncherStorageCleanup.CanRunDuringGame(step);
            if (safeDuringGame) run.RunStep(key, Sweep);
            else WithoutGame(key, Sweep);

            void Sweep()
            {
                var completedEntries = 0;
                LauncherStorageCleanup.RunStep(paths, logger, step, safeDuringGame ? Check : CheckWithoutGame, IsManagedPack,
                    run.Cursor(key), position =>
                    {
                        if (++completedEntries % 64 == 0) run.SaveCursor(key, position);
                    });
            }
        }

        void WithoutGame(string key, Action work)
        {
            if (run.GameIsRunning) run.DeferStep(key);
            else run.RunStep(key, work);
        }

        void Check() => run.Checkpoint(token);
        void CheckWithoutGame()
        {
            Check();
            if (run.GameIsRunning) throw new OperationCanceledException(token);
        }
        bool IsManagedPack(string pack)
        {
            Check();
            return ManagedPackCleanup.IsManagedPack(paths, sync, pack, run.Checkpoint, token);
        }
    }

    private static string PackStep(string pack) => "pack:" + pack;
    private static string ServiceStep(LauncherStorageCleanupStep step) => "service:" + step;
}
