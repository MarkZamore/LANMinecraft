using System.Diagnostics;
using System.IO;
using System.Text;

namespace Minecraft;

internal static class NativeClientLaunch
{
    internal static ProcessStartInfo CreateStartInfo(AppPaths paths, LocalIdentityContext identity,
        string packRelativePath, string gameDirectory, string executablePath, string runtimeVersion,
        string skinRegistryPath, string identityRegistryPath)
    {
        var start = new ProcessStartInfo(executablePath)
        {
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("LANMINECRAFT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.ArgumentList.Add("--game-dir");
        start.ArgumentList.Add(gameDirectory);
        start.Environment["LANMINECRAFT_NATIVE_CACHE"] = Path.Combine(paths.CombineUnderRuntimes(packRelativePath), "Native");
        start.Environment["LANMINECRAFT_SHARED_MINECRAFT"] = paths.SharedRuntime;
        start.Environment["LANMINECRAFT_STEAM_ID"] = identity.SteamId64.ToString();
        start.Environment["LANMINECRAFT_PLAYER_UUID"] = identity.PlayerUuid.ToString("D");
        start.Environment["LANMINECRAFT_PLAYER_NAME"] = identity.IdentityName;
        start.Environment["LANMINECRAFT_SKIN_REGISTRY"] = skinRegistryPath;
        start.Environment["LANMINECRAFT_IDENTITY_REGISTRY"] = identityRegistryPath;
        start.Environment["LANMINECRAFT_KNOWN_STEAM_IDENTITIES"] = string.Join(",",
            KnownSteamPlayers.All.Select(player => $"{player.SteamId64}={player.PlayerUuid:D}"));
        start.Environment["LANMINECRAFT_NATIVE_VARIANT"] = runtimeVersion;
        return start;
    }
}
