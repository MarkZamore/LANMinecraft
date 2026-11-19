using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.Version;

namespace Minecraft;

internal static class MinecraftLaunchArguments
{
    internal static Process BuildProcess(MinecraftLauncher launcher, IVersion profile, MLaunchOption options)
    {
        // CmlLib 4.0.6 deliberately drops minecraftArguments from parent
        // profiles. A loader without its own game arguments then loses even
        // username/session and the asset path. Restore only that empty case;
        // an explicit loader argument set keeps its normal precedence.
        if (!profile.ConcatInheritedGameArguments().Any() &&
            profile.GetInheritedProperty(version => version.GetProperty("minecraftArguments")) is { Length: > 0 } legacy)
        {
            if (legacy.Contains("${game_assets}", StringComparison.Ordinal) &&
                !options.ArgumentDictionary.ContainsKey("game_assets"))
            {
                var assets = options.Path?.Assets ?? launcher.MinecraftPath.Assets;
                var id = profile.GetInheritedProperty(version => version.AssetIndex?.Id) ?? "legacy";
                if (id.Length == 0 || id is "." or ".." ||
                    id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
                    throw new InvalidDataException("Invalid game asset index ID.");
                using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(assets, "indexes", id + ".json")));
                var virtualAssets = index.RootElement.TryGetProperty("virtual", out var flag) && flag.ValueKind == JsonValueKind.True;
                options.ArgumentDictionary = new Dictionary<string, string>(options.ArgumentDictionary)
                {
                    ["game_assets"] = virtualAssets ? Path.Combine(assets, "virtual", id) : assets
                };
            }
            options.ExtraGameArguments = new[] { MArgument.FromCommandLine(legacy) }
                .Concat(options.ExtraGameArguments).ToArray();
        }
        return launcher.BuildProcess(profile, options);
    }
}
