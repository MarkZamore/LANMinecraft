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
            "mods/kubejs-neoforge-2101.7.2-build.348.jar",
            2_240_610,
            "6ddc6ddd16fabe09e2dd4303c6a598e3c272d6820b5c081f9c53e6b7aa6397fb"),
        new(
            "mods/ftb-library-neoforge-2101.1.31.jar",
            1_411_181,
            "3d44705187cf938842e06d484a93e0491b82b0689785e6da4f7cabe40bd88aeb"),
        new(
            "mods/XaerosMinimap.jar",
            2_120_271,
            "cece6dc6abbfbe4aeb9cd384b75cb687611dabc7ec0ab1dc65ee7da53759a32f")
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
            !string.Equals(descriptor.Loader.Version, "21.1.224", StringComparison.Ordinal))
        {
            throw Unsupported(
                $"expected Minecraft 1.21.1 with NeoForge 21.1.224, found " +
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
