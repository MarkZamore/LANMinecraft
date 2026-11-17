using System.IO;

namespace Minecraft;

internal static class MaintenancePath
{
    internal static bool IsProtectedAssetName(string name) =>
        name.Equals("resourcepacks", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("texturepacks", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("shaderpacks", StringComparison.OrdinalIgnoreCase);

    internal static bool TryRead(AppPaths paths, string path, out FileAttributes attributes)
    {
        attributes = default;
        try
        {
            var full = Path.GetFullPath(path);
            var service = Path.TrimEndingDirectorySeparator(paths.Service);
            if (!full.Equals(service, StringComparison.OrdinalIgnoreCase) &&
                !full.StartsWith(service + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;

            var ancestors = new Stack<string>();
            for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
                ancestors.Push(current);
            while (ancestors.TryPop(out var current))
            {
                var found = File.GetAttributes(current);
                if (found.HasFlag(FileAttributes.ReparsePoint)) return false;
                if (ancestors.Count == 0) attributes = found;
                else if (!found.HasFlag(FileAttributes.Directory)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool TryDelete(AppPaths paths, string path, bool directory, Func<bool>? stillAllowed = null)
    {
        if (!TryRead(paths, path, out var attributes) || attributes.HasFlag(FileAttributes.Directory) != directory)
            return false;
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(paths.Service, full);
        if (relative == "." || relative.Split(Path.DirectorySeparatorChar).Any(IsProtectedAssetName)) return false;
        return PortableRuntimeCache.TryDeleteOrdinaryEntry(full, directory, stillAllowed);
    }
}
