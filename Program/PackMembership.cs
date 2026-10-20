using System.IO;

namespace Minecraft;

public static class PackMembership
{
    public static void Initialize(AppSettings settings, AppPaths paths, bool fresh = false)
    {
        if (settings.AddedPackRelativePaths is null)
        {
            settings.AddedPackRelativePaths = PortablePackSyncService.KnownPacks
                .Where(p => MinecraftProcessService.HasPackData(paths.CombineUnderPacks(p.RelativePath)) ||
                            p.RelativePath.Equals(settings.ClientRelativePath, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.RelativePath).ToList();
            if (fresh && settings.AddedPackRelativePaths.Count == 0)
                settings.AddedPackRelativePaths.Add(PortablePackSyncService.DefaultPackRelativePath);
        }
        settings.AddedPackRelativePaths = settings.AddedPackRelativePaths
            .Where(PackCatalogService.ValidFolder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool Contains(AppSettings settings, string path) =>
        settings.AddedPackRelativePaths?.Contains(path, StringComparer.OrdinalIgnoreCase) == true;

    public static bool Add(AppSettings settings, string path)
    {
        if (!PackCatalogService.ValidFolder(path)) throw new ArgumentException("Invalid pack folder.", nameof(path));
        settings.AddedPackRelativePaths ??= [];
        if (Contains(settings, path)) return false;
        settings.AddedPackRelativePaths.Add(path);
        return true;
    }

    public static bool Remove(AppSettings settings, string path) =>
        settings.AddedPackRelativePaths?.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)) > 0;
}
