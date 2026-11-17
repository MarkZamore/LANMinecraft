using System.IO;
using System.Text.Json;

namespace Minecraft;

/// <summary>Removes private runtime copies that a verified library pack no longer references.</summary>
public static class StructureCleanupService
{
    private const string RuntimeStateFileName = ".portable-runtime.json";
    private const string LegacyJavaRoot = "runtime/windows-x64/";
    private static readonly HashSet<string> SharedNowRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "assets", "libraries", "versions", "runtime", "resources", "java21-windows-x86-64"
    };
    private static readonly char[] InvalidNameCharacters = Path.GetInvalidFileNameChars();

    public static int Run(AppPaths paths, Logger? logger = null, Action? checkpoint = null,
        Func<string, bool>? isManagedPack = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (isManagedPack is null) return 0;
        checkpoint ??= static () => { };
        var removed = 0;
        foreach (var runtime in LauncherStorageCleanup.ReadEntries(paths, paths.Runtimes, checkpoint))
        {
            if (!MaintenancePath.TryRead(paths, runtime, out var attributes) ||
                (attributes & FileAttributes.Directory) == 0) continue;
            var candidates = SharedNowRoots.Where(root => Directory.Exists(Path.Combine(runtime, root))).ToArray();
            if (candidates.Length == 0) continue;
            var state = Path.Combine(runtime, RuntimeStateFileName);
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
        var state = Path.Combine(runtime, RuntimeStateFileName);
        return ReadLayout(paths, state, Path.GetFileName(runtime), checkpoint) is { } layout &&
               !layout.LiveRoots.Contains(LegacyJavaRoot + javaName);
    }

    // A name alone never proves that an orphaned runtime belonged to a library
    // pack. The state is also needed because a current installation may still
    // refer to a private directory it prepared before the runtime was shared.
    private static Layout? ReadLayout(AppPaths paths, string statePath, string pack, Action checkpoint)
    {
        if (ReadStamp(paths, statePath) is not { Length: <= 32 * 1024 * 1024 } stamp) return null;
        try
        {
            using var input = File.OpenRead(statePath);
            using var document = JsonDocument.Parse(input);
            var json = document.RootElement;
            if (json.ValueKind != JsonValueKind.Object ||
                !json.TryGetProperty("schemaVersion", out var value) || value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt32(out var generation) || generation <= 0 || generation > PackRuntimeService.RuntimeCacheGeneration ||
                !json.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object) return null;

            var localPrefix = "Runtimes/" + pack + "/";
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasFiles = false;
            foreach (var file in files.EnumerateObject())
            {
                checkpoint();
                hasFiles = true;
                if (!AddPath(file.Name) || file.Value.ValueKind != JsonValueKind.Object) return null;
            }
            foreach (var property in new[] { "clientJarRelativePath", "javaPathRelativePath" })
            {
                if (!json.TryGetProperty(property, out var path)) continue;
                if (path.ValueKind != JsonValueKind.String) return null;
                var relative = path.GetString()!;
                if (relative.Length != 0 && !AddPath(relative)) return null;
            }
            if (generation == PackRuntimeService.RuntimeCacheGeneration && !hasFiles) return null;

            return ReadStamp(paths, statePath) == stamp ? new Layout(stamp, live) : null;

            bool AddPath(string relative)
            {
                var normalized = relative.Replace('\\', '/');
                if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(part =>
                    part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') ||
                    part.IndexOfAny(InvalidNameCharacters) >= 0)) return false;
                if (normalized.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase))
                    normalized = normalized[localPrefix.Length..];
                // Old state used paths relative to this private runtime.
                var slash = normalized.IndexOf('/');
                var root = slash < 0 ? normalized : normalized[..slash];
                if (SharedNowRoots.Contains(root)) live.Add(root);
                if (normalized.StartsWith(LegacyJavaRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var nextSlash = normalized.IndexOf('/', LegacyJavaRoot.Length);
                    live.Add(nextSlash < 0 ? normalized : normalized[..nextSlash]);
                }
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
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
