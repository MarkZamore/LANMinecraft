namespace Minecraft;

public sealed record CatalogSource(string Id, string RelativePath, PackSyncSource Source)
{
    public string? LegacyId { get; init; }
    public string? NumberedId { get; init; }
    public string? InstallationPath { get; init; }
    public string? CompatibilityPath { get; init; }
    public string? LibraryGroup { get; init; }
    public string? RequirementsLevel { get; init; }
    public List<string> LegacyRelativePaths { get; init; } = [];
    public List<PackSyncSource> LegacySources { get; init; } = [];

    // Old metadata, resources and durable journals retain this key.
    internal string StorageId => LegacyId ?? Id;

    internal bool AcceptsId(string id) => Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
        LegacyId?.Equals(id, StringComparison.OrdinalIgnoreCase) == true;

    internal bool AcceptsIdentity(CatalogSource other) => AcceptsId(other.Id) &&
        (other.LegacyId is null || StorageId.Equals(other.StorageId, StringComparison.OrdinalIgnoreCase));

    public bool Equals(CatalogSource? other) => other is not null &&
        Id == other.Id && RelativePath == other.RelativePath && Source == other.Source &&
        LegacyId == other.LegacyId && NumberedId == other.NumberedId &&
        InstallationPath == other.InstallationPath &&
        CompatibilityPath == other.CompatibilityPath && LibraryGroup == other.LibraryGroup &&
        RequirementsLevel == other.RequirementsLevel &&
        (LegacyRelativePaths is null ? other.LegacyRelativePaths is null :
            other.LegacyRelativePaths is not null && LegacyRelativePaths.SequenceEqual(other.LegacyRelativePaths, StringComparer.Ordinal)) &&
        (LegacySources is null ? other.LegacySources is null :
            other.LegacySources is not null && LegacySources.SequenceEqual(other.LegacySources));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id, StringComparer.Ordinal);
        hash.Add(LegacyId, StringComparer.Ordinal);
        hash.Add(NumberedId, StringComparer.Ordinal);
        hash.Add(RelativePath, StringComparer.Ordinal);
        hash.Add(Source);
        hash.Add(InstallationPath, StringComparer.Ordinal);
        hash.Add(CompatibilityPath, StringComparer.Ordinal);
        hash.Add(LibraryGroup, StringComparer.Ordinal);
        hash.Add(RequirementsLevel, StringComparer.Ordinal);
        hash.Add(LegacyRelativePaths?.Count ?? -1);
        if (LegacyRelativePaths is not null)
            foreach (var path in LegacyRelativePaths) hash.Add(path, StringComparer.Ordinal);
        hash.Add(LegacySources?.Count ?? -1);
        if (LegacySources is not null)
            foreach (var source in LegacySources) hash.Add(source);
        return hash.ToHashCode();
    }
}
