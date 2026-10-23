using System.IO.Compression;
using System.Text.Json;

namespace Minecraft;

/// <summary>Publisher-owned project links, with an offline fallback for older cached catalogues.</summary>
internal static class CatalogModPages
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<Dictionary<(string Pack, string Mod), string>> SeedPages = new(LoadSeedPages);

    internal static Uri? Parse(string? value) => value is { Length: > 0 and <= 2048 } &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) ? uri : null;

    internal static Uri? Resolve(string id, string? published, string? packId)
    {
        if (Parse(published) is { } page) return page;
        if (packId is not null)
            return SeedPages.Value.TryGetValue((packId, id), out var seed) ? Parse(seed) : null;
        var matches = SeedPages.Value.Where(pair => pair.Key.Mod == id).Select(pair => pair.Value).Distinct().Take(2).ToArray();
        return matches.Length == 1 ? Parse(matches[0]) : null;
    }

    private static Dictionary<(string Pack, string Mod), string> LoadSeedPages()
    {
        var pages = new Dictionary<(string Pack, string Mod), string>();
        var assembly = typeof(CatalogModPages).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n =>
                     n.StartsWith("Minecraft.Library.", StringComparison.Ordinal) && n.EndsWith(".zip", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var archive = new ZipArchive(stream);
            using var json = archive.GetEntry("library.json")!.Open();
            var content = JsonSerializer.Deserialize<CatalogDescription>(json, JsonOptions)!;
            foreach (var mod in content.Mods)
                if (mod.WebsiteAvailable && Parse(mod.WebsiteUrl) is { } uri) pages.TryAdd((content.Id, mod.Id), uri.AbsoluteUri);
        }
        return pages;
    }
}
