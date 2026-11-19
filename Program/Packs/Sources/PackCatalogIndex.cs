using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

/// <summary>Reads the published index and the embedded fallback without loading catalog services.</summary>
internal static class PackCatalogIndex
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static List<CatalogSource> ReadEmbeddedSources()
    {
        using var stream = typeof(PackCatalogIndex).Assembly.GetManifestResourceStream("Minecraft.Library.catalog.json")
            ?? throw new InvalidDataException("Missing embedded library index.");
        using var reader = new StreamReader(stream);
        return ParseIndex(reader.ReadToEnd(), useEmbeddedAliases: false);
    }

    internal static List<CatalogSource> ParseIndex(string json, bool useEmbeddedAliases = true)
    {
        var index = JsonSerializer.Deserialize<CatalogIndex>(json, JsonOptions);
        if (index is null || index.SchemaVersion != 1 || index.Packs is null || index.Packs.Count > 100)
            throw new InvalidDataException("Unsupported library index.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in index.Packs)
        {
            if (entry is null || !ValidFolder(entry.RelativePath) || string.IsNullOrWhiteSpace(entry.Id) ||
                entry.Id.Length > 100 || !names.Add(entry.RelativePath) || entry.Source is null || !ValidSource(entry.Source) || !sources.Add(entry.Source.Key))
                throw new InvalidDataException("Invalid/duplicate library source.");
            AddIndexIds(ids, entry);
            if (entry.LibraryGroup is { } group && (group.Length > 80 || string.IsNullOrWhiteSpace(group) ||
                group != group.Trim() || group.Any(char.IsControl)))
                throw new InvalidDataException("Invalid library group name.");
            if (entry.RequirementsLevel is not null && PackRequirementTag.FromCatalogCode(entry.RequirementsLevel) is null)
                throw new InvalidDataException("Invalid library requirements level.");
            if ((entry.InstallationPath is not null && !ValidFolder(entry.InstallationPath)) ||
                (entry.CompatibilityPath is not null && !ValidFolder(entry.CompatibilityPath)))
                throw new InvalidDataException("Invalid library installation path.");
            if (entry.LegacyRelativePaths is null || entry.LegacySources is null || entry.LegacyRelativePaths.Count > 16 ||
                entry.LegacySources.Count > 16 || entry.LegacyRelativePaths.Any(path => !ValidFolder(path) || path.Equals(entry.RelativePath, StringComparison.OrdinalIgnoreCase)) ||
                entry.LegacySources.Any(source => source is null || !ValidSource(source)))
                throw new InvalidDataException("Invalid library rename aliases.");
        }
        // Normalize verified historical folder names without changing index
        // membership. The embedded index is parsed without recursively reading
        // itself to obtain its own aliases.
        IReadOnlyList<CatalogSource> aliases = useEmbeddedAliases ? ReadEmbeddedSources() : [];
        // The shared index keeps the original wire ID for older clients.
        // From here on, the numbered ID is canonical and the old key is retained
        // for existing bundles, caches and journals.
        var canonical = index.Packs.Select(entry => CanonicalSource(
            entry.NumberedId is { } numbered
                ? entry with { Id = numbered, LegacyId = entry.Id, NumberedId = null }
                : entry, aliases)).ToList();
        if (canonical.Select(entry => entry.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != canonical.Count ||
            canonical.Select(entry => entry.Source.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != canonical.Count)
            throw new InvalidDataException("Invalid/duplicate library rename targets.");
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ids.Clear();
        foreach (var entry in canonical)
        {
            AddIndexIds(ids, entry);
            if (entry.LegacyRelativePaths.Count > 16 ||
                (entry.CompatibilityPath is not null &&
                 !entry.LegacyRelativePaths.Contains(entry.CompatibilityPath, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("Invalid library installation aliases.");
            foreach (var path in entry.LegacyRelativePaths.Prepend(entry.RelativePath))
            {
                if (owners.TryGetValue(path, out var owner) && owner != entry.Id)
                    throw new InvalidDataException("A library path belongs to multiple packs.");
                owners[path] = entry.Id;
            }
        }
        return canonical;
    }

    private static void AddIndexIds(HashSet<string> ids, CatalogSource entry)
    {
        if (!ids.Add(entry.Id) || (entry.LegacyId is { } legacy &&
            (string.IsNullOrWhiteSpace(legacy) || legacy.Length > 100 || legacy != legacy.Trim() || !ids.Add(legacy))))
            throw new InvalidDataException("Invalid/duplicate library ID or legacy ID.");
        if (entry.NumberedId is { } numbered && (entry.LegacyId is not null ||
            numbered.Length > 100 || !Regex.IsMatch(numbered, @"^minecraft-[1-9][0-9]*\z") || !ids.Add(numbered)))
            throw new InvalidDataException("Invalid/duplicate numbered library ID.");
    }

    internal static bool ValidFolder(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 120 &&
        value is not ("." or "..") && value == value.Trim() && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !value.EndsWith('.');
    internal static bool ValidSource(PackSyncSource source) => !string.IsNullOrWhiteSpace(source.Owner) && !string.IsNullOrWhiteSpace(source.Repo) &&
        !string.IsNullOrWhiteSpace(source.Tag) && Regex.IsMatch(source.Owner, @"^[A-Za-z0-9_-]{1,100}$") &&
        Regex.IsMatch(source.Repo, @"^[A-Za-z0-9_.-]{1,100}$") && Regex.IsMatch(source.Tag, @"^[A-Za-z0-9_.-]{1,100}$");

    internal static CatalogSource CanonicalSource(CatalogSource entry, IReadOnlyList<CatalogSource> embeddedAliases)
    {
        var seed = embeddedAliases.FirstOrDefault(candidate => candidate.AcceptsId(entry.Id) ||
            entry.LegacyId is { } legacy && candidate.AcceptsId(legacy));
        if (seed is not null)
        {
            if (!seed.AcceptsIdentity(entry))
                throw new InvalidDataException("Catalog ID conflicts with an embedded identity.");
            entry = entry with { Id = seed.Id, LegacyId = seed.LegacyId };
        }
        if (entry.StorageId == "terrafirma-rebirth" &&
            entry.RelativePath.Equals(PackSyncSource.LegacyTerraFirmaRelativePath, StringComparison.OrdinalIgnoreCase))
            entry = entry with
            {
                RelativePath = PackSyncSource.TerraFirmaRelativePath,
                Source = PackSyncSource.NormalizeTerraFirmaSource(entry.RelativePath, entry.Source)
            };
        if (entry.InstallationPath is { } target)
        {
            var old = entry.RelativePath;
            return entry with
            {
                RelativePath = target,
                InstallationPath = null,
                CompatibilityPath = old.Equals(target, StringComparison.OrdinalIgnoreCase) ? null : old,
                LegacyRelativePaths = entry.LegacyRelativePaths.Append(old)
                    .Where(path => !path.Equals(target, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
        }
        if (seed is null || !seed.LegacyRelativePaths.Contains(entry.RelativePath, StringComparer.OrdinalIgnoreCase))
            return entry;
        var paths = entry.LegacyRelativePaths.Concat(seed.LegacyRelativePaths)
            .Where(path => !path.Equals(seed.RelativePath, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sources = entry.LegacySources.Concat(seed.LegacySources)
            .DistinctBy(source => source.Key, StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count > 16 || sources.Count > 16)
            throw new InvalidDataException("Too many merged library rename aliases.");
        // A folder alias does not authorize replacing a newly published source.
        // Redirect only a source explicitly identified by the embedded rename;
        // every other source still goes through the normal verification gate.
        var knownSource = PackSyncSource.EqualsIgnoringCase(entry.Source, seed.Source) ||
            seed.LegacySources.Any(source => PackSyncSource.EqualsIgnoringCase(source, entry.Source));
        return entry with
        {
            RelativePath = seed.RelativePath,
            CompatibilityPath = seed.CompatibilityPath,
            Source = knownSource ? seed.Source : entry.Source,
            LegacyRelativePaths = paths,
            LegacySources = sources
        };
    }
}

internal sealed class CatalogIndex
{
    public int SchemaVersion { get; set; }
    public List<CatalogSource> Packs { get; set; } = null!;
    public List<CatalogSource>? EffectiveSources { get; set; }
    public List<string>? KnownRelativePaths { get; set; }
    public List<CatalogSource>? SourceHistory { get; set; }
}
