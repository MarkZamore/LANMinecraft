using System.IO;

namespace Minecraft;

/// <summary>Optional pack-authored categories of visible addon-tree roots.</summary>
public sealed class CatalogModGroup
{
    public string Title { get; set; } = "";
    public List<string> ModIds { get; set; } = [];
}

public sealed record CatalogModSection(string Title, IReadOnlyList<CatalogModNode> Mods);

internal static class CatalogModGrouping
{
    internal static void Validate(CatalogDescription content)
    {
        if (content.ModCountScope is not ("visible" or "all"))
            throw new InvalidDataException("Invalid mod count scope.");
        if (content.ModGroups is not { } groups) return;
        if (groups.Count > 100) throw new InvalidDataException("Too many mod groups.");
        var visible = content.VisibleModIds?.ToHashSet(StringComparer.Ordinal);
        var presented = content.Mods.Where(mod => visible is null || visible.Contains(mod.Id))
            .ToDictionary(mod => mod.Id, StringComparer.Ordinal);
        var roots = presented.Values.Where(mod => !mod.IsLibrary &&
            (mod.ParentId is null || !presented.ContainsKey(mod.ParentId))).Select(mod => mod.Id).ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var titles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (group is null || string.IsNullOrWhiteSpace(group.Title) || group.Title.Length > 120 ||
                !titles.Add(group.Title) || group.ModIds is null || group.ModIds.Count > 10000)
                throw new InvalidDataException("Invalid mod group.");
            foreach (var id in group.ModIds)
                if (string.IsNullOrWhiteSpace(id) || !roots.Contains(id) || !used.Add(id))
                    throw new InvalidDataException("Invalid, repeated or non-root grouped mod ID.");
        }
        if (!used.SetEquals(roots)) throw new InvalidDataException("Mod groups must cover every visible non-library root exactly once.");
    }
}
