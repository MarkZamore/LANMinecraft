using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Minecraft;

/// <summary>One global final title policy, using the existing trimmed instance-folder name.</summary>
internal sealed record WindowTitleSettings(bool Enabled, string Text, string ContentSha256)
{
    internal const string PolicyVersion = "pack-name-v1";
    internal static WindowTitleSettings Disabled { get; } = new(false, string.Empty, string.Empty);

    internal static WindowTitleSettings Load(string packDirectory, PreparedRuntime runtime, string gameDirectory)
    {
        // Author configs and templates are deliberately not read or changed.
        // One final producer handles every profile, including future catalog entries.
        var packName = Path.GetFileName(Path.GetFullPath(gameDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Trim();
        if (string.IsNullOrWhiteSpace(packName)) packName = "Minecraft";
        var fingerprint = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(PolicyVersion + "\0" + packName))).ToLowerInvariant();
        return new WindowTitleSettings(true, packName, fingerprint);
    }
}
