using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using CmlLib.Core.Version;

namespace Minecraft;

/// <summary>The same runtime icons and verified name, available before its first window.</summary>
internal static class GameWindowPresentation
{
    private static readonly string[] IconNames = ["icon_16x16.png", "icon_32x32.png", "icon_48x48.png", "icon_128x128.png", "icon_256x256.png"];

    internal static IReadOnlyList<string> Prepare(AppPaths paths, PreparedRuntime runtime, IVersion profile,
        string packDirectory, string gameDirectory)
    {
        var title = WindowTitleSettings.Load(packDirectory, runtime, gameDirectory).Text;
        var icons = ReadRuntimeIcons(paths, runtime, profile);
        if (icons.Count == 0)
        {
            using var fallback = Assembly.GetExecutingAssembly().GetManifestResourceStream("Minecraft.WindowIconAssets.jar")
                ?? throw new InvalidOperationException("The game's window icons are missing.");
            ReadArchiveIcons(fallback, icons);
        }
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        foreach (var pair in icons.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var entry = archive.CreateEntry(pair.Key);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var output = entry.Open();
            output.Write(pair.Value);
        }
        var bytes = memory.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var iconPath = Path.Combine(paths.Launcher, "WindowPresentation", hash, "icons.jar");
        paths.EnsureUnderRoot(iconPath);
        if (!File.Exists(iconPath) || !File.ReadAllBytes(iconPath).AsSpan().SequenceEqual(bytes))
            AtomicFile.WriteAllBytes(iconPath, bytes);
        return [$"-Dlanminecraft.window.title={title}", $"-Dlanminecraft.window.icons={iconPath}"];
    }

    [SuppressMessage("Security", "CA5350", Justification = "Official Minecraft asset indexes address their verified objects by SHA-1.")]
    private static Dictionary<string, byte[]> ReadRuntimeIcons(AppPaths paths, PreparedRuntime runtime, IVersion profile)
    {
        var icons = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var assets = SharedRuntimeStore.Assets(paths);
        var index = profile.GetInheritedProperty(version => version.AssetIndex?.Id);
        var indexPath = Path.Combine(assets, "indexes", (index ?? "") + ".json");
        if (!string.IsNullOrWhiteSpace(index) && File.Exists(indexPath))
        {
            paths.EnsureUnderRoot(indexPath);
            using var document = JsonDocument.Parse(File.ReadAllText(indexPath));
            if (document.RootElement.TryGetProperty("objects", out var objects))
            foreach (var name in IconNames)
            {
                if (!objects.TryGetProperty("icons/" + name, out var item) ||
                    !item.TryGetProperty("hash", out var hashNode)) continue;
                var hash = hashNode.GetString() ?? "";
                if (hash.Length != 40 || !hash.All(Uri.IsHexDigit)) continue;
                var path = Path.Combine(assets, "objects", hash[..2], hash);
                if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) continue;
                paths.EnsureUnderRoot(path);
                var bytes = File.ReadAllBytes(path);
                if (hash.Equals(Convert.ToHexString(SHA1.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
                    icons[name] = bytes;
            }
        }
        if (File.Exists(runtime.ClientJarPath))
        {
            paths.EnsureUnderRoot(runtime.ClientJarPath);
            using var client = File.OpenRead(runtime.ClientJarPath);
            ReadArchiveIcons(client, icons);
        }
        return icons;
    }

    private static void ReadArchiveIcons(Stream input, Dictionary<string, byte[]> icons)
    {
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var name in IconNames)
        {
            if (icons.ContainsKey(name)) continue;
            var entry = archive.GetEntry("icons/" + name) ?? archive.GetEntry("assets/minecraft/icons/" + name);
            if (entry is null || entry.Length is <= 0 or > 1024 * 1024) continue;
            using var source = entry.Open();
            using var copy = new MemoryStream();
            source.CopyTo(copy);
            icons[name] = copy.ToArray();
        }
    }
}
