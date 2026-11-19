using System.IO;

namespace Minecraft;

/// <summary>Reads the offline library bundles in the prepared desktop package.</summary>
internal static class OfflineCatalogSeed
{
    internal static Stream? Open(string storageId)
    {
        var context = BootstrapLaunchContext.Current;
        var name = ArchivePath(storageId);
        return context.ContainsFile("assets", name)
            ? File.Open(context.Resolve("assets", name), FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;
    }

    internal static bool Contains(string storageId) =>
        BootstrapLaunchContext.Current.ContainsFile("assets", ArchivePath(storageId));

    private static string ArchivePath(string storageId)
    {
        if (storageId is not { Length: > 0 and <= 80 } ||
            storageId.Any(value => !(char.IsAsciiLetterOrDigit(value) || value is '-' or '_')))
            throw new InvalidDataException("Invalid library preview identifier.");
        return $"LibrarySeed/{storageId}.zip";
    }
}
