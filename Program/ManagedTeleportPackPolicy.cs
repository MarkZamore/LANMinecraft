using System.IO;
using System.Security.Cryptography;

namespace Minecraft;

/// <summary>
/// Keeps launcher-managed gameplay additions opt-in and pinned to the Infinity
/// profile they were built and verified for. Other packs remain untouched.
/// </summary>
internal static class ManagedTeleportPackPolicy
{
    internal const string SupportedPackRelativePath = "Infinity";

    private static readonly ManagedTeleportPackArtifact[] RequiredArtifacts =
    [
        new(
            "mods/kubejs-neoforge-2101.7.2-build.368.jar",
            2_281_720,
            "28867299e7a9f02cfd74e34745fdbbb073fe4887fddbc98fd6c1ed2e87b01482"),
        new(
            "mods/ftb-library-neoforge-2101.1.33.jar",
            1_425_984,
            "6e8f7b57f243caf5cbb2c80387924df7b555b98d0fc6e1e575d4b5d74f5ff2e2"),
        new(
            "mods/XaerosMinimap.jar",
            2_185_409,
            "b722bcf794288f0ed51165cd1f057fc4505e20abbc723b2e17e900426e443603"),
        new(
            "mods/ftb-chunks-neoforge-2101.1.20.jar",
            655_911,
            "3bcd6f0032cec7310dc90b02b5a00e4cf1dd7a507f758bb353de864cb5e9241e"),
        new(
            "mods/SolarFluxReborn-1.21.1-21.1.8.jar",
            303_598,
            "f84ac97d52f188ea220633fbea8af6e275bb48968202aedd683974b0449f0fba")
    ];

    public static bool IsEnabledFor(string? packRelativePath)
    {
        var normalized = (packRelativePath ?? string.Empty)
            .Replace('\\', '/')
            .Trim('/');
        return string.Equals(
            normalized,
            SupportedPackRelativePath,
            StringComparison.OrdinalIgnoreCase);
    }

    public static void Validate(
        PackRuntimeDescriptor descriptor,
        PackInstanceContext instance)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(instance);
        if (!string.Equals(descriptor.MinecraftVersion, "1.21.1", StringComparison.Ordinal) ||
            descriptor.Loader.Type != PackLoaderKind.NeoForge ||
            !string.Equals(descriptor.Loader.Version, "21.1.235", StringComparison.Ordinal))
        {
            throw Unsupported(
                $"expected Minecraft 1.21.1 with NeoForge 21.1.235, found " +
                $"{descriptor.MinecraftVersion} {descriptor.Loader.Type} {descriptor.Loader.Version}");
        }

        var gameRoot = Path.GetFullPath(instance.GameDirectory);
        foreach (var artifact in RequiredArtifacts)
        {
            ValidateArtifact(gameRoot, artifact);
        }

        var ftbConfiguration = ResolveUnderRoot(
            gameRoot,
            "config/ftbessentials.snbt");
        EnsureNoReparsePointInExistingPath(
            ftbConfiguration,
            gameRoot);
        if (!File.Exists(ftbConfiguration))
        {
            throw Unsupported("the Infinity FTB Essentials configuration is missing");
        }
    }

    internal static void ValidateArtifact(
        string root,
        ManagedTeleportPackArtifact artifact)
    {
        var path = ResolveUnderRoot(root, artifact.RelativePath);
        EnsureNoReparsePointInExistingPath(path, root);
        var file = new FileInfo(path);
        if (!file.Exists ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            file.Length != artifact.SizeBytes)
        {
            throw Unsupported(
                $"required Infinity component is missing or incompatible: {artifact.RelativePath}");
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(hash, artifact.Sha256, StringComparison.Ordinal))
        {
            throw Unsupported(
                $"required Infinity component failed validation: {artifact.RelativePath}");
        }
    }

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(
            normalizedRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Managed Infinity component path escapes its root: {relativePath}");
        }
        return path;
    }

    private static void EnsureNoReparsePointInExistingPath(
        string path,
        string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var current = Path.GetFullPath(path);
        while (current is not null &&
               (string.Equals(
                    current.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    normalizedRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                current.StartsWith(
                    normalizedRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw Unsupported(
                    $"required Infinity path contains a reparse point: {path}");
            }
            if (string.Equals(
                    current.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    normalizedRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = Path.GetDirectoryName(current);
        }
    }

    private static NotSupportedException Unsupported(string reason) => new(
        $"The selected Infinity pack is not compatible with the managed teleport feature: {reason}. " +
        "Restore the original Infinity pack or update Minecraft.exe.");
}

internal sealed record ManagedTeleportPackArtifact(
    string RelativePath,
    long SizeBytes,
    string Sha256);
