using System.IO;

namespace Minecraft;

/// <summary>The identity and platform actually given to a JVM, independent of later UI/account changes.</summary>
internal sealed record WorldLaunchSession(DateTimeOffset StartedUtc, string GameDirectory, WorldMetadataContext Metadata)
{
    internal static WorldLaunchSession Capture(
        string packDirectory, string buildRelativePath, string gameDirectory, string packHash,
        PackRuntimeDescriptor runtime, LocalIdentityContext identity, DateTimeOffset startedUtc)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(identity);
        return new WorldLaunchSession(startedUtc, gameDirectory, new WorldMetadataContext
        {
            BuildName = PackDisplayNames.ResolveDirectory(packDirectory, Path.GetFileName(packDirectory)),
            BuildRelativePath = buildRelativePath,
            PackHash = packHash,
            MinecraftVersion = runtime.MinecraftVersion,
            OwnerIdentityId = identity.MinecraftUuid,
            OwnerIdentityName = identity.IdentityName,
            OwnerSteamId64 = identity.SteamId64.ToString()
        });
    }

    /// <summary>An adopted process has no proven launch identity and cannot attribute worlds.</summary>
    internal static IReadOnlyList<string> StampClosedWorlds(AppPaths paths, WorldLaunchSession? session, Logger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (session is null) return [];
        new SavesFolderService(logger).Adopt(
            WorldLocations.ForBuild(paths.Worlds, session.Metadata.BuildRelativePath), session.GameDirectory);
        return new WorldMetadataService().StampPlayedWorlds(paths.Worlds, session.Metadata, session.StartedUtc);
    }
}
