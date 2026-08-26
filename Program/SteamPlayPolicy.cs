namespace Minecraft;

/// <summary>
/// Decides which packs get the Steam transport mod. Play over Steam needs
/// e4steam inside the game, and a pack is served when its author has published
/// a build for that pack's loader and Minecraft - which is a question with a
/// table behind it, not one loader and one range.
/// </summary>
/// <remarks>
/// It used to be one loader and one range: NeoForge, [1.20.2, 26.3). That was
/// not the mod's shape but the shape of the single artifact the launcher
/// pinned, whose own metadata says exactly that and says nothing about the
/// Forge and Fabric builds published beside it. Every Forge pack - which on
/// 1.19.2 and 1.20.1 is most of the ones worth playing - was refused Steam play
/// because of a range read off the wrong file. <see cref="SteamTransportCatalog"/>
/// now holds the table, and this asks it.
/// </remarks>
public static class SteamPlayPolicy
{
    public static bool IsSupported(PackRuntimeDescriptor? descriptor) =>
        SteamTransportCatalog.Find(descriptor) is not null;

    /// <summary>Whether any published build declares this Minecraft at all.</summary>
    internal static bool IsSupportedMinecraftVersion(string? minecraftVersion) =>
        SteamTransportCatalog.CoversMinecraftVersion(minecraftVersion);
}
