namespace Minecraft;

public static class PackDisplayName
{
    public static string CorrectCase(string name) => PackDisplayNames.CorrectFallbackCase(name);

    public static string For(string relativePath, string name)
    {
        // Presentation only: published paths, ownership and peer identities retain
        // their original values, including in old offline catalog snapshots.
        name = CorrectCase(name);
        return (relativePath.Equals(PackSyncSource.LegacyTerraFirmaRelativePath, StringComparison.OrdinalIgnoreCase) ||
         relativePath.Equals(PackSyncSource.TerraFirmaRelativePath, StringComparison.OrdinalIgnoreCase)) &&
        name.Equals(PackSyncSource.LegacyTerraFirmaRelativePath, StringComparison.OrdinalIgnoreCase)
            ? PackSyncSource.TerraFirmaRelativePath : name;
    }
}
