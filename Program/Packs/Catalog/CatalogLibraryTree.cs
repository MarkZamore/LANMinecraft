namespace Minecraft;

internal static class CatalogLibraryTree
{
    public static IEnumerable<CatalogModNode> Build(IEnumerable<CatalogModNode> libraries)
    {
        var roots = new List<CatalogModNode>();
        foreach (var project in libraries.GroupBy(library => library.WebsiteUri))
        {
            var modules = project.ToList();
            if (project.Key is not { } page || modules.Count == 1)
            {
                roots.AddRange(modules);
                continue;
            }

            var label = page.Segments.Reverse()
                .Select(segment => Uri.UnescapeDataString(segment.Trim('/')))
                .FirstOrDefault(segment => segment.Length != 0 && char.IsLetter(segment[0])) ?? page.IdnHost;
            var key = ProjectKey(label);
            // A project name takes precedence over an alias such as a loader's dummy mod ID.
            var root = UniqueMatch(modules, node => ProjectKey(node.Name) == key)
                ?? UniqueMatch(modules, node => ProjectKey(node.Id) == key)
                ?? new CatalogModNode(new CatalogMod
                {
                    Id = page.AbsoluteUri,
                    Name = label,
                    IsLibrary = true,
                    IsLoadedMod = false,
                    WebsiteUrl = page.AbsoluteUri
                });
            root.Children.AddRange(modules.Where(node => node != root));
            roots.Add(root);
        }
        return roots.OrderBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private static CatalogModNode? UniqueMatch(IEnumerable<CatalogModNode> modules, Func<CatalogModNode, bool> match)
    {
        var matches = modules.Where(match).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string ProjectKey(string name) => string.Concat(name.Where(char.IsLetterOrDigit)).ToUpperInvariant();
}
