using System.IO;

namespace Minecraft;

public static class PackMembership
{
    public static void Initialize(AppSettings settings, AppPaths paths, bool fresh = false)
    {
        settings.HiddenPackRelativePaths = (settings.HiddenPackRelativePaths ?? [])
            .Where(PackCatalogService.ValidFolder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (settings.AddedPackRelativePaths is null)
        {
            // An installed legacy name is still this catalog pack. Preserve
            // implicit membership before the startup rename reads settings.
            settings.AddedPackRelativePaths = PackCatalogService.SeedSources()
                .SelectMany(entry => new[] {entry.RelativePath}.Concat(entry.LegacyRelativePaths))
                .Where(path => MinecraftProcessService.HasPackData(paths.CombineUnderPacks(path)) ||
                               path.Equals(settings.ClientRelativePath, StringComparison.OrdinalIgnoreCase)).ToList();
            if (fresh && settings.AddedPackRelativePaths.Count == 0)
                settings.AddedPackRelativePaths.Add(PortablePackSyncService.DefaultPackRelativePath);
        }
        settings.AddedPackRelativePaths = settings.AddedPackRelativePaths
            .Where(PackCatalogService.ValidFolder).Where(path => !IsHidden(settings, path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool Contains(AppSettings settings, string path) =>
        !IsHidden(settings, path) && settings.AddedPackRelativePaths?.Contains(path, StringComparer.OrdinalIgnoreCase) == true;

    public static bool IsHidden(AppSettings settings, string path) =>
        settings.HiddenPackRelativePaths?.Contains(path, StringComparer.OrdinalIgnoreCase) == true;

    public static bool ShouldListInstalled(AppSettings settings, string path, bool isCatalogPack) =>
        !IsHidden(settings, path) && (!isCatalogPack || Contains(settings, path));

    public static bool Add(AppSettings settings, string path)
    {
        if (!PackCatalogService.ValidFolder(path)) throw new ArgumentException("Invalid pack folder.", nameof(path));
        settings.AddedPackRelativePaths ??= [];
        var unhidden = settings.HiddenPackRelativePaths?.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)) > 0;
        if (Contains(settings, path)) return unhidden;
        settings.AddedPackRelativePaths.Add(path);
        return true;
    }

    public static bool Remove(AppSettings settings, string path)
    {
        if (!PackCatalogService.ValidFolder(path)) return false;
        var removed = settings.AddedPackRelativePaths?.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)) > 0;
        settings.HiddenPackRelativePaths ??= [];
        if (IsHidden(settings, path)) return removed;
        settings.HiddenPackRelativePaths.Add(path);
        return true;
    }
}
