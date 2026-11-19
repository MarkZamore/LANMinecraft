using System.IO;
using System.IO.Compression;

namespace Minecraft;

/// <summary>A verified Forge-loader correction, independent of player identity and pack content.</summary>
internal static class ForgeHandshakeMappingService
{
    internal const string TargetClass = "net/minecraftforge/network/HandshakeHandler";

    internal static IdentityAdapterConfiguration? Build(PreparedRuntime runtime, AppPaths paths)
    {
        if (runtime.Descriptor.Loader.Type != PackLoaderKind.Forge) return null;
        var version = runtime.Descriptor.MinecraftVersion + "-" + runtime.Descriptor.Loader.Version;
        if (version.IndexOfAny(['/', '\\']) >= 0) throw new InvalidDataException("Invalid Forge loader version.");
        // Only the selected loader's official output. A content mod carrying a
        // similarly named class is never a source for this platform correction.
        var source = Path.Combine(runtime.LibrariesRoot, "net", "minecraftforge", "forge", version,
            $"forge-{version}-universal.jar");
        paths.EnsureUnderRoot(source);
        if (!File.Exists(source)) return null;
        using var archive = ZipFile.OpenRead(source);
        if (archive.GetEntry(TargetClass + ".class") is null) return null;
        return new IdentityAdapterConfiguration(source,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["forgeHandshakeSyncEnabled"] = "true" },
            [new IdentityAdapterTarget(source, TargetClass)]);
    }
}
