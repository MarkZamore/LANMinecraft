using System.IO;

namespace Minecraft;

/// <summary>Opens the verified fallback archive shared by the game window icon readers.</summary>
internal static class GameWindowIconAssets
{
    internal static Stream Open()
    {
        var path = BootstrapLaunchContext.Current.Resolve("assets", "MinecraftWindowIconAssets.jar");
        return File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
}
