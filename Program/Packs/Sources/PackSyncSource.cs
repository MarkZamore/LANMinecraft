namespace Minecraft;

public sealed record PackSyncSource(string Owner, string Repo, string Tag)
{
    internal const string DefaultPackRelativePath = "All The Mods 10";
    internal const string LegacyTerraFirmaRelativePath = "TerraFirma Rebirth";
    internal const string TerraFirmaRelativePath = "TerraFirma Rebirth 2";

    internal static PackSyncSource TerraFirmaSource { get; } = new("MarkZamore", "TerraFirma-Rebirth-2", "pack-latest");
    internal static PackSyncSource DefaultPackSource { get; } = new("MarkZamore", "All-The-Mods-10", "pack-latest");

    internal string Key => Owner + "/" + Repo + "/" + Tag;

    internal static bool EqualsIgnoringCase(PackSyncSource left, PackSyncSource right) =>
        string.Equals(left.Owner, right.Owner, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Repo, right.Repo, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Tag, right.Tag, StringComparison.OrdinalIgnoreCase);

    // The default pack has no UI for pointing it to a different repository.
    // A marker with the same owner and a different repository predates its rename.
    internal static bool IsLegacyDefaultSource(PackSyncSource source) =>
        string.Equals(source.Owner, DefaultPackSource.Owner, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(source.Repo, DefaultPackSource.Repo, StringComparison.OrdinalIgnoreCase);

    internal static PackSyncSource NormalizeTerraFirmaSource(string relativePath, PackSyncSource source) =>
        (relativePath.Equals(LegacyTerraFirmaRelativePath, StringComparison.OrdinalIgnoreCase) ||
         relativePath.Equals(TerraFirmaRelativePath, StringComparison.OrdinalIgnoreCase)) &&
        source.Owner.Equals(TerraFirmaSource.Owner, StringComparison.OrdinalIgnoreCase) &&
        source.Repo.Equals("TerraFirma-Rebirth", StringComparison.OrdinalIgnoreCase) &&
        source.Tag.Equals(TerraFirmaSource.Tag, StringComparison.OrdinalIgnoreCase)
            ? TerraFirmaSource : source;
}
