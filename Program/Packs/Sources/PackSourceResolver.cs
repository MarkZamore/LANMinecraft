using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

/// <summary>The catalog state needed to select or recognize an installation source.</summary>
public interface IPackSourceCatalog
{
    IReadOnlyList<CatalogSource> Sources { get; }
    bool IsStaged { get; }
    bool IsLaunchReady(string relativePath);
    bool IsWithdrawn(string relativePath);
    bool IsRetiredInstalled(string relativePath);
    PackSyncSource? SourceFor(string relativePath);
    PackSyncSource ResolveHistoricalSourceMarker(string relativePath, PackSyncSource marker);
}

internal static partial class PackSourceResolver
{
    internal const string MarkerFileName = "portable-pack-source.json";
    private const int MarkerSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Lazy<IReadOnlyList<CatalogSource>> EmbeddedSources = new(() => PackCatalogIndex.ReadEmbeddedSources());

    internal static PackSyncSource? Resolve(AppPaths paths, Logger logger, string relativePath, IPackSourceCatalog? catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (catalog?.IsWithdrawn(relativePath) == true) return null;
        if (catalog?.IsStaged == true && catalog.IsLaunchReady(relativePath))
            return catalog.SourceFor(relativePath);

        var markerPath = Path.Combine(paths.CombineUnderPacks(relativePath), MarkerFileName);
        if (!File.Exists(markerPath))
            return catalog is null ? KnownSourceFor(relativePath) : catalog.SourceFor(relativePath);

        try
        {
            var marker = ReadInstalledMarker(markerPath);
            if (marker is null)
            {
                logger.Warn($"Pack sync source marker is invalid; the pack is treated as custom: {markerPath}");
                return null;
            }
            if (relativePath.Equals(PackSyncSource.DefaultPackRelativePath, StringComparison.OrdinalIgnoreCase) &&
                PackSyncSource.IsLegacyDefaultSource(marker))
            {
                logger.Info($"Pack sync source marker still names {marker.Repo}; the built-in {PackSyncSource.DefaultPackRelativePath} " +
                    "source is used and the marker is rewritten.");
                return catalog is null ? PackSyncSource.DefaultPackSource : catalog.SourceFor(relativePath);
            }

            var renamed = EmbeddedSources.Value.FirstOrDefault(entry =>
                entry.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase) &&
                entry.LegacySources.Any(source => PackSyncSource.EqualsIgnoringCase(source, marker)));
            var normalized = PackSyncSource.NormalizeTerraFirmaSource(relativePath, renamed?.Source ?? marker);
            return catalog?.ResolveHistoricalSourceMarker(relativePath, normalized) ?? normalized;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.Warn($"Pack sync source marker could not be read; the pack is treated as custom: {ex.Message}");
            return null;
        }
    }

    internal static PackSyncSource? KnownSourceFor(string? relativePath)
    {
        var name = relativePath?.Trim().Trim('\\', '/');
        if (string.IsNullOrEmpty(name)) return null;
        if (name.Equals(PackSyncSource.LegacyTerraFirmaRelativePath, StringComparison.OrdinalIgnoreCase))
            return PackSyncSource.TerraFirmaSource;
        return EmbeddedSources.Value.FirstOrDefault(pack =>
            pack.RelativePath.Equals(name, StringComparison.OrdinalIgnoreCase))?.Source;
    }

    internal static bool MatchesPublishedSource(string relativePath, PackSyncSource marker, IPackSourceCatalog? catalog)
    {
        var entry = (catalog?.Sources ?? EmbeddedSources.Value).FirstOrDefault(candidate =>
            candidate.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase) ||
            candidate.LegacyRelativePaths.Contains(relativePath, StringComparer.OrdinalIgnoreCase));
        return entry is not null && (PackSyncSource.EqualsIgnoringCase(entry.Source, marker) ||
            entry.LegacySources.Any(legacy => PackSyncSource.EqualsIgnoringCase(legacy, marker)) ||
            catalog is not null && PackSyncSource.EqualsIgnoringCase(entry.Source, catalog.ResolveHistoricalSourceMarker(relativePath, marker)));
    }

    // The older, non-staged library refresh accepts unversioned markers when
    // preloading custom pack metadata. Launch and cleanup still require v1.
    internal static PackSyncSource? ReadCatalogMarker(string markerPath)
    {
        if (!File.Exists(markerPath)) return null;
        var source = JsonSerializer.Deserialize<PackSyncSource>(File.ReadAllText(markerPath), JsonOptions);
        return source is not null && PackCatalogIndex.ValidSource(source) ? source : null;
    }

    internal static void WriteMarker(string packDirectory, PackSyncSource source)
    {
        var markerPath = Path.Combine(packDirectory, MarkerFileName);
        if (TryReadInstalledMarker(markerPath) == source) return;
        AtomicFile.WriteAllText(markerPath, JsonSerializer.Serialize(new SourceMarker
        {
            SchemaVersion = MarkerSchemaVersion,
            Owner = source.Owner,
            Repo = source.Repo,
            Tag = source.Tag
        }, JsonOptions));
    }

    internal static PackSyncSource? TryReadInstalledMarker(string markerPath)
    {
        try
        {
            return File.Exists(markerPath) ? ReadInstalledMarker(markerPath) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static PackSyncSource? ReadInstalledMarker(string markerPath)
    {
        var marker = JsonSerializer.Deserialize<SourceMarker>(File.ReadAllText(markerPath), JsonOptions);
        return marker is not null && marker.SchemaVersion == MarkerSchemaVersion &&
            IsValidIdentifier(marker.Owner) && IsValidIdentifier(marker.Repo) && IsValidIdentifier(marker.Tag)
                ? new PackSyncSource(marker.Owner!, marker.Repo!, marker.Tag!) : null;
    }

    private static bool IsValidIdentifier(string? value) => value is { Length: >= 1 and <= 100 } &&
        value is not ("." or "..") && IdentifierRegex().IsMatch(value);

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    private sealed class SourceMarker
    {
        public int SchemaVersion { get; set; }
        public string? Owner { get; set; }
        public string? Repo { get; set; }
        public string? Tag { get; set; }
    }
}
