using System.IO;

namespace Minecraft;

/// <summary>Removes private runtime copies that a verified library pack no longer references.</summary>
public static class StructureCleanupService
{
    private const string LegacyJavaRoot = "runtime/windows-x64/";
    private static readonly HashSet<string> SharedNowRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "assets", "libraries", "versions", "runtime", "resources", "java21-windows-x86-64"
    };

    public static int Run(AppPaths paths, Logger? logger = null, Action? checkpoint = null,
        Func<string, bool>? isManagedPack = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (isManagedPack is null) return 0;
        checkpoint ??= static () => { };
        var removed = 0;
        foreach (var runtimeRoot in paths.PackRuntimeRoots)
        foreach (var runtime in LauncherStorageCleanup.ReadEntries(paths, runtimeRoot, checkpoint))
        {
            if (!MaintenancePath.TryRead(paths, runtime, out var attributes) ||
                (attributes & FileAttributes.Directory) == 0) continue;
            var candidates = SharedNowRoots.Where(root => Directory.Exists(Path.Combine(runtime, root))).ToArray();
            if (candidates.Length == 0) continue;
            var state = Path.Combine(runtime, PackRuntimeReceipt.FileName);
            var name = Path.GetFileName(runtime);
            if (ReadLayout(paths, state, name, checkpoint) is not { } layout ||
                candidates.All(layout.LiveRoots.Contains) || !isManagedPack(name)) continue;

            var before = removed;
            foreach (var root in candidates)
            {
                if (layout.LiveRoots.Contains(root)) continue;
                checkpoint();
                if (ReadStamp(paths, state) != layout.Stamp) break;
                removed += LauncherStorageCleanup.DeleteTree(paths, Path.Combine(runtime, root), checkpoint);
            }
            if (removed > before) logger?.Info($"Removed an obsolete private runtime copy for {name}.");
        }
        return removed;
    }

    internal static bool IsUnusedLegacyJava(AppPaths paths, string runtime, string javaName, Action checkpoint)
    {
        var state = Path.Combine(runtime, PackRuntimeReceipt.FileName);
        return ReadLayout(paths, state, Path.GetFileName(runtime), checkpoint) is { } layout &&
               !layout.LiveRoots.Contains(LegacyJavaRoot + javaName);
    }

    // A name alone never proves that an orphaned runtime belonged to a library
    // pack. The state is also needed because a current installation may still
    // refer to a private directory it prepared before the runtime was shared.
    private static Layout? ReadLayout(AppPaths paths, string statePath, string pack, Action checkpoint)
    {
        if (ReadStamp(paths, statePath) is not { Length: <= PackRuntimeReceipt.MaximumBytes } stamp) return null;
        using var receipt = PackRuntimeReceipt.TryRead(statePath);
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (receipt is null || !receipt.TryReadPrivateReferences(pack, checkpoint, Record) || ReadStamp(paths, statePath) != stamp)
            return null;
        return new Layout(stamp, live);

        void Record(string relative)
        {
            var slash = relative.IndexOf('/');
            var root = slash < 0 ? relative : relative[..slash];
            if (SharedNowRoots.Contains(root)) live.Add(root);
            if (relative.StartsWith(LegacyJavaRoot, StringComparison.OrdinalIgnoreCase))
            {
                var nextSlash = relative.IndexOf('/', LegacyJavaRoot.Length);
                live.Add(nextSlash < 0 ? relative : relative[..nextSlash]);
            }
        }
    }

    private static StateStamp? ReadStamp(AppPaths paths, string path)
    {
        if (!MaintenancePath.TryRead(paths, path, out var attributes) || (attributes & FileAttributes.Directory) != 0)
            return null;
        try
        {
            var info = new FileInfo(path);
            return new StateStamp(info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private readonly record struct StateStamp(long Length, DateTime Modified, DateTime Created);
    private sealed record Layout(StateStamp Stamp, HashSet<string> LiveRoots);
}
