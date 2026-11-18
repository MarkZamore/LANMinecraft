using System.IO;

namespace Minecraft;

internal sealed class WorldRemovalService(AppPaths paths, Logger? logger = null)
{
    internal sealed record WorldRemovalPlan(IReadOnlyList<string> Targets, string? SelectedPath)
    {
        public int Worlds => Targets.Count;

        public bool SameTargets(WorldRemovalPlan other) =>
            SamePath(SelectedPath, other.SelectedPath) &&
            Targets.SequenceEqual(other.Targets, StringComparer.OrdinalIgnoreCase);
    }

    internal sealed record WorldRemovalOutcome(int Worlds, IReadOnlyList<string> Kept)
    {
        public bool Complete => Kept.Count == 0;
    }

    internal WorldRemovalPlan Plan(string? selectedPath = null)
    {
        if (selectedPath is not null) selectedPath = Path.GetFullPath(selectedPath);
        return new WorldRemovalPlan(Enumerate()
            .Where(path => selectedPath is null || SamePath(path, selectedPath))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray(), selectedPath);
    }

    internal IReadOnlyList<string> PlanForBuild(string build)
    {
        var root = WorldLocations.ForBuild(paths.Worlds, build);
        var canonical = WorldLocations.CanonicalBuildPath(build);
        var metadata = new WorldMetadataService();
        return Enumerate().Where(world =>
        {
            var parent = Path.GetDirectoryName(world);
            if (!SamePath(parent, root) && !SamePath(parent, paths.Worlds)) return false;
            var metadataPath = Path.Combine(world, WorldMetadataService.MetadataFileName);
            if (File.Exists(metadataPath) && !MaintenancePath.TryRead(paths, metadataPath, out _)) return false;
            var declared = metadata.Read(world)?.BuildRelativePath;
            return string.IsNullOrWhiteSpace(declared)
                ? SamePath(parent, root)
                : SamePath(WorldLocations.CanonicalBuildPath(declared), canonical);
        }).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal WorldRemovalOutcome Remove(WorldRemovalPlan plan)
    {
        using var lease = BuildRemovalService.AcquireRemovalLease(paths);
        if (!plan.SameTargets(Plan(plan.SelectedPath)))
            throw new IOException("The worlds to remove have changed.");
        EnsureClosed(plan.Targets);
        return RemoveUnderLease(plan.Targets);
    }

    internal void EnsureClosed(IEnumerable<string> targets)
    {
        foreach (var world in targets)
        {
            if (!IsWorld(world)) throw new IOException("The worlds to remove have changed.");
            var sessionLock = Path.Combine(world, "session.lock");
            if (File.Exists(sessionLock) && !MaintenancePath.TryRead(paths, sessionLock, out _))
                throw new IOException("The world session lock is redirected.");
            WorldAccessGuard.EnsureClosed(world);
        }
    }

    internal WorldRemovalOutcome RemoveUnderLease(IEnumerable<string> targets)
    {
        var removed = 0;
        var kept = new List<string>();
        foreach (var world in targets)
        {
            try
            {
                EnsureClosed([world]);
                if (!BuildRemovalService.RemoveOwnedTree(paths, world, logger))
                {
                    kept.Add(world);
                    continue;
                }
                removed++;
                // A pack's directory may still contain backups or unrelated files.
                var parent = Path.GetDirectoryName(world);
                if (!SamePath(parent, paths.Worlds) && parent is not null)
                    BuildRemovalService.TryRemoveIfEmpty(paths, parent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger?.Warn($"Could not delete world {world}: {ex.Message}");
                kept.Add(world);
            }
        }
        return new WorldRemovalOutcome(removed, kept);
    }

    private IEnumerable<string> Enumerate()
    {
        if (!Directory.Exists(paths.Worlds)) yield break;
        if (!OrdinaryDirectory(paths.Worlds))
            throw new IOException("The world directory is redirected or inaccessible.");

        foreach (var entry in Directory.EnumerateDirectories(paths.Worlds))
        {
            if (IsBackup(entry) || !OrdinaryDirectory(entry)) continue;
            if (IsWorld(entry))
            {
                yield return entry;
                continue;
            }
            foreach (var child in Directory.EnumerateDirectories(entry))
                if (IsWorld(child)) yield return child;
        }
    }

    private bool IsWorld(string path) =>
        !IsBackup(path) && OrdinaryDirectory(path) &&
        MaintenancePath.TryRead(paths, Path.Combine(path, "level.dat"), out var attributes) &&
        !attributes.HasFlag(FileAttributes.Directory);

    private bool OrdinaryDirectory(string path) =>
        MaintenancePath.TryRead(paths, path, out var attributes) && attributes.HasFlag(FileAttributes.Directory);

    private static bool SamePath(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsBackup(string path) =>
        Path.GetFileName(path).Contains(".backup-", StringComparison.OrdinalIgnoreCase);
}
